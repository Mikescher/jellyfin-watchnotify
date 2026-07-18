package main

import (
	"crypto/rand"
	"fmt"
	"strings"
	"unicode/utf8"
)

// newUUID returns a random RFC-4122 v4 UUID string, or "" on failure.
func newUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return ""
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// padRight left-justifies s within the given rune width.
func padRight(s string, width int) string {
	if n := utf8.RuneCountInString(s); n < width {
		return s + strings.Repeat(" ", width-n)
	}
	return s
}

// fmtPct formats a 0..1 fraction as a whole-percent string, e.g. "95%".
func fmtPct(f float64) string {
	return fmt.Sprintf("%.0f%%", f*100)
}
