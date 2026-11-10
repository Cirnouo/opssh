package diagnostics

import (
	"slices"
	"sync"
	"time"
)

const (
	capacity   = 128
	batchSize  = 32
	maxElapsed = 24 * time.Hour
)

type Event struct {
	Sequence  uint64    `json:"sequence"`
	Time      time.Time `json:"time"`
	Result    string    `json:"result"`
	ElapsedMs int64     `json:"elapsedMs"`
	Steps     []Step    `json:"steps,omitempty"`
}

type Batch struct {
	Epoch  uint64  `json:"epoch"`
	Events []Event `json:"events"`
}

type Ring struct {
	mu       sync.Mutex
	enabled  bool
	epoch    uint64
	sequence uint64
	events   [capacity]Event
}

type Attempt struct {
	start time.Time
	trace *Trace
}

func (a Attempt) Trace() *Trace { return a.trace }

// Sync updates consent and returns a bounded, retryable snapshot.
func (r *Ring) Sync(enabled bool, epoch, after uint64) (Batch, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if epoch == 0 || epoch < r.epoch || (epoch == r.epoch && enabled != r.enabled) {
		return Batch{}, false
	}
	if epoch != r.epoch {
		if after != 0 {
			return Batch{}, false
		}
		clear(r.events[:])
		r.epoch, r.enabled, r.sequence = epoch, enabled, 0
	}
	if after > r.sequence {
		return Batch{}, false
	}
	batch := Batch{Epoch: r.epoch, Events: []Event{}}
	if !r.enabled {
		return batch, true
	}
	start := after + 1
	if r.sequence >= capacity {
		start = max(start, r.sequence-capacity+1)
	}
	for seq := start; seq <= r.sequence && len(batch.Events) < batchSize; seq++ {
		event := r.events[(seq-1)%capacity]
		event.Steps = slices.Clone(event.Steps)
		batch.Events = append(batch.Events, event)
	}
	return batch, true
}

// Signing can drop an observation when the collector is busy.
func (r *Ring) Begin() Attempt {
	if r == nil || !r.mu.TryLock() {
		return Attempt{}
	}
	defer r.mu.Unlock()
	if !r.enabled {
		return Attempt{}
	}
	return Attempt{start: time.Now(), trace: &Trace{epoch: r.epoch}}
}

func (r *Ring) Finish(attempt Attempt, result string) {
	if r == nil || attempt.trace == nil || !r.mu.TryLock() {
		return
	}
	defer r.mu.Unlock()
	if attempt.trace.epoch != r.epoch || !r.enabled {
		return
	}
	switch result {
	case "published", "denied", "unavailable", "timeout", "canceled", "failed":
	default:
		return
	}
	now := time.Now()
	r.sequence++
	r.events[(r.sequence-1)%capacity] = Event{
		Sequence: r.sequence, Time: now.UTC(), Result: result,
		ElapsedMs: min(now.Sub(attempt.start), maxElapsed).Milliseconds(), Steps: attempt.trace.Steps(),
	}
}
