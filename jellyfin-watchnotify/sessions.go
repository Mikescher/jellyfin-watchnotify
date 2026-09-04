package main

import (
	"sync"
	"time"
)

type sessionEntry struct {
	start    time.Time // earliest playback start time seen for this watch
	lastSeen time.Time // wall-clock time of the most recent event for this key
}

// Tracker holds ephemeral in-memory state: playback start times keyed by
// session+item, and a dedup ledger of recently-notified user+item pairs.
// All state is lost on restart, which is acceptable for this use case.
type Tracker struct {
	mu             sync.Mutex
	sessions       map[string]sessionEntry
	notified       map[string]time.Time
	dedupTTL       time.Duration
	coalesceWindow time.Duration
	now            func() time.Time
}

// NewTracker creates a Tracker with the given dedup and start-coalesce windows.
func NewTracker(dedupTTL, coalesceWindow time.Duration) *Tracker {
	return &Tracker{
		sessions:       map[string]sessionEntry{},
		notified:       map[string]time.Time{},
		dedupTTL:       dedupTTL,
		coalesceWindow: coalesceWindow,
		now:            time.Now,
	}
}

// Start records the playback start time for a session/item key. Delegates to
// record, so a fresh PlaybackStart during an active watch (e.g. a seek that
// restarts the stream) keeps the earliest start rather than resetting it.
func (t *Tracker) Start(key string, start time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.record(key, start)
}

// TouchStart records session activity from a progress event: it keeps a start
// available when PlaybackStart was missed, and refreshes lastSeen so an active
// watch stays "recent" and a later seek's PlaybackStart coalesces into it.
func (t *Tracker) TouchStart(key string, start time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.record(key, start)
}

// record folds an event into the session entry. If a recent entry exists (last
// activity within coalesceWindow), the earliest start is kept and lastSeen is
// refreshed; otherwise a new session begins, so a resume after a long gap is
// not glued to a stale start. Caller must hold t.mu.
func (t *Tracker) record(key string, start time.Time) {
	now := t.now()
	if e, ok := t.sessions[key]; ok && now.Sub(e.lastSeen) < t.coalesceWindow {
		if start.Before(e.start) {
			e.start = start
		}
		e.lastSeen = now
		t.sessions[key] = e
		return
	}
	t.sessions[key] = sessionEntry{start: start, lastSeen: now}
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
		if now.Sub(e.lastSeen) > 24*time.Hour {
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
