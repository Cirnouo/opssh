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

	"opssh/internal/activity"
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

func TestWindowsOpenSSHListsAndSignsWithSameLogonApproval(t *testing.T) {
	systemSSH := filepath.Join(os.Getenv("SystemRoot"), "System32", "OpenSSH")
	client := filepath.Join(systemSSH, "ssh.exe")
	sshAdd := filepath.Join(systemSSH, "ssh-add.exe")
	keygen := filepath.Join(systemSSH, "ssh-keygen.exe")
	for _, file := range []string{client, sshAdd, keygen} {
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
		if p.AppCandidate < 0 || p.AppCandidate >= len(p.Chain) {
			t.Errorf("approval candidate %d is outside the verified chain", p.AppCandidate)
			return engine.Approval{}, engine.ErrDenied
		}
		return engine.Approval{Scope: "all"}, nil
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
	history := activity.New()
	defer history.Close()
	done := make(chan struct{})
	go func() { defer close(done); acceptSSH(ctx, listener, agent, history, nil) }()
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
	dir := t.TempDir()
	config := filepath.Join(dir, "ssh_config")
	if err := os.WriteFile(config, []byte("Host github\n HostName example.test\n User git\n IdentityAgent SSH_AUTH_SOCK\n IdentitiesOnly yes\n IdentityFile test.pub\n"), 0600); err != nil {
		t.Fatal(err)
	}
	effective, err := command(client, "-G", "-F", config, "github").Output()
	options := strings.ToLower(strings.ReplaceAll(string(effective), "\r\n", "\n"))
	if err != nil || !strings.Contains(options, "\nidentityagent ssh_auth_sock\n") ||
		!strings.Contains(options, "\nidentitiesonly yes\n") || !strings.Contains(options, "\nidentityfile test.pub\n") {
		t.Fatal("the offline github alias did not resolve the intended agent and identity options")
	}
	out, err := command(sshAdd, "-L").CombinedOutput()
	if err != nil || !bytes.HasPrefix(out, bytes.TrimSpace(ssh.MarshalAuthorizedKey(public))) {
		t.Fatalf("ssh-add -L returned %s (%v)", out, err)
	}
	if store.loads.Load() != 0 || promptCount.Load() != 0 {
		t.Fatal("enumeration opened the key or requested approval")
	}
	keyFile := filepath.Join(dir, "test.pub")
	signers := filepath.Join(dir, "allowed_signers")
	messageFile := filepath.Join(dir, "offline")
	signatureFile := messageFile + ".sig"
	if err := os.WriteFile(keyFile, ssh.MarshalAuthorizedKey(public), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(signers, append([]byte("test "), ssh.MarshalAuthorizedKey(public)...), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(messageFile, []byte("offline message"), 0600); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		// System OpenSSH can block on Go's anonymous stdin pipe in an
		// SSH session. Use a regular file for both sides of the round trip.
		if out, err := command(keygen, "-Y", "sign", "-f", keyFile, "-n", "file", messageFile).CombinedOutput(); err != nil {
			t.Fatalf("ssh-keygen sign: %v %s", err, out)
		}
		input, err := os.Open(messageFile)
		if err != nil {
			t.Fatal(err)
		}
		verify := command(keygen, "-Y", "verify", "-f", signers, "-I", "test", "-n", "file", "-s", signatureFile)
		verify.Stdin = input
		out, err := verify.CombinedOutput()
		input.Close()
		if err != nil {
			t.Fatalf("OpenSSH rejected signature: %v %s", err, out)
		}
		if err := os.Remove(signatureFile); err != nil {
			t.Fatal(err)
		}
	}
	for {
		entries := history.Page("").Entries
		if len(entries) == 2 && entries[0].Result == "published" && entries[1].Result == "published" {
			break
		}
		select {
		case <-ctx.Done():
			t.Fatal("opssh activity did not record both published OpenSSH signatures")
		case <-time.After(10 * time.Millisecond):
		}
	}
	if prompts, loads := promptCount.Load(), store.loads.Load(); prompts != 1 || loads != 1 {
		t.Fatalf("new ssh-keygen instance did not reuse same-logon approval and loaded key: prompts=%d loads=%d", prompts, loads)
	}
}
