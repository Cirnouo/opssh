package winipc

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"
)

var testSequence atomic.Uint32

func testName() string {
	return fmt.Sprintf("\\\\.\\pipe\\opssh-test-%d-%d", os.Getpid(), testSequence.Add(1))
}

func testListener(t *testing.T) *Listener {
	t.Helper()
	l, err := ListenNamed(testName())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = l.Close() })
	return l
}

func openClient(t *testing.T, name string) windows.Handle {
	t.Helper()
	// Match Windows OpenSSH's desired access and identification level.
	handle, err := windows.CreateFile(windows.StringToUTF16Ptr(name), windows.GENERIC_READ|windows.GENERIC_WRITE, 0,
		nil, windows.OPEN_EXISTING, windows.FILE_FLAG_OVERLAPPED|windows.SECURITY_SQOS_PRESENT|windows.SECURITY_IDENTIFICATION, 0)
	if err != nil {
		t.Fatal(err)
	}
	return handle
}

func TestListenerRejectsAnOccupiedName(t *testing.T) {
	l := testListener(t)
	if other, err := ListenNamed(l.Addr()); err == nil {
		_ = other.Close()
		t.Fatal("opened a listener on an occupied name")
	}
}

func TestOpenSSHAccessAndReusableInstances(t *testing.T) {
	l := testListener(t)

	// READ_CONTROL is available, but ownership must confer no WRITE_DAC.
	handle, err := windows.CreateFile(windows.StringToUTF16Ptr(l.Addr()), windows.WRITE_DAC, 0, nil,
		windows.OPEN_EXISTING, 0, 0)
	if !errors.Is(err, windows.ERROR_ACCESS_DENIED) {
		if err == nil {
			_ = windows.CloseHandle(handle)
		}
		t.Fatalf("WRITE_DAC was not rejected: %v", err)
	}

	for batch := range 3 {
		var clients []io.ReadWriteCloser
		for i := range 8 {
			handle := openClient(t, l.Addr())
			client, err := winio.NewOpenFile(handle)
			if err != nil {
				_ = windows.CloseHandle(handle)
				t.Fatal(err)
			}
			defer client.Close()
			clients = append(clients, client)
			if _, err := client.Write([]byte{byte(batch*8 + i)}); err != nil {
				t.Fatal(err)
			}
		}
		got := make(map[byte]bool)
		for range clients {
			conn, err := l.Accept()
			if err != nil {
				t.Fatal(err)
			}
			if err := conn.SetReadDeadline(time.Now().Add(2 * time.Second)); err != nil {
				t.Fatal(err)
			}
			var b [1]byte
			if _, err := io.ReadFull(conn, b[:]); err != nil {
				t.Fatal(err)
			}
			got[b[0]] = true
			if err := conn.Close(); err != nil {
				t.Fatal(err)
			}
		}
		for i, client := range clients {
			if !got[byte(batch*8+i)] {
				t.Fatalf("batch %d lost a client request", batch)
			}
			_ = client.Close()
		}
	}
}

func TestPeerTracksRequesterAndLiveParent(t *testing.T) {
	if os.Getenv("OPSSH_TEST_PIPE") != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		conn, err := winio.DialPipeAccessImpLevel(ctx, os.Getenv("OPSSH_TEST_PIPE"),
			windows.GENERIC_READ|windows.GENERIC_WRITE, winio.PipeImpLevelIdentification)
		if err != nil {
			t.Fatal("connect failed")
		}
		defer conn.Close()
		if _, err := conn.Write([]byte{1}); err != nil {
			t.Fatal(err)
		}
		_, _ = io.ReadFull(conn, make([]byte, 1))
		return
	}

	l := testListener(t)
	exe, err := os.Executable()
	if err != nil {
		t.Fatal("could not resolve test executable")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, "-test.run=^TestPeerTracksRequesterAndLiveParent$")
	cmd.Env = append(os.Environ(), "OPSSH_TEST_PIPE="+l.Addr())
	if err := cmd.Start(); err != nil {
		t.Fatal("could not start helper")
	}
	t.Cleanup(func() {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
	})
	stop := context.AfterFunc(ctx, func() { _ = l.Close() })
	defer stop()
	conn, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()

	if _, err := io.ReadFull(conn, make([]byte, 1)); err != nil {
		t.Fatal(err)
	}
	peer, err := conn.Peer()
	if err != nil {
		t.Fatal(err)
	}
	defer peer.Close()
	if len(peer.Chain) < 2 {
		t.Fatal("requester and parent were not both verified")
	}
	child, parent := peer.Chain[0], peer.Chain[1]
	if child.PID != uint32(cmd.Process.Pid) || parent.PID != uint32(os.Getpid()) ||
		child.Created <= parent.Created || !strings.EqualFold(filepath.Base(child.Path), filepath.Base(exe)) {
		t.Fatal("wrong requester or parent identity")
	}
	if !peer.Alive(child.PID, child.Created) || peer.Alive(child.PID, child.Created-1) || !conn.Connected() {
		t.Fatal("incorrect identity or connection liveness")
	}
	if _, err := conn.Write([]byte{1}); err != nil {
		t.Fatal(err)
	}
	if err := cmd.Wait(); err != nil {
		t.Fatal(err)
	}
	if peer.Alive(child.PID, child.Created) || conn.Connected() || !peer.Alive(parent.PID, parent.Created) {
		t.Fatal("client exit was not observed")
	}
}

func TestListenerCloseInterruptsAcceptAndRead(t *testing.T) {
	l := testListener(t)
	handle := openClient(t, l.Addr())
	defer windows.CloseHandle(handle)
	conn, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	if err := conn.SetReadDeadline(time.Now().Add(10 * time.Second)); err != nil {
		t.Fatal(err)
	}
	pending := make(chan error, 2)
	go func() {
		_, err := conn.Read(make([]byte, 1))
		pending <- err
	}()
	go func() {
		_, err := l.Accept()
		pending <- err
	}()
	if err := l.Close(); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		select {
		case err := <-pending:
			if !errors.Is(err, net.ErrClosed) {
				t.Fatalf("unexpected shutdown error: %v", err)
			}
		case <-time.After(2 * time.Second):
			t.Fatal("operation remained blocked after close")
		}
	}
	// Retain the client handle and ensure the endpoint can still be recreated.
	reopened, err := ListenNamed(l.Addr())
	if err != nil {
		t.Fatal(err)
	}
	_ = reopened.Close()
}

func TestPeerRequiresNewlyReadRequestData(t *testing.T) {
	l := testListener(t)
	client, err := winio.NewOpenFile(openClient(t, l.Addr()))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	conn, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if _, err := client.Write([]byte{1}); err != nil {
		t.Fatal(err)
	}
	if _, err := io.ReadFull(conn, make([]byte, 1)); err != nil {
		t.Fatal(err)
	}
	peer, err := conn.Peer()
	if err != nil {
		t.Fatal(err)
	}
	_ = peer.Close()
	if stale, err := conn.Peer(); err == nil {
		_ = stale.Close()
		t.Fatal("reused the previous request's client context")
	}
}

func TestConnectedDetectsAClosedPipeWithUnreadData(t *testing.T) {
	l := testListener(t)
	client, err := winio.NewOpenFile(openClient(t, l.Addr()))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	conn, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if _, err := client.Write([]byte{1}); err != nil {
		t.Fatal(err)
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
	if conn.Connected() {
		t.Fatal("reported a closed connection with unread data as live")
	}
}

func TestInheritedControlReportsTrayExit(t *testing.T) {
	exe, err := os.Executable()
	if err != nil {
		t.Fatal("could not resolve test executable")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, "-test.run=^TestControlTrayProcess$")
	cmd.Env = append(os.Environ(), "OPSSH_CONTROL_TEST_HELPER=1")
	stdin, err := cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := cmd.Start(); err != nil {
		t.Fatal("could not start helper")
	}
	t.Cleanup(func() {
		_ = stdin.Close()
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
	})
	remote, err := windows.OpenProcess(windows.PROCESS_DUP_HANDLE, false, uint32(cmd.Process.Pid))
	if err != nil {
		t.Fatal(err)
	}
	defer windows.CloseHandle(remote)
	var remoteClient uintptr
	if _, err := fmt.Fscan(stdout, &remoteClient); err != nil {
		t.Fatal("could not read helper handle")
	}
	duplicate := func() windows.Handle {
		t.Helper()
		var client windows.Handle
		if err := windows.DuplicateHandle(remote, windows.Handle(remoteClient), windows.CurrentProcess(),
			&client, 0, true, windows.DUPLICATE_SAME_ACCESS); err != nil {
			t.Fatal(err)
		}
		return client
	}
	wrongTray, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION|windows.SYNCHRONIZE, false, uint32(os.Getpid()))
	if err != nil {
		t.Fatal(err)
	}
	if invalid, err := AdoptControl(uintptr(duplicate()), uintptr(wrongTray)); err == nil {
		_ = invalid.Close()
		t.Fatal("accepted a process unrelated to the control pipe")
	}
	tray, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION|windows.SYNCHRONIZE, true, uint32(cmd.Process.Pid))
	if err != nil {
		t.Fatal(err)
	}
	control, err := AdoptControl(uintptr(duplicate()), uintptr(tray))
	if err != nil {
		t.Fatal(err)
	}
	defer control.Close()
	if err := control.SetReadDeadline(time.Now().Add(40 * time.Millisecond)); err != nil {
		t.Fatal(err)
	}
	if _, err := control.Read(make([]byte, 1)); !errors.Is(err, os.ErrDeadlineExceeded) {
		t.Fatalf("expected a control read deadline, got %v", err)
	}
	if err := control.SetReadDeadline(time.Time{}); err != nil {
		t.Fatal(err)
	}
	read := make(chan error, 1)
	go func() {
		_, err := control.Read(make([]byte, 1))
		read <- err
	}()
	_ = stdin.Close()
	if err := cmd.Wait(); err != nil {
		t.Fatal(err)
	}
	select {
	case <-control.TrayDone():
	case <-time.After(2 * time.Second):
		t.Fatal("tray exit was not observed")
	}
	select {
	case err := <-read:
		if err == nil {
			t.Fatal("read succeeded after tray exit")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("read remained blocked after tray exit")
	}
}

func TestControlTrayProcess(t *testing.T) {
	if os.Getenv("OPSSH_CONTROL_TEST_HELPER") != "1" {
		return
	}
	name := testName()
	server, err := windows.CreateNamedPipe(windows.StringToUTF16Ptr(name),
		windows.PIPE_ACCESS_DUPLEX|windows.FILE_FLAG_OVERLAPPED, windows.PIPE_REJECT_REMOTE_CLIENTS,
		1, 4096, 4096, 0, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer windows.CloseHandle(server)
	client := openClient(t, name)
	defer windows.CloseHandle(client)
	fmt.Println(uintptr(client))
	_, _ = io.Copy(io.Discard, os.Stdin)
}

func TestWindowsOpenSSHListsAnEmptyIsolatedAgent(t *testing.T) {
	sshAdd := filepath.Join(os.Getenv("SystemRoot"), "System32", "OpenSSH", "ssh-add.exe")
	if _, err := os.Stat(sshAdd); errors.Is(err, os.ErrNotExist) {
		t.Skip("Windows OpenSSH client is unavailable")
	} else if err != nil {
		t.Fatal("could not inspect Windows OpenSSH client")
	}
	l := testListener(t)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	stop := context.AfterFunc(ctx, func() { _ = l.Close() })
	defer stop()
	cmd := exec.CommandContext(ctx, sshAdd, "-l")
	cmd.Env = append(os.Environ(), "SSH_AUTH_SOCK="+l.Addr())
	var stdout bytes.Buffer
	cmd.Stdout = &stdout
	if err := cmd.Start(); err != nil {
		t.Fatal("could not launch Windows OpenSSH client")
	}
	t.Cleanup(func() {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
	})
	conn, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	var request [5]byte
	if _, err := io.ReadFull(conn, request[:]); err != nil || request != [5]byte{0, 0, 0, 1, 11} {
		t.Fatal("did not receive an SSH agent identities request")
	}
	peer, err := conn.Peer()
	if err != nil {
		t.Fatal(err)
	}
	defer peer.Close()
	if len(peer.Chain) == 0 || peer.Chain[0].PID != uint32(cmd.Process.Pid) {
		t.Fatal("did not authenticate Windows OpenSSH requester")
	}
	if _, err := conn.Write([]byte{0, 0, 0, 5, 12, 0, 0, 0, 0}); err != nil {
		t.Fatal(err)
	}
	var exit *exec.ExitError
	if err := cmd.Wait(); !errors.As(err, &exit) || exit.ExitCode() != 1 || !bytes.Contains(stdout.Bytes(), []byte("The agent has no identities.")) {
		t.Fatal("Windows OpenSSH did not recognize the empty identities response")
	}
}
