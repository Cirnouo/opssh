package main

import (
	"os"
	"os/exec"
	"path/filepath"

	"golang.org/x/sys/windows"
)

func main() {
	if launchTray() {
		return
	}

	message := "Could not start opssh. Extract the entire ZIP and keep the opssh-runtime folder beside opssh-tray.exe." +
		"\n\n无法启动 opssh。请完整解压 ZIP，并将 opssh-runtime 文件夹与 opssh-tray.exe 保留在同一目录。"
	text, _ := windows.UTF16PtrFromString(message)
	title, _ := windows.UTF16PtrFromString("opssh")
	_, _ = windows.MessageBox(0, text, title, windows.MB_OK|windows.MB_ICONERROR)
	os.Exit(1)
}

func launchTray() bool {
	executable, err := os.Executable()
	if err != nil || !filepath.IsAbs(executable) {
		return false
	}

	runtime := filepath.Join(filepath.Dir(executable), "opssh-runtime")
	tray := exec.Command(filepath.Join(runtime, "opssh-tray.exe"), os.Args[1:]...)
	tray.Dir = runtime
	if err := tray.Start(); err != nil {
		return false
	}
	_ = tray.Process.Release()
	return true
}
