package main

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"time"
)

// Server wires the webhook handler to the tracker and the action clients.
type Server struct {
	cfg     Config
	tracker *Tracker
	scn     *SCNClient
	joplin  *JoplinClient
	logger  *slog.Logger
}

// NewServer builds a Server, enabling only the integrations that are configured.
func NewServer(cfg Config, logger *slog.Logger) *Server {
	s := &Server{
		cfg:     cfg,
		tracker: NewTracker(cfg.DedupTTL),
		logger:  logger,
	}
	if cfg.SCN.Enabled() {
		s.scn = NewSCNClient(cfg.SCN, logger)
	}
	if cfg.Joplin.Enabled() {
		s.joplin = NewJoplinClient(cfg.Joplin, logger)
	}
	return s
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
	start, ok := s.tracker.PopStart(p.SessionKey())
	if !ok {
		// No PlaybackStart recorded (e.g. process restarted mid-watch): estimate
		// the start from elapsed content, falling back to the stop time.
		if p.PlaybackPositionTicks > 0 {
			start = end.Add(-ticksToDuration(p.PlaybackPositionTicks))
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
		Fraction:    frac,
		Position:    ticksToDuration(p.PlaybackPositionTicks),
		Runtime:     ticksToDuration(p.RunTimeTicks),
		Device:      p.DeviceName,
		Client:      p.ClientName,
	}

	s.logger.Info("watched",
		"user", ev.User, "item", ev.Title, "pct", fmtPct(frac),
		"start", ev.Start.Format(timeLayout), "end", ev.End.Format(timeLayout))

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	s.dispatch(ctx, p.Username(), ev)
}

// dispatch runs the SCN and Joplin actions; each is guarded by its allowlist,
// and a failure in one does not prevent the other.
func (s *Server) dispatch(ctx context.Context, user string, ev WatchEvent) {
	if s.scn != nil {
		if s.cfg.SCN.Accounts.Contains(user) {
			if err := s.scn.Send(ctx, ev); err != nil {
				s.logger.Error("scn send failed", "err", err, "user", user, "item", ev.Title)
			}
		} else {
			s.logger.Debug("scn skipped: user not in SCN_ACCOUNTS", "user", user)
		}
	}

	if s.joplin != nil {
		if s.cfg.Joplin.Accounts.Contains(user) {
			if err := s.joplin.Append(ctx, ev); err != nil {
				s.logger.Error("joplin append failed", "err", err, "user", user, "item", ev.Title)
			}
		} else {
			s.logger.Debug("joplin skipped: user not in JOPLIN_ACCOUNTS", "user", user)
		}
	}
}
