// Package activity keeps bounded, in-memory metadata for final SSH sign requests.
package activity

import (
	"context"
	"crypto/rand"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"
)

const (
	maxEntries      = 200
	pageSize        = 32
	maxLabelRunes   = 256
	maxIDBytes      = 64
	maxCommandUTF16 = 32767
	captureTimeout  = 3 * time.Second
	captureWorkers  = 2
)

type Entry struct {
	ID           string    `json:"id"`
	App          string    `json:"app"`
	PID          uint32    `json:"pid"`
	Key          string    `json:"key"`
	Result       string    `json:"result"`
	Time         time.Time `json:"time"`
	CommandReady bool      `json:"commandReady"`
}

type Page struct {
	Entries []Entry `json:"entries"`
	Total   int     `json:"total"`
	Next    string  `json:"next,omitempty"`
}

// Source owns one retained, verified requesting process instance.
type Source interface {
	CommandLine(context.Context) string
	Close()
}

type Log struct {
	mu          sync.Mutex
	generation  context.Context
	cancel      context.CancelFunc
	rows        [maxEntries]*Attempt
	next        int
	count       int
	commands    map[*Attempt]string
	workers     chan struct{}
	workersDone sync.WaitGroup
}

type Attempt struct {
	log        *Log
	generation context.Context
	entry      Entry
	capturing  bool
	finished   bool
	evicted    bool
}

func New() *Log {
	ctx, cancel := context.WithCancel(context.Background())
	return &Log{
		generation: ctx,
		cancel:     cancel,
		commands:   make(map[*Attempt]string),
		workers:    make(chan struct{}, captureWorkers),
	}
}

// Begin binds the eventual outcome to the history as it exists at request start.
func (l *Log) Begin() *Attempt {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.generation == nil {
		return nil
	}
	return &Attempt{log: l, generation: l.generation}
}

// Capture retains the requester only when a worker is available. It performs
// the command-line lookup independently of signing.
func (a *Attempt) Capture(retain func() (Source, bool)) {
	if a == nil {
		return
	}
	l := a.log
	l.mu.Lock()
	if l.generation != a.generation || a.capturing || a.finished {
		l.mu.Unlock()
		return
	}
	select {
	case l.workers <- struct{}{}:
		a.capturing = true
		l.workersDone.Add(1)
	default:
		l.mu.Unlock()
		return
	}
	l.mu.Unlock()

	source, ok := retain()
	if !ok {
		<-l.workers
		l.workersDone.Done()
		return
	}
	ctx, cancel := context.WithTimeout(a.generation, captureTimeout)
	go func() {
		defer l.workersDone.Done()
		defer func() {
			cancel()
			source.Close()
			<-l.workers
		}()
		if ctx.Err() != nil {
			return
		}
		command := source.CommandLine(ctx)
		if !validCommand(command) {
			return
		}
		l.mu.Lock()
		defer l.mu.Unlock()
		if l.generation == a.generation && !a.evicted && ctx.Err() == nil {
			l.commands[a] = command
		}
	}()
}

// Finish records one final outcome. App is a verified process image path and is
// reduced to its display name before storage.
func (a *Attempt) Finish(entry Entry) {
	if a == nil {
		return
	}
	l := a.log
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.generation != a.generation || a.finished {
		return
	}
	entry.ID = rand.Text()
	entry.App = displayLabel(entry.App[strings.LastIndexAny(entry.App, `\/`)+1:])
	entry.Key = displayLabel(entry.Key)
	entry.Time = time.Now().UTC()
	entry.CommandReady = false
	a.entry = entry
	a.finished = true
	if old := l.rows[l.next]; old != nil {
		old.evicted = true
		delete(l.commands, old)
	} else {
		l.count++
	}
	l.rows[l.next] = a
	l.next = (l.next + 1) % len(l.rows)
}

func (l *Log) newest(index int) *Attempt {
	return l.rows[(l.next+len(l.rows)-1-index)%len(l.rows)]
}

// Page returns newest-first metadata. A cursor identifies the last row of the
// preceding page, so new entries do not shift the continuation.
func (l *Log) Page(before string) Page {
	l.mu.Lock()
	defer l.mu.Unlock()
	page := Page{Entries: make([]Entry, 0, min(pageSize, l.count)), Total: l.count}
	start := 0
	if before != "" {
		index := l.find(before)
		if index < 0 {
			return page
		}
		start = index + 1
	}
	end := min(start+pageSize, l.count)
	for index := start; index < end; index++ {
		row := l.newest(index)
		entry := row.entry
		_, entry.CommandReady = l.commands[row]
		page.Entries = append(page.Entries, entry)
	}
	if end < l.count {
		page.Next = l.newest(end - 1).entry.ID
	}
	return page
}

func (l *Log) Command(id string) string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if index := l.find(id); index >= 0 {
		return l.commands[l.newest(index)]
	}
	return ""
}

func (l *Log) find(id string) int {
	if id != "" && len(id) <= maxIDBytes {
		for index := range l.count {
			if l.newest(index).entry.ID == id {
				return index
			}
		}
	}
	return -1
}

func (l *Log) Clear() {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.generation != nil {
		l.reset()
		l.generation, l.cancel = context.WithCancel(context.Background())
	}
}

func (l *Log) Close() {
	l.mu.Lock()
	if l.generation != nil {
		l.reset()
		l.generation, l.cancel = nil, nil
	}
	l.mu.Unlock()
	l.workersDone.Wait()
}

func (l *Log) reset() {
	l.cancel()
	clear(l.rows[:])
	clear(l.commands)
	l.next, l.count = 0, 0
}

func displayLabel(value string) string {
	var label strings.Builder
	count := 0
	for _, character := range value {
		if count == maxLabelRunes {
			label.WriteRune('…')
			break
		}
		if unicode.IsControl(character) || unicode.Is(unicode.Bidi_Control, character) ||
			character == '\u2028' || character == '\u2029' {
			character = '�'
		}
		label.WriteRune(character)
		count++
	}
	return label.String()
}

func validCommand(command string) bool {
	if len(command) == 0 || len(command) > maxCommandUTF16*3 || !utf8.ValidString(command) {
		return false
	}
	units := 0
	for _, character := range command {
		if character == 0 {
			return false
		}
		units++
		if character > 0xffff {
			units++
		}
		if units > maxCommandUTF16 {
			return false
		}
	}
	return true
}
