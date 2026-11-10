package diagnostics

import (
	"reflect"
	"testing"
)

func TestRingOptInAndGeneration(t *testing.T) {
	var ring Ring
	before := ring.Begin()
	if _, ok := ring.Sync(true, 1, 0); !ok {
		t.Fatal("enable diagnostics")
	}
	ring.Finish(before, "published")
	old := ring.Begin()
	if _, ok := ring.Sync(true, 2, 0); !ok {
		t.Fatal("clear diagnostics")
	}
	ring.Finish(old, "published")
	ring.Finish(ring.Begin(), "arbitrary input")
	ring.Finish(ring.Begin(), "unavailable")
	batch, ok := ring.Sync(true, 2, 0)
	if !ok || batch.Epoch != 2 || len(batch.Events) != 1 || batch.Events[0].Result != "unavailable" {
		t.Fatalf("only the current allowlisted event should survive: %+v", batch)
	}
	if _, ok := ring.Sync(true, 1, 0); ok {
		t.Fatal("stale configuration was applied")
	}
	if batch, ok := ring.Sync(false, 3, 0); !ok || len(batch.Events) != 0 {
		t.Fatal("disabling diagnostics clears pending events")
	}
}

func TestRingReturnsBoundedRetryableSnapshots(t *testing.T) {
	var ring Ring
	ring.Sync(true, 1, 0)
	for range 200 {
		ring.Finish(ring.Begin(), "published")
	}
	first, ok := ring.Sync(true, 1, 0)
	if !ok || len(first.Events) != 32 || first.Events[0].Sequence != 73 {
		t.Fatalf("expected the oldest 32 of 128 retained events, got %+v", first)
	}
	retry, _ := ring.Sync(true, 1, 0)
	if !reflect.DeepEqual(retry.Events[0], first.Events[0]) {
		t.Fatal("reading a snapshot discarded events")
	}
	next, _ := ring.Sync(true, 1, first.Events[31].Sequence)
	if len(next.Events) != 32 || next.Events[0].Sequence != 105 {
		t.Fatal("snapshot cursor did not advance")
	}
}
