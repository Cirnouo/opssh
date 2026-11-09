package engine

import (
	"bytes"
	"context"
	"crypto/dsa"
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/rsa"
	"errors"
	"math/big"
	"time"

	"golang.org/x/crypto/ssh"
)

type keyMaterial struct {
	signer ssh.AlgorithmSigner
	wipe   func()
}

func (material *keyMaterial) dispose() {
	if material.signer != nil {
		material.wipe()
		material.signer = nil
		material.wipe = nil
	}
}

type loadFlight struct {
	entry     *keyEntry
	key       Key
	ctx       context.Context
	cancel    context.CancelFunc
	done      chan struct{}
	waiters   int
	completed bool
	transient bool
	material  *keyMaterial
	err       error
}

type materialSnapshot struct {
	entry    *keyEntry
	revision uint64
}

func (e *Engine) Preload(ctx context.Context, keyID string) error {
	if !reserve(e.requests) {
		return ErrBusy
	}
	defer func() { <-e.requests }()
	e.mu.Lock()
	entry := e.state[keyID]
	if err := e.keyErrorLocked(entry); err != nil {
		e.mu.Unlock()
		return err
	}
	snapshot := materialSnapshot{entry: entry, revision: entry.materialRevision}
	e.mu.Unlock()
	return e.preload(ctx, snapshot)
}

// PreloadAll loads the catalog in order using one request slot. Capturing
// every revision before the first load prevents a batch from restoring
// material evicted while an earlier key was loading.
func (e *Engine) PreloadAll(ctx context.Context) error {
	if !reserve(e.requests) {
		return ErrBusy
	}
	defer func() { <-e.requests }()
	e.mu.Lock()
	if err := e.indexErrorLocked(); err != nil {
		e.mu.Unlock()
		return err
	}
	keys := make([]materialSnapshot, 0, len(e.keys.entries))
	for _, key := range e.keys.entries {
		entry := e.state[key.key.ID]
		keys = append(keys, materialSnapshot{entry: entry, revision: entry.materialRevision})
	}
	e.mu.Unlock()
	var firstErr error
	for _, snapshot := range keys {
		if ctx.Err() != nil {
			break
		}
		if err := e.preload(ctx, snapshot); err != nil && firstErr == nil {
			firstErr = err
		}
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	return firstErr
}

func (e *Engine) preload(ctx context.Context, snapshot materialSnapshot) error {
	_, flight, err := e.getMaterial(ctx, snapshot.entry, nil, snapshot.revision)
	if flight != nil {
		e.releaseFlight(flight)
	}
	return err
}

func (e *Engine) getMaterial(ctx context.Context, entry *keyEntry, version context.Context, revision uint64) (*keyMaterial, *loadFlight, error) {
	e.mu.Lock()
	if err := e.checkMaterialLocked(ctx, entry, version, revision); err != nil {
		e.mu.Unlock()
		return nil, nil, err
	}
	entry.pruneLocked(e.now())
	if material := entry.material; material != nil {
		e.mu.Unlock()
		return material, nil, nil
	}
	flight := entry.flight
	if flight == nil {
		if !reserve(e.loads) {
			e.mu.Unlock()
			return nil, nil, ErrBusy
		}
		loadCtx, cancel := context.WithTimeout(context.Background(), e.loadTimeout)
		flight = &loadFlight{entry: entry, key: entry.key, ctx: loadCtx, cancel: cancel, done: make(chan struct{})}
		e.flights[flight] = struct{}{}
		entry.flight = flight
		go e.load(flight)
	}
	flight.waiters++
	e.mu.Unlock()

	select {
	case <-ctx.Done():
	case <-flight.ctx.Done():
	case <-flight.done:
	}

	e.mu.Lock()
	defer e.mu.Unlock()
	if err := e.checkMaterialLocked(ctx, entry, version, revision); err != nil {
		return nil, flight, err
	}
	if !flight.completed {
		e.finishFlightLocked(flight, nil, ErrLoadTimeout)
	}
	return flight.material, flight, flight.err
}

func (e *Engine) checkMaterialLocked(ctx context.Context, entry *keyEntry, version context.Context, revision uint64) error {
	if err := e.keyErrorLocked(entry); err != nil {
		return err
	}
	if entry.materialRevision != revision || (version != nil && entry.signVersion != version) {
		return ErrStale
	}
	return ctx.Err()
}

func (e *Engine) load(flight *loadFlight) {
	defer func() { <-e.loads }()
	if flight.ctx.Err() != nil {
		return
	}
	material, err := e.fetch(flight.ctx, flight.key)
	e.mu.Lock()
	defer e.mu.Unlock()
	if flight.completed {
		if material != nil {
			material.dispose()
		}
		return
	}
	if errors.Is(flight.ctx.Err(), context.DeadlineExceeded) {
		if material != nil {
			material.dispose()
		}
		material, err = nil, ErrLoadTimeout
	}
	e.finishFlightLocked(flight, material, err)
}

func (e *Engine) fetch(ctx context.Context, key Key) (*keyMaterial, error) {
	if e.provider == nil {
		return nil, ErrProvider
	}
	private, err := e.provider.Load(ctx, cloneKey(key))
	defer clear(private)
	if err != nil || ctx.Err() != nil {
		return nil, ErrProvider
	}
	return parsePrivateKey(private, key.Public)
}

func (e *Engine) finishFlightLocked(flight *loadFlight, material *keyMaterial, err error) {
	entry := flight.entry
	entry.flight = nil
	flight.completed, flight.material, flight.err = true, material, err
	entry.loadError = loadStatus(err)
	if material != nil {
		if e.keyPolicy.Mode == "off" {
			flight.transient = true
		} else {
			entry.material = material
			entry.keyExpiry = e.keyPolicy.expiry(e.now())
		}
	}
	if !flight.transient {
		delete(e.flights, flight)
	}
	flight.cancel()
	close(flight.done)
}

func (e *Engine) releaseFlight(flight *loadFlight) {
	e.mu.Lock()
	defer e.mu.Unlock()
	flight.waiters--
	if flight.waiters == 0 {
		e.discardFlightLocked(flight)
	}
}

func (e *Engine) discardFlightLocked(flight *loadFlight) {
	delete(e.flights, flight)
	if flight.entry.flight == flight {
		flight.entry.flight = nil
	}
	if !flight.completed {
		flight.completed, flight.err = true, ErrStale
		close(flight.done)
	}
	if flight.transient && flight.material != nil {
		flight.material.dispose()
		flight.material = nil
	}
	flight.cancel()
}

func (e *Engine) clearMaterialLocked(entry *keyEntry) {
	entry.materialRevision++
	if entry.material != nil {
		entry.material.dispose()
		entry.material = nil
	}
	entry.keyExpiry = time.Time{}
	entry.loadError = ""
	for flight := range e.flights {
		if flight.entry == entry {
			e.discardFlightLocked(flight)
		}
	}
}

func loadStatus(err error) string {
	switch err {
	case nil:
		return ""
	case ErrLoadTimeout:
		return "load-timeout"
	case ErrInvalidKey:
		return "invalid-key"
	case ErrKeyMismatch:
		return "key-mismatch"
	default:
		return "provider"
	}
}

func validPublicKey(public ssh.PublicKey) bool {
	key, ok := public.(ssh.CryptoPublicKey)
	if !ok {
		return false
	}
	switch raw := key.CryptoPublicKey().(type) {
	case ed25519.PublicKey:
		return public.Type() == ssh.KeyAlgoED25519 && len(raw) == ed25519.PublicKeySize
	case *rsa.PublicKey:
		return public.Type() == ssh.KeyAlgoRSA && validRSA(raw)
	default:
		return false
	}
}

func validRSA(public *rsa.PublicKey) bool {
	return public.N != nil && public.N.Sign() > 0 && public.N.BitLen() >= 2048 && public.N.BitLen() <= 4096
}

func signatureAlgorithm(algorithm string, flags uint32) (string, error) {
	const (
		rsaSHA256 = 2
		rsaSHA512 = 4
	)
	switch algorithm {
	case ssh.KeyAlgoED25519:
		if flags == 0 {
			return ssh.KeyAlgoED25519, nil
		}
	case ssh.KeyAlgoRSA:
		switch flags {
		case rsaSHA256:
			return ssh.KeyAlgoRSASHA256, nil
		case rsaSHA512:
			return ssh.KeyAlgoRSASHA512, nil
		}
	}
	return "", ErrUnsupportedFlags
}

func parsePrivateKey(pemBytes, expectedPublic []byte) (*keyMaterial, error) {
	if len(pemBytes) == 0 || len(pemBytes) > maxPrivateKey {
		return nil, ErrInvalidKey
	}
	private, err := ssh.ParseRawPrivateKey(pemBytes)
	if err != nil {
		return nil, ErrInvalidKey
	}
	wipe := func() { wipePrivateKey(private) }
	keep := false
	defer func() {
		if !keep {
			wipe()
		}
	}()
	switch key := private.(type) {
	case ed25519.PrivateKey:
		if !validEd25519(key) {
			return nil, ErrInvalidKey
		}
	case *ed25519.PrivateKey:
		if !validEd25519(*key) {
			return nil, ErrInvalidKey
		}
	case *rsa.PrivateKey:
		if !validRSA(&key.PublicKey) || key.Validate() != nil {
			return nil, ErrInvalidKey
		}
	default:
		return nil, ErrInvalidKey
	}
	signer, err := ssh.NewSignerFromKey(private)
	if err != nil {
		return nil, ErrInvalidKey
	}
	algorithmSigner, ok := signer.(ssh.AlgorithmSigner)
	if !ok {
		return nil, ErrInvalidKey
	}
	if !bytes.Equal(signer.PublicKey().Marshal(), expectedPublic) {
		return nil, ErrKeyMismatch
	}
	keep = true
	return &keyMaterial{signer: algorithmSigner, wipe: wipe}, nil
}

func validEd25519(key ed25519.PrivateKey) bool {
	if len(key) != ed25519.PrivateKeySize {
		return false
	}
	derived := ed25519.NewKeyFromSeed(key[:ed25519.SeedSize])
	defer clear(derived)
	return bytes.Equal(key, derived)
}

// Wipe the private buffers exposed by the standard key types before dropping
// references. The Go runtime and cryptographic parsers can hold internal copies.
func wipePrivateKey(private any) {
	switch key := private.(type) {
	case ed25519.PrivateKey:
		clear(key)
	case *ed25519.PrivateKey:
		clear(*key)
		*key = nil
	case *rsa.PrivateKey:
		wipeInt(key.D)
		for _, prime := range key.Primes {
			wipeInt(prime)
		}
		wipeInt(key.Precomputed.Dp)
		wipeInt(key.Precomputed.Dq)
		wipeInt(key.Precomputed.Qinv)
		for _, crt := range key.Precomputed.CRTValues {
			wipeInt(crt.Exp)
			wipeInt(crt.Coeff)
			wipeInt(crt.R)
		}
		*key = rsa.PrivateKey{}
	case *ecdsa.PrivateKey:
		wipeInt(key.D)
	case *dsa.PrivateKey:
		wipeInt(key.X)
	}
}

func wipeInt(integer *big.Int) {
	if integer != nil {
		clear(integer.Bits())
	}
}
