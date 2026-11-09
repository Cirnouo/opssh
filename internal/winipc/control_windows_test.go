package winipc

import (
	"errors"
	"math"
	"testing"

	"golang.org/x/sys/windows"
)

func TestInheritedHandleValueRange(t *testing.T) {
	for _, test := range []struct {
		name  string
		value uintptr
		valid bool
	}{
		{"zero", 0, false},
		{"smallest positive", 1, true},
		{"largest positive", math.MaxInt, true},
		{"sign bit", uintptr(math.MaxInt) + 1, false},
		{"all bits", ^uintptr(0), false},
	} {
		t.Run(test.name, func(t *testing.T) {
			if valid := validInheritedHandle(test.value); valid != test.valid {
				t.Fatalf("value %#x: valid = %t, want %t", test.value, valid, test.valid)
			}
		})
	}
}

func TestAdoptControlConsumesCompanionOnInvalidHandle(t *testing.T) {
	for _, test := range []struct {
		name string
		pair func(handle uintptr) (uintptr, uintptr)
	}{
		{"invalid pipe", func(handle uintptr) (uintptr, uintptr) { return uintptr(math.MaxInt) + 1, handle }},
		{"invalid tray", func(handle uintptr) (uintptr, uintptr) { return handle, ^uintptr(0) }},
		{"same handle", func(handle uintptr) (uintptr, uintptr) { return handle, handle }},
	} {
		t.Run(test.name, func(t *testing.T) {
			handle, err := windows.CreateEvent(nil, 1, 0, nil)
			if err != nil {
				t.Fatal(err)
			}
			pipe, tray := test.pair(uintptr(handle))
			if control, err := AdoptControl(pipe, tray); err == nil {
				_ = control.Close()
				t.Fatal("adopted an invalid handle pair")
			}
			if err := windows.SetHandleInformation(handle, windows.HANDLE_FLAG_INHERIT, 0); !errors.Is(err, windows.ERROR_INVALID_HANDLE) {
				_ = windows.CloseHandle(handle)
				t.Fatalf("companion handle was not closed: %v", err)
			}
		})
	}
}
