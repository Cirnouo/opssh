package winipc

import "testing"

func TestApprovalAppPrefersTheNearestLiveWindowOwner(t *testing.T) {
	chain := []Process{
		{PID: 31, Created: 300}, // ssh.exe
		{PID: 29, Created: 200}, // shell
		{PID: 25, Created: 100}, // terminal host
		{PID: 22, Created: 50},
	}
	for _, test := range []struct {
		name   string
		owners [maxChain]bool
		dead   uint32
		want   int
		owns   bool
	}{
		{name: "terminal behind a headless shell", owners: [maxChain]bool{2: true, 3: true}, want: 2, owns: true},
		{name: "requester has a window", owners: [maxChain]bool{0: true, 2: true}, want: 0, owns: true},
		{name: "nearest owner exited", owners: [maxChain]bool{1: true, 2: true}, dead: 29, want: 2, owns: true},
		{name: "verified requester fallback", owners: [maxChain]bool{2: true}, dead: 25, want: 0},
	} {
		t.Run(test.name, func(t *testing.T) {
			got, owns := closestWindowProcess(chain, test.owners, func(pid uint32, _ uint64) bool { return pid != test.dead })
			if got != test.want || owns != test.owns {
				t.Fatalf("candidate = (%d, %t), want (%d, %t)", got, owns, test.want, test.owns)
			}
		})
	}
}
