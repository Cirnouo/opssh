package winipc

import (
	"crypto/sha256"
	"errors"
	"net"
	"os"
	"runtime"
	"sync"
	"unsafe"

	"golang.org/x/sys/windows"
)

const maxChain = 16

var (
	getNamedPipeClientSessionID = windows.NewLazySystemDLL("kernel32.dll").NewProc("GetNamedPipeClientSessionId")
	impersonateNamedPipeClient  = windows.NewLazySystemDLL("advapi32.dll").NewProc("ImpersonateNamedPipeClient")
	errIdentity                 = errors.New("cannot verify local pipe client identity")
)

// Process identifies one kernel process instance. Created is its Windows
// creation FILETIME, in 100 ns ticks.
type Process struct {
	PID     uint32
	Created uint64
	Path    string
}

type pinnedProcess struct {
	Process
	handle windows.Handle
}

func (p pinnedProcess) alive() bool {
	return processHandleAlive(p.handle)
}

func processHandleAlive(handle windows.Handle) bool {
	status, err := windows.WaitForSingleObject(handle, 0)
	return err == nil && status == uint32(windows.WAIT_TIMEOUT)
}

func (p pinnedProcess) duplicate() (pinnedProcess, bool) {
	if !p.alive() {
		return pinnedProcess{}, false
	}
	self := windows.CurrentProcess()
	if err := windows.DuplicateHandle(self, p.handle, self, &p.handle, 0, false, windows.DUPLICATE_SAME_ACCESS); err != nil {
		return pinnedProcess{}, false
	}
	return p, true
}

// Peer owns process handles for the verified requester and any verified live
// ancestors. The caller closes it when the request is retired.
type Peer struct {
	Chain []Process
	Logon [32]byte
	state *peerState
}

type peerState struct {
	mu        sync.Mutex
	processes []pinnedProcess
}

func (p Peer) Alive(pid uint32, created uint64) bool {
	if p.state == nil {
		return false
	}
	p.state.mu.Lock()
	defer p.state.mu.Unlock()
	for _, process := range p.state.processes {
		if process.PID == pid && process.Created == created {
			return process.alive()
		}
	}
	return false
}

// Retain keeps a liveness check for the exact verified instance after the
// request's Peer is closed. The caller owns the returned cleanup.
func (p Peer) Retain(pid uint32, created uint64) (func() bool, func(), bool) {
	if p.state == nil {
		return nil, nil, false
	}
	p.state.mu.Lock()
	defer p.state.mu.Unlock()
	for _, process := range p.state.processes {
		if process.PID == pid && process.Created == created {
			retained, ok := process.duplicate()
			if !ok {
				return nil, nil, false
			}
			handle := retained.handle
			return func() bool { return processHandleAlive(handle) }, func() { _ = windows.CloseHandle(handle) }, true
		}
	}
	return nil, nil, false
}

func (p Peer) Close() error {
	if p.state == nil {
		return nil
	}
	p.state.mu.Lock()
	defer p.state.mu.Unlock()
	var err error
	for _, process := range p.state.processes {
		err = errors.Join(err, windows.CloseHandle(process.handle))
	}
	p.state.processes = nil
	return err
}

type logonIdentity struct {
	sid     *windows.SID
	session uint32
	digest  [32]byte
}

func currentLogon() (logonIdentity, error) {
	sid, err := logonSID(windows.GetCurrentProcessToken())
	if err != nil {
		return logonIdentity{}, err
	}
	var session uint32
	if err := windows.ProcessIdToSessionId(windows.GetCurrentProcessId(), &session); err != nil {
		return logonIdentity{}, err
	}
	return logonIdentity{sid: sid, session: session, digest: sha256.Sum256([]byte(sid.String()))}, nil
}

func logonSID(token windows.Token) (*windows.SID, error) {
	groups, err := token.GetTokenGroups()
	if err != nil {
		return nil, err
	}
	for _, group := range groups.AllGroups() {
		const enabledLogon = windows.SE_GROUP_LOGON_ID | windows.SE_GROUP_ENABLED
		if group.Attributes&(enabledLogon|windows.SE_GROUP_USE_FOR_DENY_ONLY) == enabledLogon {
			return group.Sid.Copy()
		}
	}
	return nil, errors.New("token has no Windows logon SID")
}

func (id logonIdentity) requester(pipe windows.Handle) (pinnedProcess, error) {
	var pid, session uint32
	if err := windows.GetNamedPipeClientProcessId(pipe, &pid); err != nil {
		return pinnedProcess{}, errIdentity
	}
	ok, _, _ := getNamedPipeClientSessionID.Call(uintptr(pipe), uintptr(unsafe.Pointer(&session)))
	if ok == 0 || session != id.session {
		return pinnedProcess{}, errIdentity
	}
	return id.pin(pid)
}

func (id logonIdentity) pin(pid uint32) (pinnedProcess, error) {
	if pid == 0 {
		return pinnedProcess{}, errIdentity
	}
	handle, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION|windows.SYNCHRONIZE, false, pid)
	if err != nil {
		return pinnedProcess{}, errIdentity
	}
	p := pinnedProcess{Process: Process{PID: pid}, handle: handle}
	keep := false
	defer func() {
		if !keep {
			_ = windows.CloseHandle(handle)
		}
	}()
	if !p.alive() {
		return pinnedProcess{}, errIdentity
	}
	var session uint32
	if err := windows.ProcessIdToSessionId(pid, &session); err != nil || session != id.session {
		return pinnedProcess{}, errIdentity
	}
	var token windows.Token
	if err := windows.OpenProcessToken(handle, windows.TOKEN_QUERY, &token); err != nil {
		return pinnedProcess{}, errIdentity
	}
	defer token.Close()
	sid, err := logonSID(token)
	if err != nil || !id.sid.Equals(sid) {
		return pinnedProcess{}, errIdentity
	}
	var created, exited, kernel, user windows.Filetime
	if err := windows.GetProcessTimes(handle, &created, &exited, &kernel, &user); err != nil {
		return pinnedProcess{}, errIdentity
	}
	p.Created = uint64(created.HighDateTime)<<32 | uint64(created.LowDateTime)
	path := make([]uint16, 1024)
	n := uint32(len(path))
	err = windows.QueryFullProcessImageName(handle, 0, &path[0], &n)
	if errors.Is(err, windows.ERROR_INSUFFICIENT_BUFFER) {
		path = make([]uint16, 32768)
		n = uint32(len(path))
		err = windows.QueryFullProcessImageName(handle, 0, &path[0], &n)
	}
	if err != nil || n == 0 || p.Created == 0 || !p.alive() {
		return pinnedProcess{}, errIdentity
	}
	p.Path = windows.UTF16ToString(path[:n])
	keep = true
	return p, nil
}

// Peer verifies the security context of the last message read from the pipe.
// Call it after reading a request and before any further reads.
func (c *Conn) Peer() (Peer, error) {
	c.readMu.Lock()
	defer c.readMu.Unlock()

	if !c.readSincePeer {
		return Peer{}, errIdentity
	}
	c.readSincePeer = false
	requester, err := c.requesterForRequest()
	if err != nil {
		return Peer{}, err
	}
	peer := Peer{Chain: []Process{requester.Process}, Logon: c.identity.digest,
		state: &peerState{processes: []pinnedProcess{requester}}}
	parents := processParents()
	child := requester
	for len(peer.Chain) < maxChain {
		parent, err := c.identity.pin(parents[child.PID])
		if err != nil {
			break
		}
		if parent.Created >= child.Created || !child.alive() {
			_ = windows.CloseHandle(parent.handle)
			break
		}
		peer.Chain = append(peer.Chain, parent.Process)
		peer.state.processes = append(peer.state.processes, parent)
		child = parent
	}
	if !c.Connected() {
		_ = peer.Close()
		return Peer{}, errIdentity
	}
	return peer, nil
}

func (c *Conn) requesterForRequest() (pinnedProcess, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return pinnedProcess{}, net.ErrClosed
	}
	if !c.client.alive() || !pipeConnected(c.instance.handle) {
		return pinnedProcess{}, errIdentity
	}
	sid, err := readClientLogon(c.instance.handle)
	if err != nil || !c.identity.sid.Equals(sid) {
		return pinnedProcess{}, errIdentity
	}
	p := c.client
	self := windows.CurrentProcess()
	if err := windows.DuplicateHandle(self, c.client.handle, self, &p.handle, 0, false, windows.DUPLICATE_SAME_ACCESS); err != nil {
		return pinnedProcess{}, errIdentity
	}
	return p, nil
}

func readClientLogon(pipe windows.Handle) (*windows.SID, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	ok, _, _ := impersonateNamedPipeClient.Call(uintptr(pipe))
	if ok == 0 {
		return nil, errIdentity
	}
	defer func() {
		if err := windows.RevertToSelf(); err != nil {
			// This thread must never continue executing in the client's
			// security context or return to Go's thread pool.
			os.Exit(1)
		}
	}()
	var token windows.Token
	if err := windows.OpenThreadToken(windows.CurrentThread(), windows.TOKEN_QUERY, true, &token); err != nil {
		return nil, errIdentity
	}
	defer token.Close()
	return logonSID(token)
}

func processParents() map[uint32]uint32 {
	handle, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil
	}
	defer windows.CloseHandle(handle)
	parents := make(map[uint32]uint32)
	entry := windows.ProcessEntry32{Size: uint32(unsafe.Sizeof(windows.ProcessEntry32{}))}
	err = windows.Process32First(handle, &entry)
	for i := 0; i < 65536 && err == nil; i++ {
		parents[entry.ProcessID] = entry.ParentProcessID
		err = windows.Process32Next(handle, &entry)
	}
	return parents
}
