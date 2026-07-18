package main

import (
	"sync"
	"time"
)

type sessionEntry struct {
	start time.Time
	added time.Time
}

// Tracker holds ephemeral in-memory state: playback start times keyed by
// session+item, and a dedup ledger of recently-notified user+item pairs.
// All state is lost on restart, which is acceptable for this use case.
type Tracker struct {
	mu       sync.Mutex
	sessions map[string]sessionEntry
	notified map[string]time.Time
	dedupTTL time.Duration
	now      func() time.Time
}

// NewTracker creates a Tracker with the given dedup window.
func NewTracker(dedupTTL time.Duration) *Tracker {
	return &Tracker{
		sessions: map[string]sessionEntry{},
		notified: map[string]time.Time{},
		dedupTTL: dedupTTL,
		now:      time.Now,
	}
}

// Start records (or overwrites) the playback start time for a session/item key.
func (t *Tracker) Start(key string, start time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.sessions[key] = sessionEntry{start: start, added: t.now()}
}

// TouchStart records a start time only if none is present yet, so a start is
// available even when the PlaybackStart event was missed.
func (t *Tracker) TouchStart(key string, start time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if _, ok := t.sessions[key]; !ok {
		t.sessions[key] = sessionEntry{start: start, added: t.now()}
	}
}

// PopStart returns and removes the recorded start time for a key.
func (t *Tracker) PopStart(key string) (time.Time, bool) {
	t.mu.Lock()
	defer t.mu.Unlock()
	e, ok := t.sessions[key]
	if ok {
		delete(t.sessions, key)
	}
	return e.start, ok
}

// MarkNotified records a notification and reports whether the caller should
// proceed. It returns false when an identical key was seen within the dedup TTL.
func (t *Tracker) MarkNotified(key string) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	now := t.now()
	if last, ok := t.notified[key]; ok && now.Sub(last) < t.dedupTTL {
		return false
	}
	t.notified[key] = now
	return true
}

// GC removes stale entries to prevent unbounded growth.
func (t *Tracker) GC() {
	t.mu.Lock()
	defer t.mu.Unlock()
	now := t.now()
	for k, e := range t.sessions {
		if now.Sub(e.added) > 24*time.Hour {
			delete(t.sessions, k)
		}
	}
	for k, ts := range t.notified {
		if now.Sub(ts) > t.dedupTTL {
			delete(t.notified, k)
		}
	}
}

// StartGC launches a background sweeper that runs until stop is closed.
func (t *Tracker) StartGC(stop <-chan struct{}) {
	go func() {
		ticker := time.NewTicker(30 * time.Minute)
		defer ticker.Stop()
		for {
			select {
			case <-stop:
				return
			case <-ticker.C:
				t.GC()
			}
		}
	}()
}
