package engine

import (
	"context"
	"errors"

	"opssh/internal/diagnostics"
)

func requestOutcome(err error) diagnostics.Outcome {
	switch err {
	case nil:
		return "accepted"
	case ErrBusy:
		return "busy"
	case ErrUnknownKey:
		return "unknown-key"
	case ErrUnsupportedFlags:
		return "unsupported-flags"
	case ErrLocked:
		return "locked"
	case ErrClosed, ErrStale:
		return "stale"
	case ErrPeerGone, context.Canceled, context.DeadlineExceeded:
		return "canceled"
	default:
		return "invalid"
	}
}

func approvalOutcome(err error) diagnostics.Outcome {
	switch err {
	case nil:
		return "allowed"
	case ErrDenied:
		return "denied"
	case ErrUnavailable:
		return "unavailable"
	case ErrApprovalTimeout:
		return "timeout"
	case ErrBusy:
		return "busy"
	default:
		return interruptedOutcome(err)
	}
}

func interruptedOutcome(err error) diagnostics.Outcome {
	if err == ErrPeerGone || err == context.Canceled || err == context.DeadlineExceeded {
		return "canceled"
	}
	return "stale"
}

func waitOutcome(err error) diagnostics.Outcome {
	switch err {
	case nil:
		return "ready"
	case ErrLoadTimeout, context.DeadlineExceeded:
		return "timeout"
	case context.Canceled:
		return "canceled"
	case ErrClosed, ErrLocked, ErrStale:
		return "stale"
	default:
		return "failed"
	}
}

// Only known categories can leave the provider boundary. Opaque errors can
// contain key material, references, or other secret-bearing data.
func providerResult(err error) (error, diagnostics.Outcome) {
	switch {
	case err == nil:
		return nil, "resolved"
	case errors.Is(err, ErrProviderExpired):
		return ErrProviderExpired, "session-expired"
	case errors.Is(err, ErrProviderDenied):
		return ErrProviderDenied, "denied"
	case errors.Is(err, ErrProviderBusy):
		return ErrProviderBusy, "busy"
	case errors.Is(err, ErrPrivateMissing):
		return ErrPrivateMissing, "field-not-found"
	case errors.Is(err, ErrPrivateEmpty):
		return ErrPrivateEmpty, "empty"
	case errors.Is(err, ErrPrivateInvalid):
		return ErrPrivateInvalid, "invalid"
	case errors.Is(err, context.DeadlineExceeded):
		return context.DeadlineExceeded, "timeout"
	case errors.Is(err, context.Canceled):
		return context.Canceled, "canceled"
	default:
		return ErrProvider, "unavailable"
	}
}
