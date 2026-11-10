package winipc

import (
	"context"
	"runtime"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	maxDesktopWindows = 4096
	dwmwaCloaked      = 14
	gwOwner           = 4
	gwlStyle          = 0xfffffff0 // GWL_STYLE (-16)
	gwlExStyle        = 0xffffffec // GWL_EXSTYLE (-20)
	wsChild           = 0x40000000
	wsExToolWindow    = 0x00000080
	wsExNoActivate    = 0x08000000
)

var (
	approvalUser32         = windows.NewLazySystemDLL("user32.dll")
	enumDesktopWindows     = approvalUser32.NewProc("EnumDesktopWindows")
	getWindowThreadProcess = approvalUser32.NewProc("GetWindowThreadProcessId")
	isWindowVisible        = approvalUser32.NewProc("IsWindowVisible")
	getWindow              = approvalUser32.NewProc("GetWindow")
	getShellWindow         = approvalUser32.NewProc("GetShellWindow")
	getWindowLong          = approvalUser32.NewProc("GetWindowLongW")
	getWindowRect          = approvalUser32.NewProc("GetWindowRect")
	dwmGetWindowAttribute  = windows.NewLazySystemDLL("dwmapi.dll").NewProc("DwmGetWindowAttribute")
	approvalWindowCallback = windows.NewCallback(inspectApprovalWindow)
)

type desktopSearch struct {
	ctx     context.Context
	chain   []Process
	owners  [maxChain]bool
	visited int
	shell   uintptr
}

// ApprovalApp selects a verified window owner by chain index. The requester
// at index zero is the fallback when no eligible owner is found.
func (p Peer) ApprovalApp(ctx context.Context) (index int, ownsWindow bool) {
	if len(p.Chain) == 0 || len(p.Chain) > maxChain || ctx.Err() != nil {
		return 0, false
	}
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	shell, _, _ := getShellWindow.Call()
	search := desktopSearch{ctx: ctx, chain: p.Chain, shell: shell}
	// A null desktop handle limits enumeration to this thread's desktop.
	// The callback runs synchronously on the same thread.
	ok, _, _ := enumDesktopWindows.Call(0, approvalWindowCallback, uintptr(unsafe.Pointer(&search)))
	runtime.KeepAlive(&search)
	if uint32(ok) == 0 || ctx.Err() != nil {
		return 0, false
	}
	return closestWindowProcess(p.Chain, search.owners, p.Alive)
}

func closestWindowProcess(chain []Process, owners [maxChain]bool, alive func(uint32, uint64) bool) (int, bool) {
	for i, process := range chain {
		if owners[i] && alive(process.PID, process.Created) {
			return i, true
		}
	}
	return 0, false
}

func inspectApprovalWindow(window uintptr, search *desktopSearch) uintptr {
	if search.visited >= maxDesktopWindows || search.ctx.Err() != nil {
		return 0
	}
	search.visited++
	if window == search.shell {
		return 1
	}
	pid, thread := windowProcess(window)
	if thread != 0 {
		for i, process := range search.chain {
			if process.PID == pid && !search.owners[i] && eligibleApprovalWindow(window) {
				// Re-read the owner after the other queries in case the window
				// was destroyed and the handle reused during enumeration.
				currentPID, currentThread := windowProcess(window)
				search.owners[i] = currentPID == pid && currentThread == thread
				break
			}
		}
	}
	return 1
}

func windowProcess(window uintptr) (pid, thread uint32) {
	id, _, _ := getWindowThreadProcess.Call(window, uintptr(unsafe.Pointer(&pid)))
	return pid, uint32(id)
}

func eligibleApprovalWindow(window uintptr) bool {
	visible, _, _ := isWindowVisible.Call(window)
	if uint32(visible) == 0 {
		return false
	}
	owner, _, _ := getWindow.Call(window, gwOwner)
	style, _, _ := getWindowLong.Call(window, gwlStyle)
	exStyle, _, _ := getWindowLong.Call(window, gwlExStyle)
	if owner != 0 || style&wsChild != 0 || exStyle&(wsExToolWindow|wsExNoActivate) != 0 {
		return false
	}
	var rect struct{ left, top, right, bottom int32 }
	ok, _, _ := getWindowRect.Call(window, uintptr(unsafe.Pointer(&rect)))
	if uint32(ok) == 0 || rect.right <= rect.left || rect.bottom <= rect.top {
		return false
	}
	var cloaked uint32
	result, _, _ := dwmGetWindowAttribute.Call(window, dwmwaCloaked,
		uintptr(unsafe.Pointer(&cloaked)), unsafe.Sizeof(cloaked))
	return uint32(result) == 0 && cloaked == 0
}
