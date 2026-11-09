package winipc

import (
	"errors"
	"fmt"
	"math"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Control owns the connected pipe and exact tray process handle inherited at
// launch. A signaled tray process interrupts any pending control I/O.
type Control struct {
	pipeFile
	tray          windows.Handle
	stop          windows.Handle
	trayDone      chan struct{}
	watcherDone   chan struct{}
	fileCloseOnce sync.Once
	closeOnce     sync.Once
	fileErr       error
	closeErr      error
}

// AdoptControl consumes both inherited handles, including on failure.
func AdoptControl(pipeValue, trayValue uintptr) (*Control, error) {
	pipe, tray := windows.Handle(pipeValue), windows.Handle(trayValue)
	pipeInRange, trayInRange := validInheritedHandle(pipeValue), validInheritedHandle(trayValue)
	adopted := false
	defer func() {
		if !adopted {
			if pipeInRange {
				_ = windows.CloseHandle(pipe)
			}
			if trayInRange && tray != pipe {
				_ = windows.CloseHandle(tray)
			}
		}
	}()
	errHandle := errors.New("invalid inherited tray or control handle")
	if !pipeInRange || !trayInRange || pipe == tray {
		return nil, errHandle
	}
	if err := windows.SetHandleInformation(pipe, windows.HANDLE_FLAG_INHERIT, 0); err != nil {
		return nil, errHandle
	}
	if err := windows.SetHandleInformation(tray, windows.HANDLE_FLAG_INHERIT, 0); err != nil {
		return nil, errHandle
	}
	if err := validateControl(pipe, tray); err != nil {
		return nil, err
	}
	stop, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		return nil, fmt.Errorf("create control stop event: %w", err)
	}
	file, err := openPipeFile(pipe)
	if err != nil {
		_ = windows.CloseHandle(stop)
		return nil, fmt.Errorf("open inherited control pipe: %w", err)
	}

	control := &Control{pipeFile: file, tray: tray, stop: stop, trayDone: make(chan struct{}), watcherDone: make(chan struct{})}
	adopted = true
	go control.watchTray()
	return control, nil
}

func validInheritedHandle(value uintptr) bool {
	return value != 0 && value <= math.MaxInt
}

func validateControl(pipe, tray windows.Handle) error {
	badPipe := errors.New("inherited control handle must be a connected local overlapped byte pipe")
	info, err := queryPipeLocalInfo(pipe)
	const localBytePipe = windows.FILE_PIPE_BYTE_STREAM_TYPE | windows.FILE_PIPE_REJECT_REMOTE_CLIENTS
	if err != nil || info.end != windows.FILE_PIPE_CLIENT_END || info.pipeType != localBytePipe ||
		info.configuration != windows.FILE_PIPE_FULL_DUPLEX || info.state != windows.FILE_PIPE_CONNECTED_STATE {
		return badPipe
	}
	const fileModeInformation = 16
	var status windows.IO_STATUS_BLOCK
	var mode uint32
	if err := windows.NtQueryInformationFile(pipe, &status, (*byte)(unsafe.Pointer(&mode)),
		uint32(unsafe.Sizeof(mode)), fileModeInformation); err != nil ||
		mode&(windows.FILE_SYNCHRONOUS_IO_ALERT|windows.FILE_SYNCHRONOUS_IO_NONALERT) != 0 {
		return badPipe
	}

	mismatch := errors.New("inherited control pipe does not belong to a live tray")
	pid, err := windows.GetProcessId(tray)
	if err != nil || pid == 0 {
		return mismatch
	}
	var server, client uint32
	if err := windows.GetNamedPipeServerProcessId(pipe, &server); err != nil || server != pid {
		return mismatch
	}
	if err := windows.GetNamedPipeClientProcessId(pipe, &client); err != nil || client != pid {
		return mismatch
	}
	if status, err := windows.WaitForSingleObject(tray, 0); err != nil || status != uint32(windows.WAIT_TIMEOUT) {
		return mismatch
	}
	return nil
}

func (c *Control) SetDeadline(deadline time.Time) error {
	return errors.Join(c.SetReadDeadline(deadline), c.SetWriteDeadline(deadline))
}

func (c *Control) TrayDone() <-chan struct{} { return c.trayDone }

func (c *Control) watchTray() {
	defer close(c.watcherDone)
	status, err := windows.WaitForMultipleObjects([]windows.Handle{c.tray, c.stop}, false, windows.INFINITE)
	if err != nil || status != windows.WAIT_OBJECT_0+1 {
		close(c.trayDone)
		c.closeFile()
	}
}

func (c *Control) closeFile() {
	c.fileCloseOnce.Do(func() {
		c.fileErr = c.pipeFile.Close()
	})
}

func (c *Control) Close() error {
	c.closeOnce.Do(func() {
		_ = windows.SetEvent(c.stop)
		<-c.watcherDone
		c.closeFile()
		c.closeErr = errors.Join(c.fileErr, windows.CloseHandle(c.stop), windows.CloseHandle(c.tray))
	})
	return c.closeErr
}
