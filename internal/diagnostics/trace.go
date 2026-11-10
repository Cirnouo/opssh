package diagnostics

import (
	"context"
	"slices"
	"time"
)

type Stage string

const (
	Peer         Stage = "peer"
	Request      Stage = "request"
	Approval     Stage = "approval"
	KeyCache     Stage = "key-cache"
	MaterialWait Stage = "material-wait"
	ProviderRead Stage = "provider-read"
	PrivateParse Stage = "private-parse"
	PublicMatch  Stage = "public-match"
	Crypto       Stage = "crypto"
	Publish      Stage = "publish"
)

// Outcome is a fixed category chosen at a stage boundary. Never use error text.
type Outcome string

type Step struct {
	Stage     Stage   `json:"stage"`
	Outcome   Outcome `json:"outcome"`
	ElapsedMs int64   `json:"elapsedMs"`
}

// Trace is owned by one request or material worker. A worker publishes its
// completed trace with its result, allowing active waiters to copy the steps.
type Trace struct {
	epoch uint64
	steps [9]Step
	count int
}

type traceKey struct{}

func WithTrace(ctx context.Context, trace *Trace) context.Context {
	if trace == nil {
		return ctx
	}
	return context.WithValue(ctx, traceKey{}, trace)
}

func FromContext(ctx context.Context) *Trace {
	trace, _ := ctx.Value(traceKey{}).(*Trace)
	return trace
}

func (t *Trace) Start() time.Time {
	if t == nil {
		return time.Time{}
	}
	return time.Now()
}

func (t *Trace) Add(stage Stage, outcome Outcome, start time.Time) {
	if t == nil || t.count == len(t.steps) {
		return
	}
	var elapsed int64
	if !start.IsZero() {
		elapsed = min(time.Since(start), maxElapsed).Milliseconds()
	}
	t.steps[t.count] = Step{Stage: stage, Outcome: outcome, ElapsedMs: elapsed}
	t.count++
}

func (t *Trace) Fork() *Trace {
	if t == nil {
		return nil
	}
	return &Trace{epoch: t.epoch}
}

func (t *Trace) Merge(other *Trace) bool {
	if t == nil || other == nil || t.epoch != other.epoch || other.count == 0 || other.count > len(t.steps)-t.count {
		return false
	}
	t.count += copy(t.steps[t.count:], other.steps[:other.count])
	return true
}

func (t *Trace) Epoch() uint64 {
	return t.epoch
}

func (t *Trace) Steps() []Step {
	if t == nil || t.count == 0 {
		return nil
	}
	return slices.Clone(t.steps[:t.count])
}
