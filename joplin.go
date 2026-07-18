package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// joplinTitleWidth is the fixed column width the watch-log title is padded to
// so the "// From Jellyfin [...]" suffix roughly lines up across lines.
const joplinTitleWidth = 45

// JoplinClient appends watch-log lines to a note via the custom Joplin API server.
type JoplinClient struct {
	cfg    JoplinConfig
	http   *http.Client
	logger *slog.Logger
}

// NewJoplinClient constructs a Joplin client.
func NewJoplinClient(cfg JoplinConfig, logger *slog.Logger) *JoplinClient {
	return &JoplinClient{
		cfg:    cfg,
		http:   &http.Client{Timeout: 10 * time.Minute},
		logger: logger,
	}
}

type joplinInsertRequest struct {
	Content  string `json:"content"`
	Search   string `json:"search"`
	Position string `json:"position"`
}

// Append inserts a watch-log line into the configured note, before the anchor.
func (c *JoplinClient) Append(ctx context.Context, ev WatchEvent) error {
	line := formatJoplinLine(ev)

	payload, err := json.Marshal(joplinInsertRequest{
		Content:  line,
		Search:   c.cfg.Anchor,
		Position: c.cfg.Position,
	})
	if err != nil {
		return err
	}

	endpoint := strings.TrimRight(c.cfg.BaseURL, "/") + "/notes/" + url.PathEscape(c.cfg.NoteID) + "/insert"
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(payload))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+c.cfg.Token)

	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("joplin returned %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}
	c.logger.Info("joplin line appended", "user", ev.User, "note", c.cfg.NoteID, "line", line, "status", resp.StatusCode)
	return nil
}

func formatJoplinLine(ev WatchEvent) string {
	ts := ev.Start.Format(timeLayout)
	title := padRight(ev.JoplinTitle, joplinTitleWidth)
	return fmt.Sprintf("[%s]: %s // From Jellyfin [%s]", ts, title, ev.User)
}
