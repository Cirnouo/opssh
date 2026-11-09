package main

import (
	"context"
	"errors"

	"opssh/internal/engine"
)

func activityResult(err error) string {
	switch {
	case err == nil:
		return "published"
	case errors.Is(err, engine.ErrDenied):
		return "denied"
	case errors.Is(err, context.DeadlineExceeded), errors.Is(err, engine.ErrApprovalTimeout),
		errors.Is(err, engine.ErrLoadTimeout):
		return "timeout"
	case errors.Is(err, context.Canceled), errors.Is(err, engine.ErrPeerGone),
		errors.Is(err, engine.ErrStale), errors.Is(err, engine.ErrClosed), errors.Is(err, engine.ErrLocked):
		return "canceled"
	default:
		return "failed"
	}
}
