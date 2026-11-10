package engine

import (
	"cmp"
	"context"
	"crypto/rand"
	"slices"
	"strings"
	"time"
	"unicode"
)

// Grant is the display state of a single reusable approval. ExpiresAt is zero
// for approvals that do not have a time limit.
type Grant struct {
	ID        string
	Name      string
	PID       uint32
	Scope     string
	ExpiresAt time.Time
}

type grantID struct {
	instance    Instance
	descendants bool
}

type grant struct {
	id       string
	name     string
	expiry   time.Time
	lastUsed time.Time
	ctx      context.Context
	cancel   context.CancelFunc
	alive    func() bool
	release  func()
}

func (e *Engine) Grants(keyID string) ([]Grant, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	entry, err := e.stateEntryLocked(keyID)
	if err != nil {
		return nil, err
	}
	entry.pruneGrantsLocked(e.now())
	grants := make([]Grant, 0, len(entry.grants))
	for id, current := range entry.grants {
		grants = append(grants, Grant{
			ID: current.id, Name: current.name, PID: id.instance.PID, Scope: "app", ExpiresAt: current.expiry,
		})
	}
	slices.SortFunc(grants, func(a, b Grant) int {
		return cmp.Or(cmp.Compare(a.PID, b.PID), cmp.Compare(a.ID, b.ID))
	})
	return grants, nil
}

func (e *Engine) RevokeGrant(keyID, opaqueID string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	entry, err := e.stateEntryLocked(keyID)
	if err != nil {
		return err
	}
	entry.pruneGrantsLocked(e.now())
	for id, current := range entry.grants {
		if current.id == opaqueID {
			entry.retireGrantLocked(id)
			return nil
		}
	}
	return ErrUnknownGrant
}

func (entry *keyEntry) pruneGrantsLocked(now time.Time) {
	for id, current := range entry.grants {
		if expired(current.expiry, now) || (current.alive != nil && !current.alive()) {
			entry.retireGrantLocked(id)
		}
	}
}

func (entry *keyEntry) clearGrantsLocked() {
	for id := range entry.grants {
		entry.retireGrantLocked(id)
	}
	entry.grants = nil
}

func (entry *keyEntry) retireGrantLocked(id grantID) {
	current := entry.grants[id]
	delete(entry.grants, id)
	current.cancel()
	if current.release != nil {
		current.release()
	}
}

func (e *Engine) useGrantLocked(request signRequest, now time.Time) {
	entry := request.entry
	current := entry.grants[request.grantID]
	if current != nil && request.grant == nil && expired(current.expiry, now) {
		entry.retireGrantLocked(request.grantID)
		current = nil
	}
	if current == nil {
		var alive func() bool
		var release func()
		if request.peer.Retain != nil {
			var ok bool
			alive, release, ok = request.peer.Retain(request.grantID.instance)
			if !ok {
				return
			}
		}
		ctx, cancel := context.WithCancel(context.Background())
		current = &grant{
			id: rand.Text(), name: grantProcessName(request.anchor().Path), expiry: request.approvedUntil,
			ctx: ctx, cancel: cancel, alive: alive, release: release,
		}
		if entry.grants == nil {
			entry.grants = make(map[grantID]*grant)
		}
		if len(entry.grants) == maxGrants {
			var oldest grantID
			for id, cached := range entry.grants {
				if entry.grants[oldest] == nil || cached.lastUsed.Before(entry.grants[oldest].lastUsed) {
					oldest = id
				}
			}
			entry.retireGrantLocked(oldest)
		}
		entry.grants[request.grantID] = current
	}
	current.lastUsed = now
	if e.approvalPolicy.Mode == "idle" {
		current.expiry = e.approvalPolicy.expiry(now)
	}
}

func grantProcessName(path string) string {
	name := path[strings.LastIndexAny(path, "/\\")+1:]
	if len(name) >= 2 && name[1] == ':' {
		name = name[2:]
	}
	name = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) || unicode.Is(unicode.Bidi_Control, r) || r == '\u2028' || r == '\u2029' {
			return '�'
		}
		return r
	}, name)
	if runes := []rune(name); len(runes) > 255 {
		name = string(runes[:255])
	}
	return name
}
