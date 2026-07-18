package main

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"sync"
	"time"
)

// retryInterval is the delay between dispatch attempts after a failed send.
const retryInterval = 30 * time.Second

// Server wires the webhook handler to the tracker and the action clients.
type Server struct {
	cfg     Config
	tracker *Tracker
	scn     *SCNClient
	joplin  *JoplinClient
	logger  *slog.Logger

	// ctx bounds the lifetime of background dispatch goroutines; cancelling it
	// (via Shutdown) makes their retry loops stop. wg tracks those goroutines.
	ctx    context.Context
	cancel context.CancelFunc
	wg     sync.WaitGroup
}

// NewServer builds a Server, enabling only the integrations that are configured.
func NewServer(cfg Config, logger *slog.Logger) *Server {
	ctx, cancel := context.WithCancel(context.Background())
	s := &Server{
		cfg:     cfg,
		tracker: NewTracker(cfg.DedupTTL),
		logger:  logger,
		ctx:     ctx,
		cancel:  cancel,
	}
	if cfg.SCN.Enabled() {
		s.scn = NewSCNClient(cfg.SCN, logger)
	}
	if cfg.Joplin.Enabled() {
		s.joplin = NewJoplinClient(cfg.Joplin, logger)
	}
	return s
}

// Shutdown cancels any in-flight dispatch retries and waits for their
// goroutines to exit. Notifications that never succeeded are abandoned.
func (s *Server) Shutdown() {
	s.cancel()
	s.wg.Wait()
}

func (s *Server) handleWebhook(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
	if err != nil {
		s.logger.Error("failed to read webhook body", "err", err)
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}

	var p WebhookPayload
	if err := json.Unmarshal(body, &p); err != nil {
		s.logger.Error("failed to parse webhook json", "err", err, "body", string(body))
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}

	s.logger.Info("webhook received",
		"type", p.NotificationType,
		"user", p.Username(),
		"item", p.ShortTitle(),
		"itemType", p.ItemType,
		"pos", fmtPct(p.WatchedFraction()),
		"device", p.DeviceName,
	)
	s.logger.Debug("webhook raw payload", "body", string(body))

	switch p.NotificationType {
	case "PlaybackStart":
		s.tracker.Start(p.SessionKey(), p.EventTime())
	case "PlaybackProgress":
		s.tracker.TouchStart(p.SessionKey(), p.EventTime())
	case "PlaybackStop":
		s.handleStop(p)
	case "SessionStart", "ItemAdded":
		// Subscribed for visibility; logged above, no action taken.
	default:
		s.logger.Debug("unhandled notification type", "type", p.NotificationType)
	}

	w.WriteHeader(http.StatusOK)
	io.WriteString(w, "ok")
}

// handleStop applies the "watched" logic and, if satisfied, dispatches actions.
func (s *Server) handleStop(p WebhookPayload) {
	if !p.IsMovie() && !p.IsEpisode() {
		s.logger.Info("stop ignored: not a movie/episode", "itemType", p.ItemType, "item", p.ShortTitle())
		return
	}

	frac := p.WatchedFraction()
	watched := p.PlayedToCompletion || frac >= s.cfg.WatchedThreshold
	if !watched {
		s.logger.Info("stop: not watched, skipping",
			"user", p.Username(), "item", p.ShortTitle(),
			"pct", fmtPct(frac), "threshold", fmtPct(s.cfg.WatchedThreshold),
			"playedToCompletion", p.PlayedToCompletion)
		return
	}

	if !s.tracker.MarkNotified(p.DedupKey()) {
		s.logger.Info("stop: duplicate within dedup window, skipping",
			"user", p.Username(), "item", p.ShortTitle())
		return
	}

	end := p.EventTime()
	dispFrac, pos, runtime := p.EffectiveProgress()

	start, ok := s.tracker.PopStart(p.SessionKey())
	if !ok {
		// No PlaybackStart recorded (e.g. process restarted mid-watch): estimate
		// the start from elapsed content, falling back to the stop time.
		if pos > 0 {
			start = end.Add(-pos)
		} else {
			start = end
		}
		s.logger.Debug("no recorded start time; using fallback", "item", p.ShortTitle(), "start", start.Format(timeLayout))
	}

	ev := WatchEvent{
		User:        p.Username(),
		Title:       p.ShortTitle(),
		JoplinTitle: p.JoplinTitle(),
		ItemType:    p.ItemType,
		Start:       start,
		End:         end,
		Fraction:    dispFrac,
		Position:    pos,
		Runtime:     runtime,
		Device:      p.DeviceName,
		Client:      p.ClientName,
	}

	s.logger.Info("watched",
		"user", ev.User, "item", ev.Title, "pct", fmtPct(ev.Fraction),
		"start", ev.Start.Format(timeLayout), "end", ev.End.Format(timeLayout))

	s.dispatch(p.Username(), ev)
}

// dispatch launches a background sender for each enabled integration whose
// allowlist includes the user. The webhook handler does not wait for delivery;
// each sender retries on its own until it succeeds or the server shuts down, so
// a slow or failing action never blocks the HTTP response nor the other action.
func (s *Server) dispatch(user string, ev WatchEvent) {
	if s.scn != nil {
		if s.cfg.SCN.Accounts.Contains(user) {
			s.sendWithRetry("scn", ev, s.scn.Send)
		} else {
			s.logger.Debug("scn skipped: user not in SCN_ACCOUNTS", "user", user)
		}
	}

	if s.joplin != nil {
		if s.cfg.Joplin.Accounts.Contains(user) {
			s.sendWithRetry("joplin", ev, s.joplin.Append)
		} else {
			s.logger.Debug("joplin skipped: user not in JOPLIN_ACCOUNTS", "user", user)
		}
	}
}

// sendWithRetry runs send in a background goroutine, retrying every
// retryInterval until it succeeds or the server context is cancelled (restart).
func (s *Server) sendWithRetry(action string, ev WatchEvent, send func(context.Context, WatchEvent) error) {
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		for attempt := 1; ; attempt++ {
			err := send(s.ctx, ev)
			if err == nil {
				if attempt > 1 {
					s.logger.Info("dispatch succeeded after retries",
						"action", action, "attempts", attempt, "user", ev.User, "item", ev.Title)
				}
				return
			}
			if s.ctx.Err() != nil {
				s.logger.Warn("dispatch abandoned: shutting down",
					"action", action, "user", ev.User, "item", ev.Title, "err", err)
				return
			}
			s.logger.Warn("dispatch failed, will retry",
				"action", action, "attempt", attempt, "retry_in", retryInterval.String(),
				"user", ev.User, "item", ev.Title, "err", err)
			select {
			case <-s.ctx.Done():
				return
			case <-time.After(retryInterval):
			}
		}
	}()
}
