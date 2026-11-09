package engine

import (
	"context"
	"crypto/rand"
	"errors"
	"slices"
	"time"

	"golang.org/x/crypto/ssh"
)

type grantID struct {
	scope    string
	instance Instance
}

type grant struct {
	expiry   time.Time
	lastUsed time.Time
}

type signRequest struct {
	entry         *keyEntry
	version       context.Context
	revision      uint64
	peer          Peer
	grantID       grantID
	grant         *grant
	algorithm     string
	approvedUntil time.Time
}

// Sign validates and authorizes a single signature. The publisher must finish
// synchronously with a bounded write, and must not call back into the engine.
func (e *Engine) Sign(ctx context.Context, peer Peer, public, data []byte, flags uint32, publish func(*ssh.Signature) error) error {
	if len(public) == 0 || len(public) > maxPublicKey || len(data) > maxSignData || publish == nil || !validPeer(peer) {
		return ErrInvalidRequest
	}
	if !reserve(e.requests) {
		return ErrBusy
	}
	defer func() { <-e.requests }()
	peer.Chain = slices.Clone(peer.Chain)
	request, prompt, err := e.beginSign(ctx, peer, public, flags)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stop := context.AfterFunc(request.version, cancel)
	defer stop()
	if prompt != nil {
		approval, err := e.ask(ctx, *prompt)
		if currentErr := e.checkSign(ctx, request); currentErr != nil {
			return currentErr
		}
		if err != nil {
			return err
		}
		request.grantID, err = approvedGrant(approval, peer, prompt.CacheAllowed)
		if err != nil {
			return err
		}
		if request.grantID.scope != "" {
			e.mu.Lock()
			request.approvedUntil = e.approvalPolicy.expiry(e.now())
			e.mu.Unlock()
		}
	}
	if err := e.checkSign(ctx, request); err != nil {
		return err
	}
	material, flight, err := e.getMaterial(ctx, request.entry, request.version, request.revision)
	if flight != nil {
		defer e.releaseFlight(flight)
	}
	if err != nil {
		return err
	}
	return e.signAndPublish(ctx, request, material, data, publish)
}

func validPeer(peer Peer) bool {
	if peer.Alive == nil || peer.Connected == nil || len(peer.Chain) == 0 || len(peer.Chain) > maxPeerChain {
		return false
	}
	seen := make(map[uint32]bool, len(peer.Chain))
	for _, process := range peer.Chain {
		if process.PID == 0 || process.Created == 0 || seen[process.PID] || len(process.Path) > maxPathBytes {
			return false
		}
		seen[process.PID] = true
	}
	return true
}

func (e *Engine) beginSign(ctx context.Context, peer Peer, public []byte, flags uint32) (signRequest, *Prompt, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	var entry *keyEntry
	if key := e.keys.byPublic[string(public)]; key != nil {
		entry = e.state[key.key.ID]
	}
	if err := e.keyErrorLocked(entry); err != nil {
		return signRequest{}, nil, err
	}
	algorithm, err := signatureAlgorithm(entry.algorithm, flags)
	if err != nil {
		return signRequest{}, nil, err
	}
	request := signRequest{entry: entry, version: entry.signVersion, revision: entry.materialRevision, peer: peer, algorithm: algorithm}
	if err := e.checkSignLocked(ctx, request); err != nil {
		return signRequest{}, nil, err
	}
	for i, process := range peer.Chain {
		if !peer.Alive(process.Instance) {
			continue
		}
		if i == 0 {
			id := grantID{scope: "client", instance: process.Instance}
			if cached := entry.grants[id]; cached != nil {
				request.grantID, request.grant = id, cached
				return request, nil, nil
			}
		}
		id := grantID{scope: "host", instance: process.Instance}
		if cached := entry.grants[id]; cached != nil {
			request.grantID, request.grant = id, cached
			return request, nil, nil
		}
	}
	return request, &Prompt{
		Key: cloneKey(entry.key), Chain: slices.Clone(peer.Chain), CacheAllowed: e.approvalPolicy.Mode != "off",
	}, nil
}

func (e *Engine) ask(ctx context.Context, prompt Prompt) (Approval, error) {
	if e.approve == nil {
		return Approval{}, ErrDenied
	}
	if !reserve(e.approvals) {
		return Approval{}, ErrBusy
	}
	ctx, cancel := context.WithTimeout(ctx, e.approvalTimeout)
	defer cancel()
	prompt.Deadline, _ = ctx.Deadline()
	type result struct {
		approval Approval
		err      error
	}
	results := make(chan result, 1)
	go func() {
		defer func() { <-e.approvals }()
		if ctx.Err() == nil {
			approval, err := e.approve(ctx, prompt)
			results <- result{approval, err}
		}
	}()
	select {
	case decision := <-results:
		if ctx.Err() == nil {
			if decision.err != nil {
				return Approval{}, ErrDenied
			}
			return decision.approval, nil
		}
	case <-ctx.Done():
	}
	if errors.Is(ctx.Err(), context.DeadlineExceeded) {
		return Approval{}, ErrApprovalTimeout
	}
	return Approval{}, ctx.Err()
}

func approvedGrant(approval Approval, peer Peer, cacheAllowed bool) (grantID, error) {
	switch approval.Scope {
	case "once":
		return grantID{}, nil
	case "client":
		if cacheAllowed {
			return grantID{scope: "client", instance: peer.Chain[0].Instance}, nil
		}
	case "host":
		if cacheAllowed && approval.Candidate > 0 && approval.Candidate < len(peer.Chain) {
			return grantID{scope: "host", instance: peer.Chain[approval.Candidate].Instance}, nil
		}
	}
	return grantID{}, ErrDenied
}

func (e *Engine) checkSign(ctx context.Context, request signRequest) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.checkSignLocked(ctx, request)
}

func (e *Engine) checkSignLocked(ctx context.Context, request signRequest) error {
	if err := e.keyErrorLocked(request.entry); err != nil {
		return err
	}
	if request.entry.signVersion != request.version {
		return ErrStale
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if !request.peer.Connected() || !request.peer.Alive(request.peer.Chain[0].Instance) ||
		(request.grantID.scope == "host" && !request.peer.Alive(request.grantID.instance)) {
		return ErrPeerGone
	}
	now := e.now()
	request.entry.pruneLocked(now)
	if expired(request.approvedUntil, now) {
		return ErrStale
	}
	if request.grant != nil && request.entry.grants[request.grantID] != request.grant {
		return ErrStale
	}
	return nil
}

func (e *Engine) signAndPublish(ctx context.Context, request signRequest, material *keyMaterial, data []byte, publish func(*ssh.Signature) error) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if err := e.checkSignLocked(ctx, request); err != nil {
		return err
	}
	if material == nil || material.signer == nil {
		return ErrStale
	}
	signature, err := material.signer.SignWithAlgorithm(rand.Reader, data, request.algorithm)
	if err != nil {
		return ErrInvalidKey
	}
	if err := e.checkSignLocked(ctx, request); err != nil {
		return err
	}
	if material.signer == nil {
		return ErrStale
	}
	if err := publish(signature); err != nil {
		return ErrPublish
	}
	now := e.now()
	if e.keyPolicy.Mode == "idle" {
		request.entry.keyExpiry = e.keyPolicy.expiry(now)
	}
	if request.grantID.scope != "" {
		e.useGrantLocked(request, now)
	}
	return nil
}

func (e *Engine) useGrantLocked(request signRequest, now time.Time) {
	grants := request.entry.grants
	current := request.grant
	if current == nil {
		current = grants[request.grantID]
		if current == nil || expired(current.expiry, now) {
			current = &grant{expiry: request.approvedUntil}
			if grants == nil {
				grants = make(map[grantID]*grant)
				request.entry.grants = grants
			}
			if _, replacing := grants[request.grantID]; !replacing && len(grants) == maxGrants {
				var oldest grantID
				for id, cached := range grants {
					if grants[oldest] == nil || cached.lastUsed.Before(grants[oldest].lastUsed) {
						oldest = id
					}
				}
				delete(grants, oldest)
			}
			grants[request.grantID] = current
		}
	}
	current.lastUsed = now
	if e.approvalPolicy.Mode == "idle" {
		current.expiry = e.approvalPolicy.expiry(now)
	}
}
