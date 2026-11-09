package winipc

import (
	"encoding/binary"
	"strings"
	"testing"
	"unicode/utf16"

	"golang.org/x/sys/windows"
)

func commandUTF16LE(text string) []byte {
	var data []byte
	for _, unit := range utf16.Encode([]rune(text)) {
		data = binary.LittleEndian.AppendUint16(data, unit)
	}
	return data
}

func TestCommandMetadataPreservesQuotedAndUnicodeText(t *testing.T) {
	const want = "fixture.exe \"quoted value\" 雪🌨\r\n"
	if got := decodeCommandLine(commandUTF16LE(want)); got != want {
		t.Fatal("command text changed during UTF-16 decoding")
	}
}

func TestCommandMetadataRejectsMalformedAndOversizedOutput(t *testing.T) {
	valid := strings.Repeat("a", 32765) + "🌨"
	if decodeCommandLine(commandUTF16LE(valid)) != valid {
		t.Fatal("the largest supported UTF-16 command was lost")
	}
	for name, data := range map[string][]byte{
		"too long":           commandUTF16LE(valid + "b"),
		"odd byte count":     {0x61},
		"unpaired surrogate": {0x00, 0xd8, 0x61, 0x00},
		"embedded null":      {0x61, 0x00, 0x00, 0x00},
	} {
		t.Run(name, func(t *testing.T) {
			if decodeCommandLine(data) != "" {
				t.Fatal("invalid command data was retained")
			}
		})
	}
}

func TestCommandMetadataBoundsTheOutputWriter(t *testing.T) {
	output := commandOutput{data: make([]byte, 0, maxCommandBytes)}
	if n, err := output.Write(make([]byte, maxCommandBytes)); err != nil || n != maxCommandBytes {
		t.Fatal("the largest supported command was not buffered")
	}
	if n, err := output.Write([]byte{0x61}); err == nil || n != 0 || len(output.data) != maxCommandBytes {
		t.Fatal("the command pipe accepted output beyond its byte bound")
	}
}

func TestRetainRequesterUsesTheVerifiedInstanceAfterPeerClose(t *testing.T) {
	logon, err := currentLogon()
	if err != nil {
		t.Fatal("could not resolve the current test logon")
	}
	pinned, err := logon.pin(windows.GetCurrentProcessId())
	if err != nil {
		t.Fatal("could not pin the current test process")
	}
	peer := Peer{
		Chain: []Process{{PID: pinned.PID + 1, Created: pinned.Created + 1}},
		state: &peerState{processes: []pinnedProcess{pinned}},
	}
	t.Cleanup(func() { _ = peer.Close() })

	requester, ok := peer.RetainRequester()
	if !ok {
		t.Fatal("could not retain the verified requester")
	}
	t.Cleanup(requester.Close)
	if err := peer.Close(); err != nil {
		t.Fatal("could not close original peer")
	}
	if requester.process.PID != pinned.PID || requester.process.Created != pinned.Created ||
		!requester.process.alive() {
		t.Fatal("retained requester did not keep the verified process instance")
	}
}
