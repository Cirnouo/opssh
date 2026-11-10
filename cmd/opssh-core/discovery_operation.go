package main

import (
	"context"
	"encoding/hex"
	"errors"
	"sync"

	"opssh/internal/control"
)

var errDiscoveryCanceled = errors.New("discovery canceled and prior index restored")

type discoveryRequest struct {
	OperationID string `json:"operationId"`
}

func parseDiscoveryRequest(msg control.Message) (string, error) {
	var body discoveryRequest
	if control.DecodeBody(msg, &body) != nil || len(body.OperationID) != 32 {
		return "", errCommand
	}
	if _, err := hex.DecodeString(body.OperationID); err != nil {
		return "", errCommand
	}
	return body.OperationID, nil
}

type discoveryProgress struct {
	Phase     string `json:"phase"`
	Completed int    `json:"completed"`
	Total     int    `json:"total"`
}

type discoveryOperation struct {
	id     string
	ctx    context.Context
	cancel context.CancelCauseFunc

	mu        sync.Mutex
	progress  discoveryProgress
	finishing bool
}

func (op *discoveryOperation) report(phase string, completed, total int) {
	op.mu.Lock()
	defer op.mu.Unlock()
	if !op.finishing && op.ctx.Err() == nil {
		op.progress = discoveryProgress{Phase: phase, Completed: completed, Total: total}
	}
}

// Commit and cancel share a boundary. Once the worker starts publishing, the
// reader must not acknowledge cancellation of that result.
func (op *discoveryOperation) finish() error {
	op.mu.Lock()
	defer op.mu.Unlock()
	op.finishing = true
	return op.ctx.Err()
}

func (a *application) discover(ctx context.Context, msg control.Message) error {
	var id string
	if len(msg.Body) != 0 {
		var err error
		id, err = parseDiscoveryRequest(msg)
		if err != nil {
			return err
		}
	}
	deadline, stop := context.WithTimeout(ctx, indexTimeout)
	defer stop()
	scan, cancel := context.WithCancelCause(deadline)
	defer cancel(nil)
	op := &discoveryOperation{id: id, ctx: scan, cancel: cancel, progress: discoveryProgress{Phase: "vaults"}}
	a.discovery.Store(op)
	defer a.discovery.Store(nil)
	return a.scanIndex(scan, a.copySettings(), op)
}

func (a *application) discoveryControl(msg control.Message) control.Result {
	id, err := parseDiscoveryRequest(msg)
	if err != nil {
		return commandResult(err)
	}
	if op := a.discovery.Load(); op != nil && id == op.id {
		op.mu.Lock()
		defer op.mu.Unlock()
		if !op.finishing {
			if msg.Type == "discovery-progress" {
				return control.Result{OK: true, Data: op.progress}
			}
			if op.ctx.Err() == nil {
				op.cancel(errDiscoveryCanceled)
				return control.Result{OK: true}
			}
		}
	}
	if msg.Type == "discovery-progress" {
		return control.Result{OK: true}
	}
	return control.Result{ErrorCode: "discovery-not-active", Error: "This discovery request is no longer active."}
}
