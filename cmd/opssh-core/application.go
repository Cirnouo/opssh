package main

import (
	"context"
	"errors"
	"fmt"
	"maps"
	"strings"
	"sync"
	"time"
	"unicode"

	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/desktop"
	"opssh/internal/engine"
)

var version = "dev" // Set from the tray project Version by the Windows build.

type vault interface {
	Public(context.Context, string, string) ([]byte, error)
	Private(context.Context, string, string) ([]byte, error)
	Status() string
}

type discoverableVault interface {
	vault
	Discover(context.Context, config.Scope) ([]desktop.Vault, error)
}

type vaultSlot struct {
	mu sync.RWMutex
	v  vault
}

func (s *vaultSlot) get() vault {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.v
}
func (s *vaultSlot) set(v vault) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.v = v
}
func (s *vaultSlot) Load(ctx context.Context, key engine.Key) ([]byte, error) {
	return s.get().Private(ctx, key.VaultID, key.ItemID)
}

type application struct {
	path, endpoint  string
	settings        config.Settings // changed only by the control command worker
	vaults          []desktop.Vault // only public discovery metadata, scoped to this account
	engine          *engine.Engine
	channel         *control.Channel
	vault           vaultSlot
	newVault        func(string) vault
	restartMu       sync.Mutex
	restartRequired bool // written only by the worker, read by unlock under restartMu
}

func newApplication(path, endpoint string, cfg config.Settings, ch *control.Channel, factory func(string) vault) (*application, error) {
	a := &application{path: path, endpoint: endpoint, settings: cfg, vaults: []desktop.Vault{}, channel: ch, newVault: factory}
	a.vault.set(factory(cfg.Account))
	opts := engine.DefaultOptions()
	opts.Keys = engineKeys(cfg)
	opts.KeyCache = enginePolicy(cfg.KeyCache)
	opts.ApprovalCache = enginePolicy(cfg.ApprovalCache)
	opts.Provider = &a.vault
	opts.Approve = a.approve
	e, err := engine.New(opts)
	if err != nil {
		return nil, err
	}
	a.engine = e
	return a, nil
}

func engineKeys(cfg config.Settings) []engine.Key {
	keys := make([]engine.Key, 0, len(cfg.Keys))
	for _, k := range cfg.Keys {
		if len(k.Public) > 0 {
			keys = append(keys, engine.Key{ID: k.ID(), VaultID: k.VaultID, ItemID: k.ItemID, Name: k.Name, Public: k.Public})
		}
	}
	return keys
}

func enginePolicy(p config.Policy) engine.Policy {
	return engine.Policy{Mode: p.Mode, TTL: time.Duration(p.Seconds) * time.Second}
}

type processDisplay struct {
	PID     uint32 `json:"pid"`
	Created uint64 `json:"created"`
	Path    string `json:"path"`
}

func (a *application) approve(ctx context.Context, prompt engine.Prompt) (engine.Approval, error) {
	if a.channel == nil {
		return engine.Approval{}, control.ErrDisconnected
	}
	chain := make([]processDisplay, 0, len(prompt.Chain))
	for _, p := range prompt.Chain {
		// Keep the end of very long Windows paths, where the executable name lives.
		runes := []rune(p.Path)
		if len(runes) > 512 {
			runes = append([]rune{'…'}, runes[len(runes)-511:]...)
		}
		var shown strings.Builder
		for _, r := range runes {
			if unicode.IsControl(r) || unicode.Is(unicode.Bidi_Control, r) || r == '\u2028' || r == '\u2029' {
				fmt.Fprintf(&shown, "[U+%04X]", r)
			} else {
				shown.WriteRune(r)
			}
		}
		path := shown.String()
		chain = append(chain, processDisplay{PID: p.PID, Created: p.Created, Path: path})
	}
	body := struct {
		Name         string           `json:"name"`
		NameHint     string           `json:"nameHint,omitempty"`
		Fingerprint  string           `json:"fingerprint"`
		Chain        []processDisplay `json:"chain"`
		Deadline     time.Time        `json:"deadline"`
		CacheAllowed bool             `json:"cacheAllowed"`
	}{
		Name: prompt.Key.Name, Fingerprint: fingerprint(prompt.Key.Public),
		Chain: chain, Deadline: prompt.Deadline, CacheAllowed: prompt.CacheAllowed,
	}
	if body.Name == "" {
		body.NameHint = prompt.Key.ItemID[:8]
		body.Name = "SSH key " + body.NameHint
	}
	decision, err := a.channel.Prompt(ctx, body)
	return engine.Approval{Scope: decision.Scope, Candidate: decision.Candidate}, err
}

type keyDisplay struct {
	ID          string `json:"id"`
	VaultID     string `json:"vaultId"`
	ItemID      string `json:"itemId"`
	Name        string `json:"name"`
	Unnamed     bool   `json:"unnamed,omitempty"`
	Fingerprint string `json:"fingerprint"`
	Loaded      bool   `json:"loaded"`
	Loading     bool   `json:"loading"`
	Error       string `json:"error,omitempty"`
}
type appState struct {
	Pipe          string          `json:"pipe"`
	Locked        bool            `json:"locked"`
	Account       string          `json:"account"`
	Keys          []keyDisplay    `json:"keys"`
	KeyCache      config.Policy   `json:"keyCache"`
	ApprovalCache config.Policy   `json:"approvalCache"`
	Provider      string          `json:"provider"`
	Pending       int             `json:"pending"`
	Scope         config.Scope    `json:"scope"`
	Vaults        []desktop.Vault `json:"vaults"`
	Index         string          `json:"index"`
}

func (a *application) status() appState {
	s := a.engine.Status()
	state := appState{
		Pipe: a.endpoint, Locked: s.Locked, Account: a.settings.Account,
		Keys:     make([]keyDisplay, 0, len(a.settings.Keys)),
		KeyCache: a.settings.KeyCache, ApprovalCache: a.settings.ApprovalCache,
		Provider: a.vault.get().Status(), Scope: a.settings.Scope, Vaults: a.vaults, Index: a.settings.Index,
	}
	if a.restartRequired {
		state.Provider = "unknown"
		if state.Account == "" {
			state.Provider = "unconfigured"
		}
	}
	if a.channel != nil {
		state.Pending = a.channel.Pending()
	}
	for _, k := range a.settings.Keys {
		row := keyDisplay{ID: k.ID(), VaultID: k.VaultID, ItemID: k.ItemID, Name: k.Name, Unnamed: k.Name == "",
			Fingerprint: fingerprint(k.Public), Loaded: s.Loaded[k.ID()], Loading: s.Loading[k.ID()], Error: s.Errors[k.ID()]}
		if row.Unnamed {
			row.Name = "SSH key " + k.ItemID[:8]
		}
		if len(k.Public) == 0 {
			row.Error = "public-index-missing"
		}
		state.Keys = append(state.Keys, row)
	}
	return state
}

func fingerprint(blob []byte) string {
	key, err := ssh.ParsePublicKey(blob)
	if err != nil {
		return ""
	}
	return ssh.FingerprintSHA256(key)
}

// These operations only touch the synchronized engine. They can run while
// the regular command worker is waiting on 1Password.
func (a *application) priority(msg control.Message) (control.Result, bool) {
	switch msg.Type {
	case "lock":
		a.engine.Lock()
	case "unlock":
		a.restartMu.Lock()
		defer a.restartMu.Unlock()
		if a.restartRequired {
			return restartResult(), true
		}
		a.engine.Unlock()
	case "evict-keys":
		a.engine.EvictKeys()
	case "revoke":
		var body struct {
			KeyID string `json:"keyId"`
		}
		if len(msg.Body) > 0 && control.DecodeBody(msg, &body) != nil {
			return commandResult(errCommand), true
		}
		a.engine.Revoke(body.KeyID)
	default:
		return control.Result{}, false
	}
	return control.Result{OK: true}, true
}

func (a *application) command(ctx context.Context, msg control.Message) control.Result {
	var err error
	if a.restartRequired && msg.Type != "status" {
		result := restartResult()
		result.State = a.status()
		return result
	}
	switch msg.Type {
	case "status":
	case "settings":
		err = a.updateSettings(msg)
	case "connect":
		err = a.connect(ctx, msg)
	case "refresh-index":
		if len(msg.Body) != 0 {
			err = errCommand
		} else {
			err = a.refreshIndex(ctx, a.copySettings())
		}
	case "set-scope":
		var scope config.Scope
		if control.DecodeBody(msg, &scope) != nil || !scope.Valid() {
			err = errCommand
		} else {
			next := a.copySettings()
			next.Scope = scope.Normalized()
			if next.Account == "" {
				err = a.persist(next)
			} else {
				err = a.refreshIndex(ctx, next)
			}
		}
	case "add-key":
		err = a.addKey(ctx, msg)
	case "refresh", "remove-key", "preload":
		var body struct {
			KeyID string `json:"keyId"`
		}
		if control.DecodeBody(msg, &body) != nil {
			err = errCommand
			break
		}
		i := a.keyIndex(body.KeyID)
		if i < 0 {
			err = engine.ErrUnknownKey
			break
		}
		switch msg.Type {
		case "refresh":
			err = a.refresh(ctx, i)
		case "remove-key":
			next := a.copySettings()
			next.Keys = append(next.Keys[:i], next.Keys[i+1:]...)
			next.Scope.Items[body.KeyID] = "exclude"
			next.Index = config.IndexPartial
			err = a.persist(next)
		case "preload":
			err = a.engine.Preload(ctx, body.KeyID)
		}
	default:
		err = errCommand
	}
	result := commandResult(err)
	result.State = a.status()
	result.RestartRequired = a.restartRequired
	return result
}

func restartResult() control.Result {
	return control.Result{
		ErrorCode: "restart-required", Error: "Restart the core to use the selected account.", RestartRequired: true,
	}
}

func (a *application) keyIndex(id string) int {
	for i, k := range a.settings.Keys {
		if k.ID() == id {
			return i
		}
	}
	return -1
}

func (a *application) copySettings() config.Settings {
	next := a.settings
	next.Keys = append([]config.Key{}, next.Keys...)
	next.Scope.Vaults = maps.Clone(next.Scope.Vaults)
	next.Scope.Items = maps.Clone(next.Scope.Items)
	return next
}

func (a *application) updateSettings(msg control.Message) error {
	var form struct {
		Account       *string       `json:"account"`
		KeyCache      config.Policy `json:"keyCache"`
		ApprovalCache config.Policy `json:"approvalCache"`
	}
	if control.DecodeBody(msg, &form) != nil {
		return errCommand
	}
	next := a.copySettings()
	if form.Account != nil {
		next.Account = strings.TrimSpace(*form.Account)
	}
	next.KeyCache, next.ApprovalCache = form.KeyCache, form.ApprovalCache
	return a.applySettings(next)
}

func (a *application) connect(ctx context.Context, msg control.Message) error {
	var form struct {
		Account string `json:"account"`
	}
	if control.DecodeBody(msg, &form) != nil || strings.TrimSpace(form.Account) == "" {
		return errCommand
	}
	next := a.copySettings()
	next.Account = strings.TrimSpace(form.Account)
	if next.Account != a.settings.Account {
		if err := a.applySettings(next); err != nil || a.restartRequired {
			return err
		}
	}
	return a.refreshIndex(ctx, a.copySettings())
}

func (a *application) applySettings(next config.Settings) error {
	oldAccount := a.settings.Account
	if next.Account != oldAccount {
		next.Keys = []config.Key{}
		next.Index = config.IndexNotIndexed
		if oldAccount != "" {
			next.Scope = config.Default().Scope
		}
	}
	if err := a.persist(next); err != nil {
		return err
	}
	if next.Account != oldAccount {
		a.vaults = []desktop.Vault{}
		if oldAccount != "" {
			a.restartMu.Lock()
			a.engine.Lock()
			a.restartRequired = true
			a.restartMu.Unlock()
		} else {
			a.vault.set(a.newVault(next.Account))
		}
	}
	return nil
}

func (a *application) addKey(ctx context.Context, msg control.Message) error {
	var form struct {
		VaultID string `json:"vaultId"`
		ItemID  string `json:"itemId"`
		Name    string `json:"name"`
	}
	if control.DecodeBody(msg, &form) != nil {
		return errCommand
	}
	if a.settings.Account == "" {
		return desktop.ErrUnconfigured
	}
	k := config.Key{VaultID: strings.TrimSpace(form.VaultID), ItemID: strings.TrimSpace(form.ItemID), Name: strings.TrimSpace(form.Name)}
	if a.keyIndex(k.ID()) >= 0 {
		return errAlreadySelected
	}
	next := a.copySettings()
	next.Keys = append(next.Keys, k)
	next.Scope.Items[k.ID()] = "include"
	next.Index = config.IndexPartial
	if err := a.persist(next); err != nil {
		return err
	}
	return a.refresh(ctx, len(next.Keys)-1)
}

func (a *application) refresh(ctx context.Context, i int) error {
	next := a.copySettings()
	previousIndex := next.Index
	k := next.Keys[i]
	// Disable the old identity before waiting for any provider result.
	if len(k.Public) > 0 {
		next.Keys[i].Public = nil
		next.Index = config.IndexPartial
		if err := a.persist(next); err != nil {
			return err
		}
	}
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	public, err := a.vault.get().Public(ctx, k.VaultID, k.ItemID)
	if err != nil {
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if !engine.ValidPublic(public) {
		return desktop.ErrPublic
	}
	next.Keys[i].Public = public
	next.Index = previousIndex
	return a.persist(next)
}

func (a *application) persist(next config.Settings) error {
	if err := config.Save(a.path, next); err != nil {
		if errors.Is(err, config.ErrInvalid) {
			return err
		}
		return errSettingsWrite
	}
	if err := a.engine.ReplaceKeys(engineKeys(next)); err != nil {
		return err
	}
	if next.KeyCache != a.settings.KeyCache || next.ApprovalCache != a.settings.ApprovalCache {
		if err := a.engine.SetPolicies(enginePolicy(next.KeyCache), enginePolicy(next.ApprovalCache)); err != nil {
			return err
		}
	}
	a.settings = next
	return nil
}

var (
	errCommand         = errors.New("invalid tray command")
	errSettingsWrite   = errors.New("could not save settings")
	errAlreadySelected = errors.New("item already selected")
)

func commandResult(err error) control.Result {
	if err == nil {
		return control.Result{OK: true}
	}
	code, message := displayError(err)
	return control.Result{ErrorCode: code, Error: message}
}

// Keep the safe English message for older trays. New trays translate the code.
func displayError(err error) (code, message string) {
	switch {
	case errors.Is(err, errCommand):
		return "invalid-command", "Invalid command or settings."
	case errors.Is(err, errSettingsWrite):
		return "configuration-write", "Could not save the public configuration."
	case errors.Is(err, errAlreadySelected):
		return "already-selected", "This item is already selected."
	case errors.Is(err, config.ErrInvalid):
		return "invalid-settings", "Check the account name, item IDs, cache duration and duplicate public keys."
	case errors.Is(err, desktop.ErrUnconfigured):
		return "account-unconfigured", "Configure your 1Password account first."
	case errors.Is(err, desktop.ErrBusy), errors.Is(err, engine.ErrBusy):
		return "busy", "A request is already in progress. Try again after it completes."
	case errors.Is(err, desktop.ErrScopeTooBroad):
		return "scope-too-broad", "The scope selects more than 64 SSH keys. Narrow the selection and refresh the public index."
	case errors.Is(err, desktop.ErrScanLimit):
		return "scan-limit", "Discovery can scan up to 256 vaults and 8,192 item overviews per scoped vault. The public index is empty."
	case errors.Is(err, desktop.ErrPartial):
		return "partial-index", "The public index is incomplete. Check 1Password desktop access and refresh."
	case errors.Is(err, desktop.ErrPublic), errors.Is(err, desktop.ErrUnavailable):
		return "public-lookup", "The public key lookup failed. Check 1Password desktop integration and the selected item, then refresh."
	case errors.Is(err, engine.ErrUnknownKey):
		return "unknown-key", "Select a key with a current public index."
	case errors.Is(err, engine.ErrLocked):
		return "locked", "Unlock opssh before loading a private key."
	case errors.Is(err, engine.ErrLoadTimeout), errors.Is(err, context.DeadlineExceeded):
		return "provider-timeout", "The 1Password request timed out."
	case errors.Is(err, context.Canceled):
		return "canceled", "The request was canceled."
	case errors.Is(err, engine.ErrKeyMismatch):
		return "key-mismatch", "The private key no longer matches the public index. Refresh the item."
	case errors.Is(err, engine.ErrInvalidKey):
		return "invalid-key", "The selected private key has an unsupported or invalid format."
	default:
		return "request-failed", "The request could not be completed."
	}
}
