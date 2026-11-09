package main

import (
	"context"
	"errors"
	"net"
	"strconv"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/agentwire"
	"opssh/internal/config"
	"opssh/internal/control"
	"opssh/internal/engine"
	"opssh/internal/winipc"
)

type startup struct {
	readConfig func() (string, config.Settings, error)
	listen     func() (*winipc.Listener, error)
	newVault   func(string) vault
}

func run(args []string, boot startup) error {
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
		return startupError(ch, "The public configuration is invalid or unreadable.")
	}
	listener, err := boot.listen()
	if err != nil {
		return startupError(ch, "The SSH agent pipe is unavailable or already in use.")
	}
	defer listener.Close()
	app, err := newApplication(path, listener.Addr(), settings, ch, boot.newVault)
	if err != nil {
		return startupError(ch, "The public key index could not be opened.")
	}
	defer app.engine.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if err := ch.Send(control.Event("hello", "", map[string]string{"pipe": listener.Addr(), "version": version})); err != nil {
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
	go func() {
		defer cancel()
		acceptSSH(ctx, listener, app.engine)
	}()
	return ch.Run(ctx, app.command, app.priority)
}

func startupError(ch *control.Channel, message string) error {
	_ = ch.Send(control.Event("fatal", "", map[string]string{"error": message}))
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

func acceptSSH(ctx context.Context, listener *winipc.Listener, e *engine.Engine) {
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
			_ = agentwire.Serve(ctx, conn, sshHandler{conn: conn, engine: e})
		}()
	}
}

type sshHandler struct {
	conn   *winipc.Conn
	engine *engine.Engine
}

func (h sshHandler) List() []agentwire.Identity {
	keys := h.engine.List()
	ids := make([]agentwire.Identity, 0, len(keys))
	for _, k := range keys {
		ids = append(ids, agentwire.Identity{Blob: k.Public, Comment: k.Name})
	}
	return ids
}

func (h sshHandler) Sign(ctx context.Context, public, data []byte, flags uint32, publish func(*ssh.Signature) error) error {
	client, err := h.conn.Peer()
	if err != nil {
		return engine.ErrPeerGone
	}
	defer client.Close()
	if len(client.Chain) == 0 {
		return engine.ErrPeerGone
	}
	peer := engine.Peer{
		Chain:     make([]engine.Process, 0, len(client.Chain)),
		Alive:     func(p engine.Instance) bool { return client.Alive(p.PID, p.Created) },
		Connected: h.conn.Connected,
	}
	for _, p := range client.Chain {
		peer.Chain = append(peer.Chain, engine.Process{Instance: engine.Instance{PID: p.PID, Created: p.Created}, Path: p.Path})
	}
	ctx, cancel := context.WithCancel(ctx)
	stopped := make(chan struct{})
	go watchRequester(ctx, cancel, peer, stopped)
	defer func() { cancel(); <-stopped }()
	return h.engine.Sign(ctx, peer, public, data, flags, publish)
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
