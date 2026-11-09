package main

import (
	"context"

	"opssh/internal/winipc"
)

const defaultAgentPipe = `\\.\pipe\openssh-ssh-agent`

func (s *sshEndpoint) setDefaultAgent(enabled bool) error {
	if enabled {
		if s.alias != nil {
			return nil
		}
		listener, err := winipc.ListenNamed(defaultAgentPipe)
		if err != nil {
			return errPipeUnavailable
		}
		ctx, cancel := context.WithCancel(s.ctx)
		done := make(chan struct{})
		s.alias, s.closeAlias, s.aliasDone = listener, cancel, done
		go func() {
			defer close(done)
			defer listener.Close()
			acceptSSH(ctx, listener, s.app.engine, s.app.activity)
		}()
		return nil
	}
	if s.alias != nil {
		s.closeAlias()
		_ = s.alias.Close()
		<-s.aliasDone
		s.alias = nil
	}
	return nil
}

func (s *sshEndpoint) defaultAgentEnabled() bool {
	if s.alias == nil {
		return false
	}
	select {
	case <-s.aliasDone:
		s.alias = nil
		return false
	default:
		return true
	}
}
