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

	"opssh/internal/diagnostics"
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
	waiters   map[*loadWaiter]struct{}
	completed bool
	transient bool
	material  *keyMaterial
	err       error
	trace     *diagnostics.Trace
}

type loadWaiter struct {
	flight   *loadFlight
	ctx      context.Context
	version  context.Context // nil for an explicit preload
	revision uint64
}

type materialSnapshot struct {
	entry    *keyEntry
	revision uint64
}

type PreloadReport struct {
	Selected      int  `json:"selected"`
	Loaded        int  `json:"loaded"`
	AlreadyLoaded int  `json:"alreadyLoaded"`
	Failed        int  `json:"failed"`
	Skipped       int  `json:"skipped"`
	Interrupted   bool `json:"-"`
}

func (report *PreloadReport) record(alreadyLoaded bool, err error) {
	switch {
	case err != nil:
		report.Failed++
	case alreadyLoaded:
		report.AlreadyLoaded++
	default:
		report.Loaded++
	}
}

func (e *Engine) Preload(ctx context.Context, keyID string) (PreloadReport, error) {
	trace := diagnostics.FromContext(ctx)
	requestStart := trace.Start()
	report := PreloadReport{Selected: 1}
	if !reserve(e.requests) {
		trace.Add(diagnostics.Request, "busy", requestStart)
		report.record(false, ErrBusy)
		return report, ErrBusy
	}
	defer func() { <-e.requests }()
	e.mu.Lock()
	entry := e.state[keyID]
	if err := e.keyErrorLocked(entry); err != nil {
		e.mu.Unlock()
		trace.Add(diagnostics.Request, requestOutcome(err), requestStart)
		report.record(false, err)
		return report, err
	}
	snapshot := materialSnapshot{entry: entry, revision: entry.materialRevision}
	e.mu.Unlock()
	trace.Add(diagnostics.Request, "accepted", requestStart)
	alreadyLoaded, err := e.preload(ctx, snapshot, trace)
	report.record(alreadyLoaded, err)
	return report, err
}

// PreloadAll loads the catalog in order using one request slot. Capturing
// every revision before the first load prevents a batch from restoring
// material evicted while an earlier key was loading.
func (e *Engine) PreloadAll(ctx context.Context) (PreloadReport, error) {
	trace := diagnostics.FromContext(ctx)
	requestStart := trace.Start()
	if !reserve(e.requests) {
		trace.Add(diagnostics.Request, "busy", requestStart)
		return PreloadReport{}, ErrBusy
	}
	defer func() { <-e.requests }()
	e.mu.Lock()
	if err := e.indexErrorLocked(); err != nil {
		e.mu.Unlock()
		trace.Add(diagnostics.Request, requestOutcome(err), requestStart)
		return PreloadReport{}, err
	}
	keys := make([]materialSnapshot, 0, len(e.keys.entries))
	for _, key := range e.keys.entries {
		entry := e.state[key.key.ID]
		keys = append(keys, materialSnapshot{entry: entry, revision: entry.materialRevision})
	}
	e.mu.Unlock()
	trace.Add(diagnostics.Request, "accepted", requestStart)
	report := PreloadReport{Selected: len(keys)}
	var firstErr error
	for i, snapshot := range keys {
		if err := ctx.Err(); err != nil {
			report.Skipped = len(keys) - i
			report.Interrupted = true
			return report, err
		}
		var keyTrace *diagnostics.Trace
		if firstErr == nil {
			keyTrace = trace.Fork()
		}
		alreadyLoaded, err := e.preload(ctx, snapshot, keyTrace)
		report.record(alreadyLoaded, err)
		if err != nil && firstErr == nil {
			firstErr = err
			trace.Merge(keyTrace)
		}
	}
	if err := ctx.Err(); err != nil {
		report.Interrupted = true
		return report, err
	}
	return report, firstErr
}

func (e *Engine) preload(ctx context.Context, snapshot materialSnapshot, trace *diagnostics.Trace) (alreadyLoaded bool, err error) {
	material, waiter, err := e.getMaterial(ctx, snapshot.entry, nil, snapshot.revision, trace)
	if waiter != nil {
		e.releaseFlight(waiter, err)
	}
	return material != nil && waiter == nil, err
}

func (e *Engine) getMaterial(ctx context.Context, entry *keyEntry, version context.Context, revision uint64, trace *diagnostics.Trace) (*keyMaterial, *loadWaiter, error) {
	cacheStart := trace.Start()
	e.mu.Lock()
	if err := e.checkMaterialLocked(ctx, entry, version, revision); err != nil {
		e.mu.Unlock()
		trace.Add(diagnostics.KeyCache, interruptedOutcome(err), cacheStart)
		return nil, nil, err
	}
	entry.pruneLocked(e.now())
	if material := entry.material; material != nil {
		e.mu.Unlock()
		trace.Add(diagnostics.KeyCache, "hit", cacheStart)
		return material, nil, nil
	}
	flight := entry.flight
	if flight == nil {
		if !reserve(e.loads) {
			entry.loadError = LoadErrorCode(ErrBusy)
			e.mu.Unlock()
			trace.Add(diagnostics.KeyCache, "busy", cacheStart)
			return nil, nil, ErrBusy
		}
		loadCtx, cancel := context.WithTimeout(context.Background(), e.loadTimeout)
		flight = &loadFlight{entry: entry, key: entry.key, ctx: loadCtx, cancel: cancel, done: make(chan struct{}), waiters: make(map[*loadWaiter]struct{})}
		e.flights[flight] = struct{}{}
		entry.flight = flight
		trace.Add(diagnostics.KeyCache, "miss", cacheStart)
		go e.load(flight, trace.Fork())
	} else {
		trace.Add(diagnostics.KeyCache, "joined", cacheStart)
	}
	waiter := &loadWaiter{flight: flight, ctx: ctx, version: version, revision: revision}
	flight.waiters[waiter] = struct{}{}
	waitStart := trace.Start()
	e.mu.Unlock()

	select {
	case <-ctx.Done():
	case <-flight.ctx.Done():
	case <-flight.done:
	}

	e.mu.Lock()
	defer e.mu.Unlock()
	if err := e.checkMaterialLocked(ctx, entry, version, revision); err != nil {
		trace.Add(diagnostics.MaterialWait, waitOutcome(err), waitStart)
		return nil, waiter, err
	}
	if !flight.completed {
		e.finishFlightLocked(flight, nil, ErrLoadTimeout)
	}
	if !trace.Merge(flight.trace) {
		trace.Add(diagnostics.MaterialWait, waitOutcome(flight.err), waitStart)
	}
	return flight.material, waiter, flight.err
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

func (e *Engine) load(flight *loadFlight, trace *diagnostics.Trace) {
	defer func() { <-e.loads }()
	if flight.ctx.Err() != nil {
		return
	}
	material, err := e.fetch(flight.ctx, flight.key, trace)
	e.mu.Lock()
	defer e.mu.Unlock()
	if flight.completed || e.discardInactiveFlightLocked(flight) {
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
	} else {
		flight.trace = trace
	}
	e.finishFlightLocked(flight, material, err)
}

// A provider can finish after its waiters cancel and before they acquire mu
// to release the flight. Check the current waiters before publishing a result.
func (e *Engine) discardInactiveFlightLocked(flight *loadFlight) bool {
	var interruptedPreload error
	for waiter := range flight.waiters {
		err := e.checkMaterialLocked(waiter.ctx, flight.entry, waiter.version, waiter.revision)
		if err == nil {
			return false
		}
		if waiter.version == nil && (err == context.DeadlineExceeded || (err == context.Canceled && interruptedPreload == nil)) {
			interruptedPreload = err
		}
	}
	if interruptedPreload != nil {
		flight.entry.loadError = LoadErrorCode(interruptedPreload)
	}
	e.discardFlightLocked(flight)
	return true
}

func (e *Engine) fetch(ctx context.Context, key Key, trace *diagnostics.Trace) (*keyMaterial, error) {
	started := trace.Start()
	if e.provider == nil {
		trace.Add(diagnostics.ProviderRead, "unavailable", started)
		return nil, ErrProvider
	}
	private, err := e.provider.Load(ctx, cloneKey(key))
	defer clear(private)
	if canceled := ctx.Err(); canceled != nil {
		err = canceled
	}
	err, outcome := providerResult(err)
	trace.Add(diagnostics.ProviderRead, outcome, started)
	if err != nil {
		return nil, err
	}
	return parsePrivateKey(private, key.Public, trace)
}

func (e *Engine) finishFlightLocked(flight *loadFlight, material *keyMaterial, err error) {
	entry := flight.entry
	entry.flight = nil
	flight.completed, flight.material, flight.err = true, material, err
	entry.loadError = LoadErrorCode(err)
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

func (e *Engine) releaseFlight(waiter *loadWaiter, err error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	flight := waiter.flight
	delete(flight.waiters, waiter)
	if len(flight.waiters) == 0 {
		if waiter.version == nil && !flight.completed && flight.entry.flight == flight && (err == context.Canceled || err == context.DeadlineExceeded) {
			flight.entry.loadError = LoadErrorCode(err)
		}
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

func LoadErrorCode(err error) string {
	switch err {
	case nil:
		return ""
	case ErrProviderExpired:
		return "desktop-session-expired"
	case ErrProviderDenied:
		return "desktop-access-denied"
	case ErrProviderBusy:
		return "desktop-busy"
	case ErrPrivateMissing, ErrPrivateEmpty:
		return "private-field-missing"
	case ErrPrivateInvalid:
		return "private-field-invalid"
	case ErrLoadTimeout, context.DeadlineExceeded:
		return "provider-timeout"
	case context.Canceled:
		return "canceled"
	case ErrStale, ErrClosed:
		return "stale"
	case ErrBusy:
		return "busy"
	case ErrInvalidKey:
		return "invalid-key"
	case ErrKeyMismatch:
		return "key-mismatch"
	default:
		return "private-lookup"
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

func parsePrivateKey(pemBytes, expectedPublic []byte, trace *diagnostics.Trace) (_ *keyMaterial, resultErr error) {
	parseStart := trace.Start()
	defer func() {
		if resultErr == ErrInvalidKey {
			trace.Add(diagnostics.PrivateParse, "invalid", parseStart)
		}
	}()
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
	trace.Add(diagnostics.PrivateParse, "parsed", parseStart)
	matchStart := trace.Start()
	if !bytes.Equal(signer.PublicKey().Marshal(), expectedPublic) {
		trace.Add(diagnostics.PublicMatch, "mismatch", matchStart)
		return nil, ErrKeyMismatch
	}
	trace.Add(diagnostics.PublicMatch, "matched", matchStart)
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
