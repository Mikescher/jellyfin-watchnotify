package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// Config holds the full runtime configuration, parsed from environment variables.
type Config struct {
	Port      string
	LogLevel  string
	LogFormat string

	WatchedThreshold float64
	DedupTTL         time.Duration

	SCN    SCNConfig
	Joplin JoplinConfig
}

// SCNConfig configures the Simple Cloud Notifier push integration.
type SCNConfig struct {
	BaseURL  string
	UserID   string
	Key      string
	Channel  string
	Priority int
	Accounts AccountSet
}

// Enabled reports whether the SCN integration has the credentials it needs.
func (c SCNConfig) Enabled() bool { return c.UserID != "" && c.Key != "" }

// JoplinConfig configures the Joplin watch-log integration.
type JoplinConfig struct {
	BaseURL  string
	Token    string
	NoteID   string
	Anchor   string
	Position string
	Accounts AccountSet
}

// Enabled reports whether the Joplin integration has everything it needs.
func (c JoplinConfig) Enabled() bool {
	return c.Token != "" && c.NoteID != "" && !c.Accounts.Empty()
}

// AccountSet represents an allowlist of Jellyfin usernames, or the special
// "ALL" value that matches every user.
type AccountSet struct {
	all   bool
	names map[string]bool
}

func parseAccounts(s string) AccountSet {
	s = strings.TrimSpace(s)
	if s == "" {
		return AccountSet{}
	}
	if strings.EqualFold(s, "ALL") {
		return AccountSet{all: true}
	}
	names := map[string]bool{}
	for _, part := range strings.Split(s, ",") {
		if p := strings.TrimSpace(part); p != "" {
			names[strings.ToLower(p)] = true
		}
	}
	return AccountSet{names: names}
}

// Empty reports whether the set matches nobody.
func (a AccountSet) Empty() bool { return !a.all && len(a.names) == 0 }

// Contains reports whether the given username is allowed.
func (a AccountSet) Contains(user string) bool {
	if a.all {
		return true
	}
	return a.names[strings.ToLower(user)]
}

// LoadConfig reads configuration from the environment, applying defaults.
func LoadConfig() (Config, error) {
	c := Config{
		Port:      getEnv("PORT", "80"),
		LogLevel:  getEnv("LOG_LEVEL", "info"),
		LogFormat: getEnv("LOG_FORMAT", "text"),
	}

	threshold, err := strconv.ParseFloat(getEnv("WATCHED_THRESHOLD", "0.90"), 64)
	if err != nil {
		return Config{}, fmt.Errorf("invalid WATCHED_THRESHOLD: %w", err)
	}
	c.WatchedThreshold = threshold

	ttl, err := time.ParseDuration(getEnv("DEDUP_TTL", "6h"))
	if err != nil {
		return Config{}, fmt.Errorf("invalid DEDUP_TTL: %w", err)
	}
	c.DedupTTL = ttl

	priority, err := strconv.Atoi(getEnv("SCN_PRIORITY", "1"))
	if err != nil {
		return Config{}, fmt.Errorf("invalid SCN_PRIORITY: %w", err)
	}

	c.SCN = SCNConfig{
		BaseURL:  getEnv("SCN_BASE_URL", "https://simplecloudnotifier.blackforestbytes.com/"),
		UserID:   os.Getenv("SCN_USER_ID"),
		Key:      os.Getenv("SCN_KEY"),
		Channel:  os.Getenv("SCN_CHANNEL"),
		Priority: priority,
		Accounts: parseAccounts(getEnv("SCN_ACCOUNTS", "ALL")),
	}

	c.Joplin = JoplinConfig{
		BaseURL:  getEnv("JOPLIN_BASE_URL", "http://10.8.0.4:4466"),
		Token:    os.Getenv("JOPLIN_TOKEN"),
		NoteID:   os.Getenv("JOPLIN_NOTE_ID"),
		Anchor:   os.Getenv("JOPLIN_ANCHOR"),
		Position: getEnv("JOPLIN_POSITION", "before"),
		Accounts: parseAccounts(os.Getenv("JOPLIN_ACCOUNTS")),
	}

	return c, nil
}

func getEnv(key, def string) string {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		return v
	}
	return def
}
