package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/pem"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"

	"opssh/internal/engine"
	"opssh/internal/winipc"
)

type generatedKey struct {
	data  []byte
	loads atomic.Int32
}

func (k *generatedKey) Load(context.Context, engine.Key) ([]byte, error) {
	k.loads.Add(1)
	return bytes.Clone(k.data), nil
}

func TestWindowsOpenSSHListsAndSignsFromNewHostChildren(t *testing.T) {
	systemSSH := filepath.Join(os.Getenv("SystemRoot"), "System32", "OpenSSH")
	sshAdd := filepath.Join(systemSSH, "ssh-add.exe")
	keygen := filepath.Join(systemSSH, "ssh-keygen.exe")
	for _, file := range []string{sshAdd, keygen} {
		if _, err := os.Stat(file); err != nil {
			t.Fatalf("Windows system OpenSSH is required: %v", err)
		}
	}
	pub, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	defer clear(private)
	public, err := ssh.NewPublicKey(pub)
	if err != nil {
		t.Fatal(err)
	}
	block, err := ssh.MarshalPrivateKey(private, "")
	if err != nil {
		t.Fatal(err)
	}
	store := &generatedKey{data: pem.EncodeToMemory(block)}
	clear(block.Bytes)
	defer clear(store.data)
	var promptCount atomic.Int32
	opts := engine.DefaultOptions()
	opts.Provider = store
	opts.Keys = []engine.Key{{ID: "test", Name: "Offline", Public: public.Marshal()}}
	opts.Approve = func(_ context.Context, p engine.Prompt) (engine.Approval, error) {
		promptCount.Add(1)
		for i, entry := range p.Chain {
			if i > 0 && entry.PID == uint32(os.Getpid()) {
				return engine.Approval{Scope: "host", Candidate: i}, nil
			}
		}
		return engine.Approval{}, engine.ErrDenied
	}
	agent, err := engine.New(opts)
	if err != nil {
		t.Fatal(err)
	}
	defer agent.Close()
	var token [8]byte
	if _, err := rand.Read(token[:]); err != nil {
		t.Fatal(err)
	}
	listener, err := winipc.ListenNamed(`\\.\pipe\opssh-e2e-` + hex.EncodeToString(token[:]))
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	done := make(chan struct{})
	go func() { defer close(done); acceptSSH(ctx, listener, agent) }()
	defer func() { cancel(); listener.Close(); <-done }()
	env := make([]string, 0)
	for _, v := range os.Environ() {
		if !strings.HasPrefix(strings.ToUpper(v), "SSH_AUTH_SOCK=") {
			env = append(env, v)
		}
	}
	env = append(env, "SSH_AUTH_SOCK="+listener.Addr())
	command := func(program string, args ...string) *exec.Cmd {
		c := exec.CommandContext(ctx, program, args...)
		c.Env = env
		return c
	}
	out, err := command(sshAdd, "-L").CombinedOutput()
	if err != nil || !bytes.HasPrefix(out, bytes.TrimSpace(ssh.MarshalAuthorizedKey(public))) {
		t.Fatalf("ssh-add -L returned %s (%v)", out, err)
	}
	if store.loads.Load() != 0 || promptCount.Load() != 0 {
		t.Fatal("enumeration opened the key or requested approval")
	}
	dir := t.TempDir()
	keyFile := filepath.Join(dir, "test.pub")
	signers := filepath.Join(dir, "allowed_signers")
	signatureFile := filepath.Join(dir, "offline.sig")
	if err := os.WriteFile(keyFile, ssh.MarshalAuthorizedKey(public), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(signers, append([]byte("test "), ssh.MarshalAuthorizedKey(public)...), 0600); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		cmd := command(keygen, "-Y", "sign", "-f", keyFile, "-n", "file")
		cmd.Stdin = strings.NewReader("offline message")
		var stderr bytes.Buffer
		cmd.Stderr = &stderr
		signature, err := cmd.Output()
		if err != nil {
			t.Fatalf("ssh-keygen sign: %v %s", err, &stderr)
		}
		if err := os.WriteFile(signatureFile, signature, 0600); err != nil {
			t.Fatal(err)
		}
		verify := command(keygen, "-Y", "verify", "-f", signers, "-I", "test", "-n", "file", "-s", signatureFile)
		verify.Stdin = strings.NewReader("offline message")
		if out, err := verify.CombinedOutput(); err != nil {
			t.Fatalf("OpenSSH rejected signature: %v %s", err, out)
		}
	}
	if prompts, loads := promptCount.Load(), store.loads.Load(); prompts != 1 || loads != 1 {
		t.Fatalf("new ssh-keygen instance did not reuse host approval and loaded key: prompts=%d loads=%d", prompts, loads)
	}
}
