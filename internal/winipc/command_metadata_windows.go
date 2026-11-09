package winipc

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
	"unicode"
	"unicode/utf16"
	"unsafe"

	"golang.org/x/sys/windows"
)

const maxCommandUTF16 = 32767
const maxCommandBytes = 2 * maxCommandUTF16

// Requester owns a duplicate of the verified requesting process handle. One
// worker reads and closes it independently of the request's peer lifetime.
type Requester struct {
	process pinnedProcess
}

func (p Peer) RetainRequester() (*Requester, bool) {
	if p.state == nil {
		return nil, false
	}
	p.state.mu.Lock()
	defer p.state.mu.Unlock()
	if len(p.state.processes) == 0 {
		return nil, false
	}
	process, ok := p.state.processes[0].duplicate()
	if !ok {
		return nil, false
	}
	return &Requester{process: process}, true
}

func (r *Requester) Close() {
	if r.process.handle != 0 {
		_ = windows.CloseHandle(r.process.handle)
		r.process.handle = 0
	}
}

// Use the inbox CIM module in local COM mode. Only the verified PID goes into
// this script. The WMI string goes directly to a private stdout pipe as UTF-16LE.
const wmiCommandScript = `
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Microsoft.PowerShell.Core\Import-Module "$PSHOME\Modules\CimCmdlets\CimCmdlets.psd1"
$p = CimCmdlets\Get-CimInstance -Namespace 'root\cimv2' -Query 'SELECT ProcessId, CommandLine FROM Win32_Process WHERE ProcessId = %d' -OperationTimeoutSec 2
if ($null -eq $p -or $p -is [array] -or $p.ProcessId -ne %d) { exit }
$line = $p.CommandLine
if ($null -eq $line -or $line.Length -eq 0 -or $line.Length -gt %d) { exit }
$encoding = [Text.UnicodeEncoding]::new($false, $false, $true)
$data = $encoding.GetBytes($line)
[Console]::OpenStandardOutput().Write($data, 0, $data.Length)
`

// CommandLine returns an empty value if the process exits, WMI cannot obtain
// the metadata, or the operation is canceled. It exposes no helper diagnostics.
func (r *Requester) CommandLine(ctx context.Context) string {
	if ctx.Err() != nil || !r.process.alive() {
		return ""
	}
	systemDir, err := windows.GetSystemDirectory()
	if err != nil {
		return ""
	}
	psHome := filepath.Join(systemDir, "WindowsPowerShell", "v1.0")
	script := fmt.Sprintf(wmiCommandScript, r.process.PID, r.process.PID, maxCommandUTF16)
	var scriptUTF16 []byte
	for _, unit := range utf16.Encode([]rune(script)) {
		scriptUTF16 = binary.LittleEndian.AppendUint16(scriptUTF16, unit)
	}
	cmd := exec.CommandContext(ctx, filepath.Join(psHome, "powershell.exe"),
		"-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
		base64.StdEncoding.EncodeToString(scriptUTF16))
	cmd.Dir = psHome
	cmd.Env = []string{"SystemRoot=" + filepath.Dir(systemDir), "PSModulePath=" + filepath.Join(psHome, "Modules")}
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: windows.CREATE_NO_WINDOW}
	cmd.WaitDelay = 250 * time.Millisecond
	output := commandOutput{data: make([]byte, 0, maxCommandBytes)}
	defer func() { clear(output.data) }()
	cmd.Stdout = &output
	// The same retained kernel object must remain live through the PID query.
	// If it exited, a WMI result for a recycled PID is discarded.
	if err := runCommandQuery(cmd); err != nil || ctx.Err() != nil || !r.process.alive() {
		return ""
	}
	return decodeCommandLine(output.data)
}

// Retain the helper in a kill-on-close job so an interrupted core can also
// release the PowerShell process and any handles it inherited.
func runCommandQuery(cmd *exec.Cmd) error {
	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(job)
	limits := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{}
	limits.BasicLimitInformation.LimitFlags = windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
	if _, err := windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation,
		uintptr(unsafe.Pointer(&limits)), uint32(unsafe.Sizeof(limits))); err != nil {
		return err
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	var assignErr error
	handleErr := cmd.Process.WithHandle(func(handle uintptr) {
		assignErr = windows.AssignProcessToJobObject(job, windows.Handle(handle))
	})
	if err := errors.Join(handleErr, assignErr); err != nil {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return err
	}
	return cmd.Wait()
}

type commandOutput struct {
	data []byte
}

func (o *commandOutput) Write(p []byte) (int, error) {
	if len(p) > maxCommandBytes-len(o.data) {
		return 0, errors.New("process metadata output exceeded its bound")
	}
	o.data = append(o.data, p...)
	return len(p), nil
}

func decodeCommandLine(data []byte) string {
	if len(data) == 0 || len(data) > maxCommandBytes || len(data)%2 != 0 {
		return ""
	}
	var command strings.Builder
	command.Grow(len(data) / 2)
	for len(data) != 0 {
		character := rune(binary.LittleEndian.Uint16(data))
		data = data[2:]
		if character == 0 {
			return ""
		}
		if utf16.IsSurrogate(character) {
			if len(data) < 2 {
				return ""
			}
			character = utf16.DecodeRune(character, rune(binary.LittleEndian.Uint16(data)))
			if character == unicode.ReplacementChar {
				return ""
			}
			data = data[2:]
		}
		command.WriteRune(character)
	}
	return command.String()
}
