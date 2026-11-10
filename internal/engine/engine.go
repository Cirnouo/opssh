// Package engine owns the public key index, approval grants, and private signing state.
package engine

import (
	"bytes"
	"context"
	"errors"
	"sync"
	"time"

	"golang.org/x/crypto/ssh"
)

var (
	ErrLocked           = errors.New("engine is locked")
	ErrClosed           = errors.New("engine is closed")
	ErrUnknownKey       = errors.New("unknown key")
	ErrUnknownGrant     = errors.New("unknown grant")
	ErrInvalidKey       = errors.New("invalid key")
	ErrInvalidPolicy    = errors.New("invalid cache policy")
	ErrInvalidRequest   = errors.New("invalid request")
	ErrUnsupportedFlags = errors.New("unsupported signature flags")
	ErrPeerGone         = errors.New("requesting process or connection ended")
	ErrDenied           = errors.New("approval denied")
	ErrUnavailable      = errors.New("approval notification unavailable")
	ErrApprovalTimeout  = errors.New("approval timed out")
	ErrLoadTimeout      = errors.New("key load timed out")
	ErrProvider         = errors.New("key provider failed")
	ErrProviderExpired  = errors.New("key provider authorization expired")
	ErrProviderDenied   = errors.New("key provider access denied")
	ErrProviderBusy     = errors.New("key provider is busy")
	ErrPrivateMissing   = errors.New("private key field is missing")
	ErrPrivateInvalid   = errors.New("private key field is invalid")
	ErrKeyMismatch      = errors.New("private key does not match the index")
	ErrBusy             = errors.New("engine is busy")
	ErrStale            = errors.New("request is stale")
	ErrPublish          = errors.New("signature publication failed")
)

const (
	maxKeys       = 256
	maxPublicKey  = 1024
	maxKeyField   = 1024
	maxGrants     = 64 // per key
	maxRequests   = 64
	maxApprovals  = 32
	maxLoads      = 8
	maxPeerChain  = 32
	maxPathBytes  = 32768 * 4 // maximum UTF-8 size of a Windows path
	maxSignData   = 256 << 10
	maxPrivateKey = 64 << 10
)

type Key struct {
	ID, VaultID, ItemID, Name string
	Public                    []byte
}

type Instance struct {
	PID     uint32
	Created uint64
}

type Process struct {
	Instance
	Path string
}

// Peer contains the original requester followed by its verified ancestors.
// Its callbacks make bounded, synchronous checks against the retained handles.
type Peer struct {
	Chain     []Process
	Logon     [32]byte // opaque identity supplied by the verified transport
	Alive     func(Instance) bool
	Connected func() bool
	// ApprovalApp returns the index of a verified window owner in Chain, or
	// false when the prompt should fall back to the requester.
	ApprovalApp func(context.Context) (index int, ownsWindow bool)
	// Retain transfers an independent liveness check and its cleanup to a
	// cached grant. Both functions must be bounded and synchronous.
	Retain func(Instance) (alive func() bool, release func(), ok bool)
}

type Provider interface {
	// Load transfers ownership of the returned PEM buffer, even on error.
	// Known failure categories use the engine's provider errors or context errors.
	Load(context.Context, Key) ([]byte, error)
}

type Approval struct {
	Scope     string
	Candidate int
}

type Prompt struct {
	Key           Key
	Chain         []Process
	AppCandidate  int
	Deadline      time.Time
	CacheAllowed  bool
	appOwnsWindow bool
}

type Approver func(context.Context, Prompt) (Approval, error)

type Policy struct {
	Mode string
	TTL  time.Duration
}

type Options struct {
	Keys            []Key
	KeyCache        Policy
	ApprovalCache   Policy
	ApprovalTimeout time.Duration
	LoadTimeout     time.Duration
	Now             func() time.Time
	Provider        Provider
	Approve         Approver
}

func DefaultOptions() Options {
	return Options{
		KeyCache:        Policy{Mode: "fixed", TTL: 5 * time.Minute},
		ApprovalCache:   Policy{Mode: "fixed", TTL: 10 * time.Minute},
		ApprovalTimeout: time.Minute,
		LoadTimeout:     30 * time.Second,
		Now:             time.Now,
	}
}

type State struct {
	Locked  bool
	Loaded  map[string]bool
	Loading map[string]bool
	Errors  map[string]string
	Grants  map[string]int
}

type indexedKey struct {
	key       Key
	algorithm string
}

type keyEntry struct {
	*indexedKey
	signVersion      context.Context
	cancelSign       context.CancelFunc
	materialRevision uint64
	material         *keyMaterial
	keyExpiry        time.Time
	loadError        string
	flight           *loadFlight
	grants           map[grantID]*grant
}

type catalog struct {
	byID     map[string]*indexedKey
	byPublic map[string]*indexedKey
	entries  []*indexedKey
}

type Engine struct {
	// mu serializes mutations and the final sign/publish operation. A provider
	// or approver callback never runs under mu.
	mu             sync.Mutex
	keys           catalog
	state          map[string]*keyEntry
	keyPolicy      Policy
	approvalPolicy Policy
	locked         bool
	suspended      bool
	closed         bool
	flights        map[*loadFlight]struct{}
	stop           chan struct{}
	done           chan struct{}

	now             func() time.Time
	provider        Provider
	approve         Approver
	approvalTimeout time.Duration
	loadTimeout     time.Duration
	requests        chan struct{}
	approvals       chan struct{}
	loads           chan struct{}
}

func New(opts Options) (*Engine, error) {
	defaults := DefaultOptions()
	if opts.KeyCache == (Policy{}) {
		opts.KeyCache = defaults.KeyCache
	}
	if opts.ApprovalCache == (Policy{}) {
		opts.ApprovalCache = defaults.ApprovalCache
	}
	if opts.ApprovalTimeout == 0 {
		opts.ApprovalTimeout = defaults.ApprovalTimeout
	}
	if opts.LoadTimeout == 0 {
		opts.LoadTimeout = defaults.LoadTimeout
	}
	if opts.Now == nil {
		opts.Now = defaults.Now
	}
	if !validPolicy(opts.KeyCache) || !validPolicy(opts.ApprovalCache) {
		return nil, ErrInvalidPolicy
	}
	if opts.ApprovalTimeout < 0 || opts.LoadTimeout < 0 {
		return nil, ErrInvalidRequest
	}
	keys, err := buildCatalog(opts.Keys)
	if err != nil {
		return nil, err
	}
	state := make(map[string]*keyEntry, len(keys.entries))
	for _, key := range keys.entries {
		state[key.key.ID] = newKeyEntry(key)
	}
	e := &Engine{
		keys: keys, state: state, keyPolicy: opts.KeyCache, approvalPolicy: opts.ApprovalCache,
		now: opts.Now, provider: opts.Provider, approve: opts.Approve,
		approvalTimeout: opts.ApprovalTimeout, loadTimeout: opts.LoadTimeout,
		flights: make(map[*loadFlight]struct{}), stop: make(chan struct{}), done: make(chan struct{}),
		requests: make(chan struct{}, maxRequests), approvals: make(chan struct{}, maxApprovals),
		loads: make(chan struct{}, maxLoads),
	}
	go e.sweep()
	return e, nil
}

func buildCatalog(keys []Key) (catalog, error) {
	if len(keys) > maxKeys {
		return catalog{}, ErrInvalidKey
	}
	index := catalog{
		byID:     make(map[string]*indexedKey, len(keys)),
		byPublic: make(map[string]*indexedKey, len(keys)),
		entries:  make([]*indexedKey, 0, len(keys)),
	}
	for _, key := range keys {
		if key.ID == "" || len(key.ID) > maxKeyField || len(key.VaultID) > maxKeyField ||
			len(key.ItemID) > maxKeyField || len(key.Name) > maxKeyField {
			return catalog{}, ErrInvalidKey
		}
		key = cloneKey(key)
		public, err := parsePublic(key.Public)
		if err != nil ||
			index.byID[key.ID] != nil || index.byPublic[string(key.Public)] != nil {
			return catalog{}, ErrInvalidKey
		}
		entry := &indexedKey{key: key, algorithm: public.Type()}
		index.byID[key.ID] = entry
		index.byPublic[string(key.Public)] = entry
		index.entries = append(index.entries, entry)
	}
	return index, nil
}

// ValidPublic checks the same key policy used by the signing catalog.
func ValidPublic(blob []byte) bool {
	_, err := parsePublic(blob)
	return err == nil
}

func parsePublic(blob []byte) (ssh.PublicKey, error) {
	if len(blob) == 0 || len(blob) > maxPublicKey {
		return nil, ErrInvalidKey
	}
	public, err := ssh.ParsePublicKey(blob)
	if err != nil || !validPublicKey(public) || !bytes.Equal(public.Marshal(), blob) {
		return nil, ErrInvalidKey
	}
	return public, nil
}

func cloneKey(key Key) Key {
	key.Public = bytes.Clone(key.Public)
	return key
}

func newKeyEntry(key *indexedKey) *keyEntry {
	entry := &keyEntry{indexedKey: key}
	entry.renewSignVersion()
	return entry
}

func (entry *keyEntry) renewSignVersion() {
	if entry.cancelSign != nil {
		entry.cancelSign()
	}
	entry.signVersion, entry.cancelSign = context.WithCancel(context.Background())
}

func (e *Engine) List() []Key {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.suspended {
		return []Key{}
	}
	keys := make([]Key, 0, len(e.keys.entries))
	for _, entry := range e.keys.entries {
		keys = append(keys, cloneKey(entry.key))
	}
	return keys
}

func (e *Engine) Status() State {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.pruneLocked(e.now())
	state := State{
		Locked: e.locked, Loaded: make(map[string]bool, len(e.state)),
		Loading: make(map[string]bool, len(e.state)), Errors: make(map[string]string),
		Grants: make(map[string]int, len(e.state)),
	}
	for _, entry := range e.state {
		state.Loaded[entry.key.ID] = entry.material != nil
		state.Loading[entry.key.ID] = entry.flight != nil && entry.flight.ctx.Err() == nil
		state.Grants[entry.key.ID] = len(entry.grants)
		if entry.loadError != "" {
			state.Errors[entry.key.ID] = entry.loadError
		}
	}
	return state
}

func (e *Engine) ReplaceKeys(keys []Key) error {
	next, err := buildCatalog(keys)
	if err != nil {
		return err
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return ErrClosed
	}
	state := make(map[string]*keyEntry, len(next.entries))
	for _, key := range next.entries {
		previous := e.state[key.key.ID]
		if previous != nil && sameKey(previous.key, key.key) {
			previous.indexedKey = key
			state[key.key.ID] = previous
		} else {
			state[key.key.ID] = newKeyEntry(key)
		}
	}
	for _, entry := range e.state {
		if state[entry.key.ID] != entry {
			entry.cancelSign()
			e.clearMaterialLocked(entry)
			entry.clearGrantsLocked()
		}
	}
	e.keys, e.state = next, state
	return nil
}

func sameKey(a, b Key) bool {
	return a.ID == b.ID && a.VaultID == b.VaultID && a.ItemID == b.ItemID && bytes.Equal(a.Public, b.Public)
}

// SuspendIndex withdraws the catalog and retires outstanding work while keeping
// cached state available for identities that survive ReplaceKeys.
func (e *Engine) SuspendIndex() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed || e.suspended {
		return
	}
	e.suspended = true
	for _, entry := range e.state {
		entry.renewSignVersion()
		entry.materialRevision++
	}
	for flight := range e.flights {
		e.discardFlightLocked(flight)
	}
}

func (e *Engine) ResumeIndex() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if !e.closed {
		e.suspended = false
	}
}

func validPolicy(policy Policy) bool {
	switch policy.Mode {
	case "off", "until-lock", "until-exit":
		return policy.TTL == 0
	case "fixed", "idle":
		return policy.TTL > 0
	default:
		return false
	}
}

func (policy Policy) expiry(now time.Time) time.Time {
	if policy.TTL == 0 {
		return time.Time{}
	}
	return now.Add(policy.TTL)
}

func expired(expiry, now time.Time) bool { return !expiry.IsZero() && !now.Before(expiry) }

func (e *Engine) SetPolicies(keyCache, approvalCache Policy) error {
	if !validPolicy(keyCache) || !validPolicy(approvalCache) {
		return ErrInvalidPolicy
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return ErrClosed
	}
	for _, entry := range e.state {
		if e.keyPolicy != keyCache || e.approvalPolicy != approvalCache {
			entry.renewSignVersion()
		}
		if e.keyPolicy != keyCache {
			e.clearMaterialLocked(entry)
		}
		if e.approvalPolicy != approvalCache {
			entry.clearGrantsLocked()
		}
	}
	e.keyPolicy, e.approvalPolicy = keyCache, approvalCache
	return nil
}

func (e *Engine) Lock() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed || e.locked {
		return
	}
	e.locked = true
	for _, entry := range e.state {
		entry.renewSignVersion()
		e.clearMaterialLocked(entry)
		entry.clearGrantsLocked()
	}
}

func (e *Engine) Unlock() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if !e.closed {
		e.locked = false
	}
}

func (e *Engine) EvictKeys() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return
	}
	for _, entry := range e.state {
		entry.renewSignVersion()
		e.clearMaterialLocked(entry)
	}
}

func (e *Engine) EvictKey(keyID string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	entry, err := e.stateEntryLocked(keyID)
	if err != nil {
		return err
	}
	entry.renewSignVersion()
	e.clearMaterialLocked(entry)
	return nil
}

func (e *Engine) Revoke(keyID string) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return
	}
	for _, entry := range e.state {
		if keyID == "" || keyID == entry.key.ID {
			entry.renewSignVersion()
			entry.clearGrantsLocked()
		}
	}
}

func (e *Engine) Close() {
	e.mu.Lock()
	if !e.closed {
		e.closed = true
		e.locked = true
		for _, entry := range e.state {
			entry.cancelSign()
			e.clearMaterialLocked(entry)
			entry.clearGrantsLocked()
		}
		close(e.stop)
	}
	e.mu.Unlock()
	<-e.done
}

func (e *Engine) sweep() {
	defer close(e.done)
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	for {
		select {
		case <-e.stop:
			return
		case <-tick.C:
			e.mu.Lock()
			e.pruneLocked(e.now())
			e.mu.Unlock()
		}
	}
}

func (e *Engine) pruneLocked(now time.Time) {
	for _, entry := range e.state {
		entry.pruneLocked(now)
	}
}

func (entry *keyEntry) pruneLocked(now time.Time) {
	if entry.material != nil && expired(entry.keyExpiry, now) {
		entry.material.dispose()
		entry.material = nil
	}
	entry.pruneGrantsLocked(now)
}

func reserve(slots chan struct{}) bool {
	select {
	case slots <- struct{}{}:
		return true
	default:
		return false
	}
}

func (e *Engine) keyErrorLocked(entry *keyEntry) error {
	if err := e.indexErrorLocked(); err != nil {
		return err
	}
	switch {
	case entry == nil:
		return ErrUnknownKey
	case e.state[entry.key.ID] != entry:
		return ErrStale
	default:
		return nil
	}
}

func (e *Engine) indexErrorLocked() error {
	switch {
	case e.closed:
		return ErrClosed
	case e.locked:
		return ErrLocked
	case e.suspended:
		return ErrStale
	default:
		return nil
	}
}

func (e *Engine) stateEntryLocked(keyID string) (*keyEntry, error) {
	if e.closed {
		return nil, ErrClosed
	}
	entry := e.state[keyID]
	if entry == nil {
		return nil, ErrUnknownKey
	}
	return entry, nil
}
