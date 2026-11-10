package engine

import (
	"context"
	"crypto/rand"
	"errors"
	"slices"
	"time"

	"golang.org/x/crypto/ssh"
)

type signRequest struct {
	entry         *keyEntry
	keyName       string
	version       context.Context
	revision      uint64
	peer          Peer
	grantID       grantID
	grant         *grant
	algorithm     string
	approvedUntil time.Time
}

type SignObservation struct {
	KeyName string
	Anchor  Process
}

// Sign validates and authorizes a single signature. The publisher must finish
// synchronously with a bounded write, and must not call back into the engine.
func (e *Engine) Sign(ctx context.Context, peer Peer, public, data []byte, flags uint32, publish func(*ssh.Signature) error) error {
	return e.SignObserved(ctx, peer, public, data, flags, publish, nil)
}

// SignObserved reports the selected key and approval anchor outside the engine
// lock before returning. A request without a reusable approval has no anchor.
func (e *Engine) SignObserved(ctx context.Context, peer Peer, public, data []byte, flags uint32, publish func(*ssh.Signature) error, observe func(SignObservation)) error {
	if len(public) == 0 || len(public) > maxPublicKey || len(data) > maxSignData || publish == nil || !validPeer(peer) {
		return ErrInvalidRequest
	}
	if !reserve(e.requests) {
		return ErrBusy
	}
	defer func() { <-e.requests }()
	peer.Chain = slices.Clone(peer.Chain)
	request, prompt, err := e.beginSign(ctx, peer, public, flags)
	if observe != nil {
		defer func() { observe(SignObservation{KeyName: request.keyName, Anchor: request.anchor()}) }()
	}
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stop := context.AfterFunc(request.version, cancel)
	defer stop()
	if request.grant != nil {
		stopGrant := context.AfterFunc(request.grant.ctx, cancel)
		defer stopGrant()
	}
	if prompt != nil {
		if peer.ApprovalApp != nil {
			i, ownsWindow := peer.ApprovalApp(ctx)
			if ownsWindow && i >= 0 && i < len(peer.Chain) && peer.Alive(peer.Chain[i].Instance) {
				prompt.AppCandidate = i
				prompt.appOwnsWindow = true
			}
		}
		approval, err := e.ask(ctx, *prompt)
		if currentErr := e.checkSign(ctx, request); currentErr != nil {
			return currentErr
		}
		if err != nil {
			return err
		}
		request.grantID, err = approvedGrant(approval, peer, *prompt)
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
	material, waiter, err := e.getMaterial(ctx, request.entry, request.version, request.revision)
	if waiter != nil {
		defer e.releaseFlight(waiter, err)
	}
	if err != nil {
		return err
	}
	return e.signAndPublish(ctx, request, material, data, publish)
}

func (request signRequest) anchor() Process {
	for _, process := range request.peer.Chain {
		if process.Instance == request.grantID.instance {
			return process
		}
	}
	return Process{}
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
	request := signRequest{peer: peer}
	if key := e.keys.byPublic[string(public)]; key != nil {
		request.entry = e.state[key.key.ID]
		request.keyName = key.key.Name
	}
	entry := request.entry
	if err := e.keyErrorLocked(entry); err != nil {
		return request, nil, err
	}
	algorithm, err := signatureAlgorithm(entry.algorithm, flags)
	if err != nil {
		return request, nil, err
	}
	request.version, request.revision, request.algorithm = entry.signVersion, entry.materialRevision, algorithm
	if err := e.checkSignLocked(ctx, request); err != nil {
		return request, nil, err
	}
	for i, process := range peer.Chain {
		if !peer.Alive(process.Instance) {
			continue
		}
		if i == 0 {
			id := grantID{scope: "app", instance: process.Instance}
			if cached := entry.grants[id]; cached != nil {
				request.grantID, request.grant = id, cached
				return request, nil, nil
			}
		}
		id := grantID{scope: "app", instance: process.Instance, descendants: true}
		if cached := entry.grants[id]; cached != nil {
			request.grantID, request.grant = id, cached
			return request, nil, nil
		}
	}
	if peer.Logon != ([32]byte{}) {
		id := grantID{scope: "all", logon: peer.Logon}
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
	if err := ctx.Err(); err != nil {
		return Approval{}, err
	}
	if e.approve == nil {
		return Approval{}, ErrUnavailable
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
			if errors.Is(decision.err, ErrDenied) {
				return Approval{}, ErrDenied
			}
			if decision.err != nil {
				return Approval{}, ErrUnavailable
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

func approvedGrant(approval Approval, peer Peer, prompt Prompt) (grantID, error) {
	if approval.Scope != "app" && approval.Candidate != 0 {
		return grantID{}, ErrDenied
	}
	switch approval.Scope {
	case "once":
		return grantID{}, nil
	case "app":
		if prompt.CacheAllowed && approval.Candidate == prompt.AppCandidate {
			return grantID{scope: "app", instance: peer.Chain[prompt.AppCandidate].Instance, descendants: prompt.appOwnsWindow}, nil
		}
	case "all":
		if prompt.CacheAllowed && peer.Logon != ([32]byte{}) {
			return grantID{scope: "all", logon: peer.Logon}, nil
		}
	case "unavailable":
		return grantID{}, ErrUnavailable
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
		(request.grantID.descendants && !request.peer.Alive(request.grantID.instance)) {
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
