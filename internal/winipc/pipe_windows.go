// Package winipc owns the Windows named pipes and the process handles used to
// authenticate their peers.
package winipc

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"net"
	"runtime"
	"strings"
	"sync"
	"time"
	"unsafe"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"
)

const (
	localPrefix   = "\\\\.\\pipe\\"
	instanceCount = 16
	pipeBuffer    = 32 * 1024
)

type pipeFile interface {
	io.ReadWriteCloser
	SetReadDeadline(time.Time) error
	SetWriteDeadline(time.Time) error
}

type Listener struct {
	name      string
	identity  logonIdentity
	accept    chan *Conn
	done      chan struct{}
	stopped   windows.Handle
	workers   sync.WaitGroup
	stopOnce  sync.Once
	closeOnce sync.Once
	stopErr   error
	closeErr  error
}

// Listen opens the opt-in public endpoint for this Windows logon.
func Listen() (*Listener, error) {
	id, err := currentLogon()
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256([]byte(id.sid.String()))
	return listenNamed(fmt.Sprintf("%sopssh-agent-%x", localPrefix, sum[:8]), id)
}

// ListenNamed opens a byte-mode endpoint in the local pipe namespace.
func ListenNamed(name string) (*Listener, error) {
	id, err := currentLogon()
	if err != nil {
		return nil, err
	}
	return listenNamed(name, id)
}

func listenNamed(name string, id logonIdentity) (*Listener, error) {
	suffix, ok := strings.CutPrefix(name, localPrefix)
	if !ok || suffix == "" || len(name) > 256 || strings.ContainsAny(suffix, "\\/\x00") {
		return nil, errors.New("invalid local pipe name")
	}
	wideName, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return nil, errors.New("invalid local pipe name")
	}

	// Create every instance before publishing it to the server. The first
	// handle can replace the temporary DACL and the existing handles retain
	// the access they need to disconnect and listen again.
	sid := id.sid.String()
	initialSD, err := windows.SecurityDescriptorFromString("D:P(A;;RC;;;OW)(A;;GRGW;;;" + sid + ")")
	if err != nil {
		return nil, err
	}
	sa := windows.SecurityAttributes{
		Length:             uint32(unsafe.Sizeof(windows.SecurityAttributes{})),
		SecurityDescriptor: initialSD,
	}
	finalSD, err := windows.SecurityDescriptorFromString("D:P(A;;RC;;;OW)(A;;0x12019b;;;" + sid + ")")
	if err != nil {
		return nil, err
	}
	dacl, _, err := finalSD.DACL()
	if err != nil {
		return nil, err
	}

	var instances []pipeInstance
	failed := true
	defer func() {
		if failed {
			for _, instance := range instances {
				_ = instance.file.Close()
			}
		}
	}()
	for i := range instanceCount {
		mode := uint32(windows.PIPE_ACCESS_DUPLEX | windows.FILE_FLAG_OVERLAPPED)
		if i == 0 {
			mode |= windows.FILE_FLAG_FIRST_PIPE_INSTANCE | windows.WRITE_DAC
		}
		handle, err := windows.CreateNamedPipe(wideName, mode, windows.PIPE_REJECT_REMOTE_CLIENTS,
			instanceCount, pipeBuffer, pipeBuffer, 0, &sa)
		if err != nil {
			return nil, fmt.Errorf("create public pipe instance: %w", err)
		}
		file, err := openPipeFile(handle)
		if err != nil {
			_ = windows.CloseHandle(handle)
			return nil, err
		}
		instances = append(instances, pipeInstance{handle: handle, file: file})
	}

	// NPFS stores the security descriptor on the named pipe, shared across
	// all instances. This mask permits native OpenSSH's client open and omits
	// FILE_CREATE_PIPE_INSTANCE. OWNER RIGHTS removes implicit WRITE_DAC.
	if err := windows.SetSecurityInfo(instances[0].handle, windows.SE_KERNEL_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil, nil, dacl, nil); err != nil {
		return nil, fmt.Errorf("protect public pipe: %w", err)
	}

	stopped, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		return nil, err
	}
	l := &Listener{name: name, identity: id, accept: make(chan *Conn), done: make(chan struct{}), stopped: stopped}
	l.workers.Add(len(instances))
	for _, instance := range instances {
		go l.serve(instance)
	}
	failed = false
	return l, nil
}

func (l *Listener) Addr() string { return l.name }

func (l *Listener) Accept() (*Conn, error) {
	select {
	case <-l.done:
		return nil, l.stopErr
	case conn := <-l.accept:
		select {
		case <-l.done:
			_ = conn.Close()
			return nil, l.stopErr
		default:
			return conn, nil
		}
	}
}

func (l *Listener) stop(err error) {
	l.stopOnce.Do(func() {
		l.stopErr = err
		close(l.done)
		_ = windows.SetEvent(l.stopped)
	})
}

func (l *Listener) Close() error {
	l.closeOnce.Do(func() {
		l.stop(net.ErrClosed)
		l.workers.Wait()
		l.closeErr = windows.CloseHandle(l.stopped)
	})
	return l.closeErr
}

type pipeInstance struct {
	handle windows.Handle
	file   pipeFile
}

func (l *Listener) serve(instance pipeInstance) {
	defer l.workers.Done()
	defer instance.file.Close()
	event, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		l.stop(err)
		return
	}
	defer windows.CloseHandle(event)
	for {
		select {
		case <-l.done:
			return
		default:
		}
		err := l.connect(instance.handle, event)
		switch err {
		case net.ErrClosed:
			return
		case windows.ERROR_NO_DATA:
			// A client opened and closed before ConnectNamedPipe.
		case nil:
			if client, err := l.identity.requester(instance.handle); err == nil {
				conn := &Conn{instance: instance, identity: l.identity, name: pipeAddr(l.name), client: client, done: make(chan struct{})}
				select {
				case l.accept <- conn:
					select {
					case <-conn.done:
					case <-l.done:
						_ = conn.Close()
					}
				case <-l.done:
					_ = conn.Close()
				}
			}
		default:
			l.stop(fmt.Errorf("connect public pipe: %w", err))
			return
		}
		if err := windows.DisconnectNamedPipe(instance.handle); err != nil && err != windows.ERROR_PIPE_NOT_CONNECTED {
			l.stop(fmt.Errorf("reset public pipe: %w", err))
			return
		}
	}
}

func (l *Listener) connect(handle, event windows.Handle) error {
	if err := windows.ResetEvent(event); err != nil {
		return err
	}
	// The low bit suppresses completion packets for this operation because
	// the handle's read/write operations use the go-winio completion port.
	op := &windows.Overlapped{HEvent: event | 1}
	var pin runtime.Pinner
	pin.Pin(op)
	defer pin.Unpin()
	err := windows.ConnectNamedPipe(handle, op)
	switch err {
	case windows.ERROR_PIPE_CONNECTED:
		return nil
	case windows.ERROR_IO_PENDING:
		// Put the stop event first so it wins when both events are signaled.
		wait, err := windows.WaitForMultipleObjects([]windows.Handle{l.stopped, event}, false, windows.INFINITE)
		var n uint32
		if err == nil && wait == windows.WAIT_OBJECT_0+1 {
			return windows.GetOverlappedResult(handle, op, &n, false)
		}
		_ = windows.CancelIoEx(handle, op)
		_ = windows.GetOverlappedResult(handle, op, &n, true)
		if err != nil {
			return err
		}
		return net.ErrClosed
	default:
		return err
	}
}

type pipeAddr string

func (a pipeAddr) Network() string { return "npipe" }
func (a pipeAddr) String() string  { return string(a) }

// Conn represents one connection generation of a reusable pipe instance.
type Conn struct {
	instance pipeInstance
	identity logonIdentity
	name     pipeAddr
	client   pinnedProcess
	done     chan struct{}

	mu            sync.Mutex
	readMu        sync.Mutex
	readSincePeer bool
	inflight      sync.WaitGroup
	closed        bool
	closeOnce     sync.Once
}

var _ net.Conn = (*Conn)(nil)

func (c *Conn) beginIO() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return false
	}
	c.inflight.Add(1)
	return true
}

func (c *Conn) Read(b []byte) (int, error) {
	c.readMu.Lock()
	defer c.readMu.Unlock()
	if !c.beginIO() {
		return 0, net.ErrClosed
	}
	defer c.inflight.Done()
	n, err := c.instance.file.Read(b)
	c.readSincePeer = c.readSincePeer || n > 0
	return n, c.ioError(err)
}

func (c *Conn) Write(b []byte) (int, error) {
	if !c.beginIO() {
		return 0, net.ErrClosed
	}
	defer c.inflight.Done()
	n, err := c.instance.file.Write(b)
	return n, c.ioError(err)
}

func (c *Conn) ioError(err error) error {
	if err != nil {
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.closed {
			return net.ErrClosed
		}
	}
	return err
}

func (c *Conn) Close() error {
	c.closeOnce.Do(func() {
		c.mu.Lock()
		c.closed = true
		_ = c.instance.file.SetReadDeadline(time.Now())
		_ = c.instance.file.SetWriteDeadline(time.Now())
		c.mu.Unlock()

		c.inflight.Wait()
		_ = c.instance.file.SetReadDeadline(time.Time{})
		_ = c.instance.file.SetWriteDeadline(time.Time{})
		_ = windows.CloseHandle(c.client.handle)
		close(c.done)
	})
	return nil
}

func (c *Conn) LocalAddr() net.Addr  { return c.name }
func (c *Conn) RemoteAddr() net.Addr { return c.name }

func (c *Conn) SetDeadline(deadline time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return net.ErrClosed
	}
	return errors.Join(c.instance.file.SetReadDeadline(deadline), c.instance.file.SetWriteDeadline(deadline))
}

func (c *Conn) SetReadDeadline(deadline time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return net.ErrClosed
	}
	return c.instance.file.SetReadDeadline(deadline)
}

func (c *Conn) SetWriteDeadline(deadline time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return net.ErrClosed
	}
	return c.instance.file.SetWriteDeadline(deadline)
}

func (c *Conn) Connected() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return !c.closed && c.client.alive() && pipeConnected(c.instance.handle)
}

func openPipeFile(handle windows.Handle) (pipeFile, error) {
	file, err := winio.NewOpenFile(handle)
	if err != nil {
		return nil, err
	}
	return file.(pipeFile), nil
}

type pipeLocalInfo struct {
	pipeType            uint32
	configuration       uint32
	maxInstances        uint32
	currentInstances    uint32
	inboundQuota        uint32
	readDataAvailable   uint32
	outboundQuota       uint32
	writeQuotaAvailable uint32
	state               uint32
	end                 uint32
}

func queryPipeLocalInfo(handle windows.Handle) (pipeLocalInfo, error) {
	// FilePipeLocalInformation exposes the closing state even when unread
	// data remains queued. The query does not consume or wait for pipe data.
	const filePipeLocalInformation = 24
	var status windows.IO_STATUS_BLOCK
	var info pipeLocalInfo
	err := windows.NtQueryInformationFile(handle, &status, (*byte)(unsafe.Pointer(&info)),
		uint32(unsafe.Sizeof(info)), filePipeLocalInformation)
	return info, err
}

func pipeConnected(handle windows.Handle) bool {
	info, err := queryPipeLocalInfo(handle)
	return err == nil && info.state == windows.FILE_PIPE_CONNECTED_STATE
}
