// Package desktop adapts only the public 1Password Desktop SDK API.
package desktop

import (
	"context"
	"errors"
	"io/fs"
	"runtime"
	"sync/atomic"
	"time"

	onepassword "github.com/1password/onepassword-sdk-go"

	"opssh/internal/config"
)

var (
	ErrBusy           = errors.New("desktop call already in progress")
	ErrUnconfigured   = errors.New("desktop account is not configured")
	ErrUnavailable    = errors.New("desktop request failed")
	ErrSessionExpired = errors.New("desktop authorization expired")
	ErrAccessDenied   = errors.New("desktop access denied")
	ErrPublic         = errors.New("public key is unavailable or unsupported")
	ErrPrivateMissing = errors.New("private key field is missing")
	ErrPrivateInvalid = errors.New("private key field is invalid")
)

// SDKError maps vendor errors to safe categories before they cross the adapter.
func SDKError(err error) error {
	var expired *onepassword.DesktopSessionExpiredError
	if errors.As(err, &expired) {
		return ErrSessionExpired
	}
	switch {
	case errors.Is(err, context.DeadlineExceeded):
		return context.DeadlineExceeded
	case errors.Is(err, context.Canceled):
		return context.Canceled
	case errors.Is(err, fs.ErrPermission):
		return ErrAccessDenied
	}
	return ErrUnavailable
}

type resolver interface {
	Resolve(context.Context, string) (string, error)
}

type desktopClient struct{ client *onepassword.Client }

func (c desktopClient) Resolve(ctx context.Context, reference string) (string, error) {
	value, err := c.client.Secrets().Resolve(ctx, reference)
	// The SDK client finalizer releases the native client ID.
	runtime.KeepAlive(c.client)
	return value, err
}

type newClient func(context.Context, string) (resolver, error)

type observation struct {
	status     string
	lastStatus string
	at         time.Time
}

type Backend struct {
	account string
	init    newClient
	client  resolver // accessed only while owning work
	work    chan struct{}
	state   atomic.Pointer[observation]
}

func New(account string) *Backend {
	return newBackend(account, func(ctx context.Context, account string) (resolver, error) {
		client, err := onepassword.NewClient(ctx, onepassword.WithDesktopAppIntegration(account),
			onepassword.WithIntegrationInfo("opssh", "0.1.0"))
		if err != nil {
			return nil, err
		}
		return desktopClient{client}, nil
	})
}

func newBackend(account string, init newClient) *Backend {
	b := &Backend{account: account, init: init, work: make(chan struct{}, 1)}
	if account == "" {
		b.state.Store(&observation{status: "unconfigured"})
	} else {
		b.state.Store(&observation{status: "unknown"})
	}
	return b
}

func (b *Backend) Status() string { return b.state.Load().status }

func (b *Backend) Observation() (status, last string, at time.Time) {
	state := b.state.Load()
	return state.status, state.lastStatus, state.at
}

func (b *Backend) Public(ctx context.Context, vault, item string) ([]byte, error) {
	if !config.ValidID(vault) || !config.ValidID(item) {
		return nil, ErrUnavailable
	}
	return execute(ctx, b, func(client resolver) ([]byte, error) {
		value, err := client.Resolve(ctx, "op://"+vault+"/"+item+"/public key")
		if err != nil {
			return nil, SDKError(err)
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		return parsePublic(value)
	}, func(buf []byte) { clear(buf) })
}

func (b *Backend) Private(ctx context.Context, vault, item string) ([]byte, error) {
	if !config.ValidID(vault) || !config.ValidID(item) {
		return nil, ErrUnavailable
	}
	return execute(ctx, b, func(client resolver) ([]byte, error) {
		bulk, ok := client.(batchResolver)
		if !ok {
			return nil, ErrUnavailable
		}
		// A single-reference ResolveAll preserves structured field errors that
		// Secrets.Resolve returns only as text.
		ref := "op://" + vault + "/" + item + "/private key?ssh-format=openssh"
		response, err := bulk.ResolveAll(ctx, []string{ref})
		if err != nil {
			return nil, SDKError(err)
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		value := response.IndividualResponses[ref]
		if len(response.IndividualResponses) != 1 {
			return nil, ErrUnavailable
		}
		if value.Error != nil {
			if value.Content != nil {
				return nil, ErrUnavailable
			}
			switch value.Error.Type {
			case onepassword.ResolveReferenceErrorTypeVariantFieldNotFound:
				return nil, ErrPrivateMissing
			case onepassword.ResolveReferenceErrorTypeVariantTooManyMatchingFields,
				onepassword.ResolveReferenceErrorTypeVariantSSHKeyMetadataNotFound,
				onepassword.ResolveReferenceErrorTypeVariantIncompatibleSSHKeyQueryParameterField,
				onepassword.ResolveReferenceErrorTypeVariantUnableToParsePrivateKey,
				onepassword.ResolveReferenceErrorTypeVariantUnableToFormatPrivateKeyToOpenSSH:
				return nil, ErrPrivateInvalid
			default:
				return nil, ErrUnavailable
			}
		}
		if value.Content == nil || value.Content.VaultID != vault || value.Content.ItemID != item {
			return nil, ErrUnavailable
		}
		switch size := len(value.Content.Secret); {
		case size == 0:
			return nil, ErrPrivateMissing
		case size > 64*1024:
			return nil, ErrPrivateInvalid
		default:
			return []byte(value.Content.Secret), nil
		}
	}, func(buf []byte) { clear(buf) })
}

func execute[T any](ctx context.Context, b *Backend, read func(resolver) (T, error), discard func(T)) (T, error) {
	var zero T
	if b.account == "" {
		return zero, ErrUnconfigured
	}
	if err := ctx.Err(); err != nil {
		return zero, err
	}
	select {
	case b.work <- struct{}{}:
	default:
		return zero, ErrBusy
	}
	prior := b.state.Load()
	reading := &observation{status: "reading", lastStatus: prior.lastStatus, at: prior.at}
	b.state.Store(reading)
	var invoked atomic.Bool
	setStatus := func(status string) {
		result := &observation{status: status, lastStatus: reading.lastStatus, at: reading.at}
		if invoked.Load() {
			result.lastStatus = status
			result.at = time.Now().UTC()
		}
		b.state.CompareAndSwap(reading, result)
	}
	cancelStatus := func() {
		if errors.Is(ctx.Err(), context.DeadlineExceeded) {
			setStatus("timed-out")
		} else {
			setStatus("canceled")
		}
	}
	type result struct {
		value T
		err   error
	}
	response := make(chan result)
	go func() {
		out := func() result {
			if err := ctx.Err(); err != nil {
				return result{err: err}
			}
			invoked.Store(true)
			if b.client == nil {
				client, err := b.init(ctx, b.account)
				if err != nil {
					return result{err: SDKError(err)}
				}
				if err := ctx.Err(); err != nil {
					return result{err: err}
				}
				b.client = client
			}
			value, err := read(b.client)
			return result{value, err}
		}()
		// Publish the actual observation before releasing the native slot.
		// A new reader can inherit this outcome before this waiter resumes.
		switch {
		case ctx.Err() != nil:
			cancelStatus()
		case errors.Is(out.err, ErrSessionExpired):
			setStatus("expired")
		case errors.Is(out.err, ErrAccessDenied):
			setStatus("denied")
		case errors.Is(out.err, context.DeadlineExceeded):
			setStatus("timed-out")
		case errors.Is(out.err, context.Canceled):
			setStatus("canceled")
		case out.err == nil, errors.Is(out.err, ErrScopeTooBroad), errors.Is(out.err, ErrScanLimit),
			errors.Is(out.err, ErrPrivateMissing), errors.Is(out.err, ErrPrivateInvalid):
			setStatus("last-success")
		default:
			setStatus("unavailable")
		}
		<-b.work
		select {
		case response <- out:
		case <-ctx.Done():
			if discard != nil {
				discard(out.value)
			}
		}
	}()
	select {
	case <-ctx.Done():
		cancelStatus()
		return zero, ctx.Err()
	case out := <-response:
		if err := ctx.Err(); err != nil {
			if discard != nil {
				discard(out.value)
			}
			cancelStatus()
			return zero, err
		}
		return out.value, out.err
	}
}
