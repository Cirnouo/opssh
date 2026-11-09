package activity

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"testing"
	"time"
)

type fakeSource struct {
	read   func(context.Context) string
	closed chan struct{}
}

func (f *fakeSource) CommandLine(ctx context.Context) string { return f.read(ctx) }
func (f *fakeSource) Close()                                 { close(f.closed) }

func waitFor(t *testing.T, signal <-chan struct{}) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(5 * time.Second):
		t.Fatal("activity worker did not complete")
	}
}

func TestActivityPageKeepsItsCursorAcrossInserts(t *testing.T) {
	log := New()
	t.Cleanup(log.Close)
	for pid := uint32(1); pid <= 35; pid++ {
		log.Begin().Finish(Entry{PID: pid, Result: "published"})
	}
	first := log.Page("")
	if first.Total != 35 || len(first.Entries) != 32 || first.Entries[0].PID != 35 ||
		first.Entries[31].PID != 4 || first.Next == "" {
		t.Fatalf("unexpected first activity page: %+v", first)
	}

	log.Begin().Finish(Entry{PID: 36, Result: "denied"})
	second := log.Page(first.Next)
	if second.Total != 36 || len(second.Entries) != 3 || second.Entries[0].PID != 3 ||
		second.Entries[2].PID != 1 || second.Next != "" {
		t.Fatalf("unexpected second activity page: %+v", second)
	}
}

func TestCommandIsAvailableOnlyByEntryID(t *testing.T) {
	log := New()
	t.Cleanup(log.Close)
	const raw = "fixture.exe \"first second\"\r\n--flag=✓"
	source := &fakeSource{read: func(context.Context) string { return raw }, closed: make(chan struct{})}
	attempt := log.Begin()
	attempt.Capture(func() (Source, bool) { return source, true })
	attempt.Finish(Entry{App: "C:\\fixture-private\\fixture.exe", PID: 72, Key: "Local key", Result: "published"})
	waitFor(t, source.closed)

	page := log.Page("")
	if len(page.Entries) != 1 {
		t.Fatalf("activity count = %d, want 1", len(page.Entries))
	}
	entry := page.Entries[0]
	if entry.App != "fixture.exe" || entry.PID != 72 || entry.Key != "Local key" ||
		entry.Time.IsZero() || entry.ID == "" || !entry.CommandReady {
		t.Fatalf("unexpected activity metadata: %+v", entry)
	}
	wire, err := json.Marshal(page)
	if err != nil || strings.Contains(string(wire), "first second") || strings.Contains(string(wire), "fixture-private") {
		t.Fatal("activity page exposed command arguments or a full path")
	}
	if log.Command(entry.ID) != raw || log.Command(entry.ID+"x") != "" {
		t.Fatal("command detail did not preserve its text or ID binding")
	}

	for pid := uint32(1); pid <= 200; pid++ {
		log.Begin().Finish(Entry{PID: pid, Key: strconv.Itoa(int(pid)), Result: "failed"})
	}
	if page := log.Page(""); page.Total != 200 || page.Entries[0].PID != 200 {
		t.Fatal("activity did not keep the latest 200 outcomes")
	}
	if log.Command(entry.ID) != "" || len(log.Page(entry.ID).Entries) != 0 {
		t.Fatal("evicted activity still exposed its command or cursor")
	}
}

func TestClearRetiresInFlightAndUnfinishedActivity(t *testing.T) {
	log := New()
	t.Cleanup(log.Close)
	started := make(chan struct{})
	canceled := make(chan struct{})
	source := &fakeSource{
		read: func(ctx context.Context) string {
			close(started)
			<-ctx.Done()
			close(canceled)
			return "late fixture result"
		},
		closed: make(chan struct{}),
	}
	attempt := log.Begin()
	attempt.Capture(func() (Source, bool) { return source, true })
	waitFor(t, started)
	attempt.Finish(Entry{PID: 10, Result: "published"})
	oldID := log.Page("").Entries[0].ID
	unfinished := log.Begin()

	log.Clear()
	waitFor(t, canceled)
	waitFor(t, source.closed)
	unfinished.Finish(Entry{PID: 11, Result: "denied"})
	log.Begin().Finish(Entry{PID: 12, Result: "canceled"})

	if page := log.Page(""); page.Total != 1 || page.Entries[0].PID != 12 {
		t.Fatal("cleared activity republished after completion")
	}
	if log.Command(oldID) != "" {
		t.Fatal("cleared activity accepted a late command")
	}
}

func TestCommandCaptureSaturationStillRecordsOutcomes(t *testing.T) {
	log := New()
	t.Cleanup(log.Close)
	sources := make([]*fakeSource, 2)
	for i := range sources {
		sources[i] = &fakeSource{
			read: func(ctx context.Context) string {
				<-ctx.Done()
				return ""
			},
			closed: make(chan struct{}),
		}
		attempt := log.Begin()
		source := sources[i]
		attempt.Capture(func() (Source, bool) { return source, true })
		attempt.Finish(Entry{Result: "published"})
	}
	overflow := log.Begin()
	overflow.Capture(func() (Source, bool) {
		t.Error("activity acquired a requester beyond the worker bound")
		return nil, false
	})
	overflow.Finish(Entry{Result: "timeout"})
	page := log.Page("")
	if len(page.Entries) != 3 || page.Entries[0].Result != "timeout" || page.Entries[0].CommandReady {
		t.Fatal("metadata was lost while command capture was saturated")
	}

	log.Clear()
	for _, source := range sources {
		waitFor(t, source.closed)
	}
}
