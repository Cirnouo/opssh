// Package desktop adapts only the public 1Password Desktop SDK API.
package desktop

import (
	"context"
	"errors"
	"runtime"
	"sync/atomic"

	onepassword "github.com/1password/onepassword-sdk-go"
	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/engine"
)

var (
	ErrBusy         = errors.New("desktop call already in progress")
	ErrUnconfigured = errors.New("desktop account is not configured")
	ErrUnavailable  = errors.New("desktop request failed")
	ErrPublic       = errors.New("public key is unavailable or unsupported")
)

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

type observation struct{ status string }

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
		b.state.Store(&observation{"unconfigured"})
	} else {
		b.state.Store(&observation{"unknown"})
	}
	return b
}

func (b *Backend) Status() string { return b.state.Load().status }

func (b *Backend) Public(ctx context.Context, vault, item string) ([]byte, error) {
	return b.resolve(ctx, vault, item, "public key", func(value string) ([]byte, error) {
		if len(value) > 8192 {
			return nil, ErrPublic
		}
		buf := []byte(value)
		defer clear(buf)
		key, _, options, rest, err := ssh.ParseAuthorizedKey(buf)
		if err != nil || len(options) > 0 || len(rest) > 0 {
			return nil, ErrPublic
		}
		blob := key.Marshal()
		if !engine.ValidPublic(blob) {
			return nil, ErrPublic
		}
		return blob, nil
	})
}

func (b *Backend) Private(ctx context.Context, vault, item string) ([]byte, error) {
	return b.resolve(ctx, vault, item, "private key?ssh-format=openssh", func(value string) ([]byte, error) {
		if len(value) == 0 || len(value) > 64*1024 {
			return nil, ErrUnavailable
		}
		return []byte(value), nil
	})
}

type result struct {
	bytes []byte
	err   error
}

func (b *Backend) resolve(ctx context.Context, vault, item, field string, convert func(string) ([]byte, error)) ([]byte, error) {
	if b.account == "" {
		return nil, ErrUnconfigured
	}
	if !config.ValidID(vault) || !config.ValidID(item) {
		return nil, ErrUnavailable
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	select {
	case b.work <- struct{}{}:
	default:
		return nil, ErrBusy
	}
	reading := &observation{"reading"}
	b.state.Store(reading)
	setStatus := func(status string) { b.state.CompareAndSwap(reading, &observation{status}) }
	cancelStatus := func() {
		if errors.Is(ctx.Err(), context.DeadlineExceeded) {
			setStatus("timed-out")
		} else {
			setStatus("canceled")
		}
	}
	response := make(chan result)
	go func() {
		defer func() { <-b.work }()
		if ctx.Err() != nil {
			return
		}
		var out result
		client := b.client
		var err error
		if client == nil {
			client, err = b.init(ctx, b.account)
			if err == nil {
				b.client = client
			}
		}
		if err == nil && ctx.Err() == nil {
			var value string
			value, err = client.Resolve(ctx, "op://"+vault+"/"+item+"/"+field)
			if err == nil && ctx.Err() == nil {
				out.bytes, out.err = convert(value)
			}
		}
		if err != nil {
			out.err = ErrUnavailable
		}
		select {
		case response <- out:
		case <-ctx.Done():
			clear(out.bytes)
		}
	}()
	select {
	case <-ctx.Done():
		cancelStatus()
		return nil, ctx.Err()
	case out := <-response:
		if err := ctx.Err(); err != nil {
			clear(out.bytes)
			cancelStatus()
			return nil, err
		}
		if out.err != nil {
			setStatus("unavailable")
		} else {
			setStatus("last-success")
		}
		return out.bytes, out.err
	}
}
