package main

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// SCNClient sends push notifications via the Simple Cloud Notifier API.
type SCNClient struct {
	cfg    SCNConfig
	http   *http.Client
	logger *slog.Logger
}

// NewSCNClient constructs an SCN client.
func NewSCNClient(cfg SCNConfig, logger *slog.Logger) *SCNClient {
	return &SCNClient{
		cfg:    cfg,
		http:   &http.Client{Timeout: 15 * time.Second},
		logger: logger,
	}
}

// Send posts a watched-notification to SCN as application/x-www-form-urlencoded.
func (c *SCNClient) Send(ctx context.Context, ev WatchEvent) error {
	title := ev.SCNTitle

	device := ev.Device
	if ev.Client != "" {
		if device != "" {
			device += " · " + ev.Client
		} else {
			device = ev.Client
		}
	}

	var b strings.Builder
	fmt.Fprintf(&b, "What:    %s\n", ev.Title)
	if ev.EpisodeName != "" {
		// Second row, indented to align with the "What:" value above.
		fmt.Fprintf(&b, "         %s\n", ev.EpisodeName)
	}
	fmt.Fprintf(&b, "Who:     %s\n", ev.User)
	fmt.Fprintf(&b, "Started: %s\n", ev.Start.Format(timeLayout))
	fmt.Fprintf(&b, "Ended:   %s\n", ev.End.Format(timeLayout))
	fmt.Fprintf(&b, "Watched: %s (%s / %s)\n", fmtPct(ev.Fraction), formatDuration(ev.Position), formatDuration(ev.Runtime))
	if device != "" {
		fmt.Fprintf(&b, "Device:  %s\n", device)
	}

	form := url.Values{}
	form.Set("user_id", c.cfg.UserID)
	form.Set("key", c.cfg.Key)
	form.Set("title", title)
	form.Set("content", b.String())
	form.Set("priority", strconv.Itoa(c.cfg.Priority))
	form.Set("sender_name", "jellyfin-watchnotify")
	form.Set("timestamp", strconv.FormatInt(ev.Start.Unix(), 10))
	if id := newUUID(); id != "" {
		form.Set("msg_id", id)
	}
	if c.cfg.Channel != "" {
		form.Set("channel", c.cfg.Channel)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.cfg.BaseURL, strings.NewReader(form.Encode()))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("scn returned %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}
	c.logger.Info("scn notification sent", "user", ev.User, "title", ev.Title, "status", resp.StatusCode)
	return nil
}
