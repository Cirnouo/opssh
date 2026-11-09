package main

import (
	"context"
	"errors"
	"fmt"
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

const version = "0.1.0"

type vault interface {
	Public(context.Context, string, string) ([]byte, error)
	Private(context.Context, string, string) ([]byte, error)
	Status() string
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
	engine          *engine.Engine
	channel         *control.Channel
	vault           vaultSlot
	newVault        func(string) vault
	restartMu       sync.Mutex
	restartRequired bool // written only by the worker, read by unlock under restartMu
}

func newApplication(path, endpoint string, cfg config.Settings, ch *control.Channel, factory func(string) vault) (*application, error) {
	a := &application{path: path, endpoint: endpoint, settings: cfg, channel: ch, newVault: factory}
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
			keys = append(keys, engine.Key{ID: k.ID(), VaultID: k.VaultID, ItemID: k.ItemID, Name: label(k), Public: k.Public})
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
		Fingerprint  string           `json:"fingerprint"`
		Chain        []processDisplay `json:"chain"`
		Deadline     time.Time        `json:"deadline"`
		CacheAllowed bool             `json:"cacheAllowed"`
	}{prompt.Key.Name, fingerprint(prompt.Key.Public), chain, prompt.Deadline, prompt.CacheAllowed}
	decision, err := a.channel.Prompt(ctx, body)
	return engine.Approval{Scope: decision.Scope, Candidate: decision.Candidate}, err
}

type keyDisplay struct {
	ID          string `json:"id"`
	VaultID     string `json:"vaultId"`
	ItemID      string `json:"itemId"`
	Name        string `json:"name"`
	Fingerprint string `json:"fingerprint"`
	Loaded      bool   `json:"loaded"`
	Loading     bool   `json:"loading"`
	Error       string `json:"error,omitempty"`
}
type appState struct {
	Pipe          string        `json:"pipe"`
	Locked        bool          `json:"locked"`
	Account       string        `json:"account"`
	Keys          []keyDisplay  `json:"keys"`
	KeyCache      config.Policy `json:"keyCache"`
	ApprovalCache config.Policy `json:"approvalCache"`
	Provider      string        `json:"provider"`
	Pending       int           `json:"pending"`
}

func (a *application) status() appState {
	s := a.engine.Status()
	state := appState{
		Pipe: a.endpoint, Locked: s.Locked, Account: a.settings.Account,
		Keys:     make([]keyDisplay, 0, len(a.settings.Keys)),
		KeyCache: a.settings.KeyCache, ApprovalCache: a.settings.ApprovalCache,
		Provider: a.vault.get().Status(),
	}
	if a.channel != nil {
		state.Pending = a.channel.Pending()
	}
	for _, k := range a.settings.Keys {
		row := keyDisplay{ID: k.ID(), VaultID: k.VaultID, ItemID: k.ItemID, Name: label(k),
			Fingerprint: fingerprint(k.Public), Loaded: s.Loaded[k.ID()], Loading: s.Loading[k.ID()], Error: s.Errors[k.ID()]}
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

func label(k config.Key) string {
	if k.Name != "" {
		return k.Name
	}
	return "SSH key " + k.ItemID[:8]
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
			return control.Result{Error: "Restart the core to use the selected account.", RestartRequired: true}, true
		}
		a.engine.Unlock()
	case "evict-keys":
		a.engine.EvictKeys()
	case "revoke":
		var body struct {
			KeyID string `json:"keyId"`
		}
		if len(msg.Body) > 0 && control.DecodeBody(msg, &body) != nil {
			return control.Result{Error: displayError(errCommand)}, true
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
		return control.Result{Error: "Restart the core to use the selected account.", State: a.status(), RestartRequired: true}
	}
	switch msg.Type {
	case "status":
	case "settings":
		err = a.updateSettings(msg)
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
			err = a.persist(next)
		case "preload":
			err = a.engine.Preload(ctx, body.KeyID)
		}
	default:
		err = errCommand
	}
	return control.Result{OK: err == nil, Error: displayError(err), State: a.status(), RestartRequired: a.restartRequired}
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
	return next
}

func (a *application) updateSettings(msg control.Message) error {
	var form struct {
		Account       string        `json:"account"`
		KeyCache      config.Policy `json:"keyCache"`
		ApprovalCache config.Policy `json:"approvalCache"`
	}
	if control.DecodeBody(msg, &form) != nil {
		return errCommand
	}
	next := a.copySettings()
	next.Account = strings.TrimSpace(form.Account)
	next.KeyCache, next.ApprovalCache = form.KeyCache, form.ApprovalCache
	oldAccount := a.settings.Account
	if next.Account != oldAccount {
		next.Keys = []config.Key{}
	}
	if err := a.persist(next); err != nil {
		return err
	}
	if next.Account != oldAccount {
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
	if err := a.persist(next); err != nil {
		return err
	}
	return a.refresh(ctx, len(next.Keys)-1)
}

func (a *application) refresh(ctx context.Context, i int) error {
	next := a.copySettings()
	k := next.Keys[i]
	// Disable the old identity before waiting for any provider result.
	if len(k.Public) > 0 {
		next.Keys[i].Public = nil
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

func displayError(err error) string {
	switch {
	case err == nil:
		return ""
	case errors.Is(err, errCommand):
		return "Invalid command or settings."
	case errors.Is(err, errSettingsWrite):
		return "Could not save the public configuration."
	case errors.Is(err, errAlreadySelected):
		return "This item is already selected."
	case errors.Is(err, config.ErrInvalid):
		return "Check the account name, item IDs, cache duration and duplicate public keys."
	case errors.Is(err, desktop.ErrUnconfigured):
		return "Configure your 1Password account first."
	case errors.Is(err, desktop.ErrBusy), errors.Is(err, engine.ErrBusy):
		return "A request is already in progress. Try again after it completes."
	case errors.Is(err, desktop.ErrPublic), errors.Is(err, desktop.ErrUnavailable):
		return "The public key lookup failed. Check 1Password desktop integration and the selected item, then refresh."
	case errors.Is(err, engine.ErrUnknownKey):
		return "Select a key with a current public index."
	case errors.Is(err, engine.ErrLocked):
		return "Unlock opssh before loading a private key."
	case errors.Is(err, engine.ErrLoadTimeout), errors.Is(err, context.DeadlineExceeded):
		return "The 1Password request timed out."
	case errors.Is(err, context.Canceled):
		return "The request was canceled."
	case errors.Is(err, engine.ErrKeyMismatch):
		return "The private key no longer matches the public index. Refresh the item."
	case errors.Is(err, engine.ErrInvalidKey):
		return "The selected private key has an unsupported or invalid format."
	default:
		return "The request could not be completed."
	}
}
