package main

import (
	"fmt"
	"strings"
	"time"
)

// ticksPerSecond is the .NET tick resolution used by Jellyfin (1 tick = 100ns).
const ticksPerSecond = 10_000_000

// timeLayout is the human-readable timestamp format used in notifications and
// the Joplin watch log.
const timeLayout = "2006-01-02 15:04:05"

// WebhookPayload is the subset of the Jellyfin "Send All Properties" JSON we
// care about. The payload is a flat PascalCase object; every field may be
// absent depending on the event and item type, so we decode defensively.
type WebhookPayload struct {
	NotificationType string `json:"NotificationType"`

	UserId               string `json:"UserId"`
	NotificationUsername string `json:"NotificationUsername"`

	ItemId        string `json:"ItemId"`
	ItemType      string `json:"ItemType"`
	Name          string `json:"Name"`
	SeriesName    string `json:"SeriesName"`
	SeasonNumber  *int   `json:"SeasonNumber"`
	EpisodeNumber *int   `json:"EpisodeNumber"`
	Year          *int   `json:"Year"`

	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	RunTimeTicks          int64 `json:"RunTimeTicks"`
	PlayedToCompletion    bool  `json:"PlayedToCompletion"`

	Timestamp    string `json:"Timestamp"`
	UtcTimestamp string `json:"UtcTimestamp"`

	SessionId   string `json:"Id"`
	DeviceName  string `json:"DeviceName"`
	ClientName  string `json:"ClientName"`
	PlayMethod  string `json:"PlayMethod"`
	IsPaused    bool   `json:"IsPaused"`
	IsAutomated bool   `json:"IsAutomated"`
}

// WatchEvent is the normalized data derived from a completed-playback payload
// that the SCN and Joplin actions consume.
type WatchEvent struct {
	User        string
	Title       string // short title for SCN, e.g. "The Matrix (1999)" / "Firefly S01E02"
	JoplinTitle string // watch-log title, e.g. "The Matrix" / "Firefly [S01E02]"
	ItemType    string
	Start       time.Time
	End         time.Time
	Fraction    float64
	Position    time.Duration
	Runtime     time.Duration
	Device      string
	Client      string
}

// Username returns the best available display name for the watching user.
func (p WebhookPayload) Username() string {
	if p.NotificationUsername != "" {
		return p.NotificationUsername
	}
	return p.UserId
}

func (p WebhookPayload) IsMovie() bool   { return strings.EqualFold(p.ItemType, "Movie") }
func (p WebhookPayload) IsEpisode() bool { return strings.EqualFold(p.ItemType, "Episode") }

// WatchedFraction returns the fraction of the item that was played (0..1).
func (p WebhookPayload) WatchedFraction() float64 {
	if p.RunTimeTicks <= 0 {
		return 0
	}
	return float64(p.PlaybackPositionTicks) / float64(p.RunTimeTicks)
}

// SessionKey identifies a playback session+item for start-time tracking.
func (p WebhookPayload) SessionKey() string {
	id := p.SessionId
	if id == "" {
		id = p.UserId
	}
	return id + "|" + p.ItemId
}

// DedupKey identifies a user+item pair for duplicate suppression.
func (p WebhookPayload) DedupKey() string {
	return p.UserId + "|" + p.ItemId
}

func (p WebhookPayload) seasonEpisodeTag() string {
	s, e := 0, 0
	if p.SeasonNumber != nil {
		s = *p.SeasonNumber
	}
	if p.EpisodeNumber != nil {
		e = *p.EpisodeNumber
	}
	return fmt.Sprintf("S%02dE%02d", s, e)
}

// ShortTitle renders the title used in SCN push notifications.
func (p WebhookPayload) ShortTitle() string {
	switch {
	case p.IsEpisode():
		return strings.TrimSpace(fmt.Sprintf("%s %s", p.SeriesName, p.seasonEpisodeTag()))
	case p.Year != nil && *p.Year > 0:
		return fmt.Sprintf("%s (%d)", p.Name, *p.Year)
	default:
		return p.Name
	}
}

// JoplinTitle renders the title used in the Joplin watch-log line.
func (p WebhookPayload) JoplinTitle() string {
	if p.IsEpisode() {
		return strings.TrimSpace(fmt.Sprintf("%s [%s]", p.SeriesName, p.seasonEpisodeTag()))
	}
	return p.Name
}

// EventTime returns the event time, preferring the server-local Timestamp,
// then UtcTimestamp, then the current time.
func (p WebhookPayload) EventTime() time.Time {
	if t, ok := parseJellyfinTime(p.Timestamp); ok {
		return t
	}
	if t, ok := parseJellyfinTime(p.UtcTimestamp); ok {
		return t
	}
	return time.Now()
}

func parseJellyfinTime(s string) (time.Time, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return time.Time{}, false
	}
	layouts := []string{
		time.RFC3339Nano,
		time.RFC3339,
		"2006-01-02T15:04:05.9999999",
		"2006-01-02T15:04:05",
		"2006-01-02 15:04:05",
	}
	for _, layout := range layouts {
		if t, err := time.Parse(layout, s); err == nil {
			return t, true
		}
	}
	return time.Time{}, false
}

func ticksToDuration(ticks int64) time.Duration {
	if ticks <= 0 {
		return 0
	}
	return time.Duration(ticks/ticksPerSecond) * time.Second
}

func formatDuration(d time.Duration) string {
	d = d.Round(time.Second)
	h := d / time.Hour
	d -= h * time.Hour
	m := d / time.Minute
	d -= m * time.Minute
	s := d / time.Second
	if h > 0 {
		return fmt.Sprintf("%dh%02dm%02ds", h, m, s)
	}
	return fmt.Sprintf("%dm%02ds", m, s)
}
