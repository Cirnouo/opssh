package main

import (
	"context"
	"errors"
	"testing"

	"opssh/internal/engine"
)

func TestActivityResultClassifiesFinalSignOutcomes(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want string
	}{
		{"published", nil, "published"},
		{"denied", engine.ErrDenied, "denied"},
		{"notification unavailable", engine.ErrUnavailable, "unavailable"},
		{"approval timeout", engine.ErrApprovalTimeout, "timeout"},
		{"provider timeout", engine.ErrLoadTimeout, "timeout"},
		{"deadline", context.DeadlineExceeded, "timeout"},
		{"canceled", context.Canceled, "canceled"},
		{"peer exit", engine.ErrPeerGone, "canceled"},
		{"retired", engine.ErrStale, "canceled"},
		{"provider failure", engine.ErrProvider, "failed"},
		{"publication failure", engine.ErrPublish, "failed"},
		{"unexpected failure", errors.New("fixture diagnostic"), "failed"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := activityResult(test.err); got != test.want {
				t.Fatalf("result = %q, want %q", got, test.want)
			}
		})
	}
}
