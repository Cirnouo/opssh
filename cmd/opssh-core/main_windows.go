package main

import (
	"context"
	"errors"
	"net"
	"strconv"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/activity"
	"opssh/internal/agentwire"
	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/diagnostics"
	"opssh/internal/engine"
	"opssh/internal/winipc"
)

type startup struct {
	readConfig func() (string, config.Settings, error)
	endpoint   func() (string, error)
	listen     func() (*winipc.Listener, error)
	newVault   func(string) vault
}

func run(args []string, boot startup) error {
	candidate := len(args) == 5 && args[4] == "--candidate"
	if candidate {
		args = args[:4]
	}
	pipe, tray, err := launchHandles(args)
	if err != nil {
		return err
	}
	private, err := winipc.AdoptControl(pipe, tray)
	if err != nil {
		return err
	}
	defer private.Close()
	ch := control.New(private)
	defer ch.Close()
	path, settings, err := boot.readConfig()
	if err != nil {
		return startupError(ch, "invalid-configuration", "The public configuration is invalid or unreadable.")
	}
	address, err := boot.endpoint()
	if err != nil {
		return startupError(ch, "pipe-unavailable", "The SSH agent pipe is unavailable or already in use.")
	}
	app, err := newApplication(path, address, settings, ch, boot.newVault)
	if err != nil {
		return startupError(ch, "index-unreadable", "The public key index could not be opened.")
	}
	defer app.engine.Close()
	defer app.activity.Close()
	app.candidate = candidate
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	server := &sshEndpoint{ctx: ctx, fail: cancel, open: boot.listen, app: app}
	defer server.stop()
	defer server.setDefaultAgent(false)
	if !candidate {
		if err := server.start(); err != nil {
			return startupError(ch, "pipe-unavailable", "The SSH agent pipe is unavailable or already in use.")
		}
	}
	if err := ch.Send(control.Event("hello", "", map[string]string{"pipe": address, "version": version})); err != nil {
		return err
	}
	go func() {
		select {
		case <-private.TrayDone():
			cancel()
		case <-ch.Done():
			cancel()
		case <-ctx.Done():
		}
	}()
	return ch.Run(ctx, server.command, app.priority)
}

// The command worker is the only writer of the endpoint during its lifetime.
// Closing the control channel cancels and joins the worker before cleanup.
type sshEndpoint struct {
	ctx         context.Context
	fail        context.CancelFunc
	open        func() (*winipc.Listener, error)
	app         *application
	close       context.CancelFunc
	done        chan struct{}
	listener    *winipc.Listener
	alias       *winipc.Listener
	closeAlias  context.CancelFunc
	aliasDone   chan struct{}
	pausedAlias bool
}

func (s *sshEndpoint) start() error {
	listener, err := s.open()
	if err != nil {
		return errPipeUnavailable
	}
	if listener.Addr() != s.app.endpoint {
		_ = listener.Close()
		return errPipeUnavailable
	}
	s.listener = listener
	ctx, cancel := context.WithCancel(s.ctx)
	done := make(chan struct{})
	s.close, s.done = cancel, done
	go func() {
		defer close(done)
		acceptSSH(ctx, listener, s.app.engine, s.app.activity, &s.app.diagnostics)
		if ctx.Err() == nil {
			s.fail()
		}
	}()
	return nil
}

func (s *sshEndpoint) stop() {
	if s.listener == nil {
		return
	}
	s.close()
	_ = s.listener.Close()
	<-s.done
	s.listener = nil
}

func (s *sshEndpoint) command(ctx context.Context, msg control.Message) control.Result {
	a := s.app
	a.defaultAgent = s.defaultAgentEnabled()
	switch msg.Type {
	case "default-agent":
		var request struct {
			Enabled *bool `json:"enabled"`
		}
		if a.candidate || a.paused || a.restartRequired || control.DecodeBody(msg, &request) != nil || request.Enabled == nil {
			return commandResult(errCommand)
		}
		if err := s.setDefaultAgent(*request.Enabled); err != nil {
			return commandResult(err)
		}
		a.defaultAgent = s.defaultAgentEnabled()
		return control.Result{OK: true}
	case "pause-ssh":
		if len(msg.Body) != 0 || a.candidate || a.restartRequired {
			return commandResult(errCommand)
		}
		if !a.paused {
			a.paused = true
			a.engine.SuspendIndex()
			s.pausedAlias = s.defaultAgentEnabled()
			_ = s.setDefaultAgent(false)
			a.defaultAgent = false
			s.stop()
		}
		return control.Result{OK: true, Data: map[string]bool{"defaultAgent": s.pausedAlias}}
	case "resume-ssh":
		if len(msg.Body) != 0 || a.candidate || a.restartRequired {
			return commandResult(errCommand)
		}
		if a.paused {
			current, err := config.Read(a.path)
			if err != nil || current.Account != a.settings.Account {
				return commandResult(errAccountChanged)
			}
			if err := s.start(); err != nil {
				return commandResult(err)
			}
			if s.pausedAlias {
				if err := s.setDefaultAgent(true); err != nil {
					s.stop()
					return commandResult(err)
				}
			}
			a.defaultAgent = s.defaultAgentEnabled()
			a.paused = false
			a.engine.ResumeIndex()
		}
		return commandResult(nil)
	case "activate-account":
		var request struct {
			DefaultAgent bool `json:"defaultAgent"`
		}
		if (len(msg.Body) != 0 && control.DecodeBody(msg, &request) != nil) || !a.candidate || a.prepared == nil {
			return commandResult(errCommand)
		}
		// Reserve the same endpoint before the durable account commit, but
		// hide all old identities while it is being handed over.
		a.engine.SuspendIndex()
		if err := s.start(); err != nil {
			return commandResult(err)
		}
		if request.DefaultAgent {
			if err := s.setDefaultAgent(true); err != nil {
				s.stop()
				return commandResult(err)
			}
		}
		if err := a.activateAccount(ctx); err != nil {
			_ = s.setDefaultAgent(false)
			s.stop()
			return commandResult(err)
		}
		a.defaultAgent = s.defaultAgentEnabled()
		a.engine.ResumeIndex()
		result := commandResult(nil)
		result.State = a.status()
		return result
	default:
		return a.command(ctx, msg)
	}
}

func startupError(ch *control.Channel, code, message string) error {
	_ = ch.Send(control.Event("fatal", "", map[string]string{"errorCode": code, "error": message}))
	return errors.New(message)
}

func launchHandles(args []string) (uintptr, uintptr, error) {
	if len(args) != 4 || args[0] != "--control-handle" || args[2] != "--tray-handle" {
		return 0, 0, errors.New("launch the tray to start the core")
	}
	a, errA := strconv.ParseUint(args[1], 10, strconv.IntSize)
	b, errB := strconv.ParseUint(args[3], 10, strconv.IntSize)
	if errA != nil || errB != nil || a == 0 || b == 0 || a == b {
		return 0, 0, errors.New("invalid inherited handles")
	}
	return uintptr(a), uintptr(b), nil
}

func acceptSSH(ctx context.Context, listener *winipc.Listener, e *engine.Engine, history *activity.Log, events *diagnostics.Ring) {
	stop := context.AfterFunc(ctx, func() { listener.Close() })
	defer stop()
	for {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		go func() {
			defer conn.Close()
			closeOnCancel := context.AfterFunc(ctx, func() { conn.Close() })
			defer closeOnCancel()
			_ = agentwire.Serve(ctx, conn, sshHandler{conn: conn, engine: e, history: history, events: events})
		}()
	}
}

type sshHandler struct {
	conn    *winipc.Conn
	engine  *engine.Engine
	history *activity.Log
	events  *diagnostics.Ring
}

func (h sshHandler) List() []agentwire.Identity {
	keys := h.engine.List()
	ids := make([]agentwire.Identity, 0, len(keys))
	for _, k := range keys {
		ids = append(ids, agentwire.Identity{Blob: k.Public, Comment: k.Name})
	}
	return ids
}

func (h sshHandler) Sign(ctx context.Context, public, data []byte, flags uint32, publish func(*ssh.Signature) error) (result error) {
	diagnostic := h.events.Begin()
	trace := diagnostic.Trace()
	peerStart := trace.Start()
	attempt := h.history.Begin()
	client, err := h.conn.Peer()
	if err == nil {
		defer client.Close()
	}
	if err != nil || len(client.Chain) == 0 {
		trace.Add(diagnostics.Peer, "unverified", peerStart)
		h.events.Finish(diagnostic, activityResult(engine.ErrPeerGone))
		return engine.ErrPeerGone
	}
	trace.Add(diagnostics.Peer, "verified", peerStart)
	entry := activity.Entry{App: client.Chain[0].Path, PID: client.Chain[0].PID}
	attempt.Capture(func() (activity.Source, bool) { return client.RetainRequester() })
	defer func() {
		entry.Result = activityResult(result)
		h.events.Finish(diagnostic, entry.Result)
		attempt.Finish(entry)
	}()
	peer := engine.Peer{
		Chain:       make([]engine.Process, 0, len(client.Chain)),
		Alive:       func(p engine.Instance) bool { return client.Alive(p.PID, p.Created) },
		Connected:   h.conn.Connected,
		ApprovalApp: client.ApprovalApp,
		Retain:      func(p engine.Instance) (func() bool, func(), bool) { return client.Retain(p.PID, p.Created) },
	}
	for _, p := range client.Chain {
		peer.Chain = append(peer.Chain, engine.Process{Instance: engine.Instance{PID: p.PID, Created: p.Created}, Path: p.Path})
	}
	ctx, cancel := context.WithCancel(diagnostics.WithTrace(ctx, trace))
	stopped := make(chan struct{})
	go watchRequester(ctx, cancel, peer, stopped)
	defer func() { cancel(); <-stopped }()
	return h.engine.SignObserved(ctx, peer, public, data, flags, publish, func(observed engine.SignObservation) {
		entry.Key = observed.KeyName
		if observed.Anchor.PID != 0 {
			entry.App, entry.PID = observed.Anchor.Path, observed.Anchor.PID
		}
	})
}

func watchRequester(ctx context.Context, cancel context.CancelFunc, peer engine.Peer, stopped chan<- struct{}) {
	defer close(stopped)
	tick := time.NewTicker(100 * time.Millisecond)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
			if !peer.Connected() || !peer.Alive(peer.Chain[0].Instance) {
				cancel()
				return
			}
		}
	}
}

var _ net.Conn = (*winipc.Conn)(nil)
