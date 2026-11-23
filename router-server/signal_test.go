package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
)

// ── Test helpers ─────────────────────────────────────────────────────────────

// resetSignalState clears /signal's package-level state and restores its default config. Call
// it only while no /signal handler is running; startSignalServer's cleanup waits for them.
func resetSignalState() {
	signalMu.Lock()
	signalRooms = make(map[string]*signalRoom)
	signalSockets = make(map[string]int)
	signalMu.Unlock()
	initSignalConfig()
}

// startSignalServer resets all state, applies opts (config changes must happen before the first
// socket opens), and serves /signal. The server and every handler are gone when the test ends.
// It returns the WebSocket URL.
func startSignalServer(t *testing.T, opts ...func()) string {
	t.Helper()
	resetGlobalState()
	resetSignalState()
	rateLimitMax = 1000 // most tests are not about the per-IP limiters
	for _, opt := range opts {
		opt()
	}
	srv := httptest.NewServer(http.HandlerFunc(handleSignal))
	t.Cleanup(func() {
		srv.Close()
		signalWG.Wait()
	})
	return "ws" + strings.TrimPrefix(srv.URL, "http") + "/signal"
}

type fakeClock struct {
	mu sync.Mutex
	t  time.Time
}

func newFakeClock() *fakeClock { return &fakeClock{t: time.Unix(1_800_000_000, 0)} }

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.t
}

func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	c.t = c.t.Add(d)
	c.mu.Unlock()
}

// waitFor polls cond until it holds; the server's view of a closed socket lags the client's.
func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for !cond() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", what)
		}
		time.Sleep(2 * time.Millisecond)
	}
}

func roomState(code string) (exists, hostConnected bool) {
	signalMu.Lock()
	defer signalMu.Unlock()
	room := signalRooms[code]
	if room == nil {
		return false, false
	}
	return true, room.host != nil
}

func openSockets() int {
	signalMu.Lock()
	defer signalMu.Unlock()
	n := 0
	for _, c := range signalSockets {
		n += c
	}
	return n
}

type sigClient struct{ c *websocket.Conn }

func dialSignalE(url string) (*sigClient, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	c, _, err := websocket.Dial(ctx, url, nil)
	if err != nil {
		return nil, err
	}
	c.SetReadLimit(1 << 20) // relayed frames can be 64 KiB
	return &sigClient{c}, nil
}

func dialSignal(t *testing.T, url string) *sigClient {
	t.Helper()
	s, err := dialSignalE(url)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { s.c.CloseNow() })
	return s
}

func (s *sigClient) sendE(msg string) error {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	return s.c.Write(ctx, websocket.MessageText, []byte(msg))
}

func (s *sigClient) send(t *testing.T, msg string) {
	t.Helper()
	if err := s.sendE(msg); err != nil {
		t.Fatalf("send: %v", err)
	}
}

func (s *sigClient) recvRawE() ([]byte, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_, data, err := s.c.Read(ctx)
	return data, err
}

func (s *sigClient) recvE() (map[string]any, error) {
	data, err := s.recvRawE()
	if err != nil {
		return nil, err
	}
	var m map[string]any
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, fmt.Errorf("not JSON: %q", data)
	}
	return m, nil
}

func (s *sigClient) recv(t *testing.T) map[string]any {
	t.Helper()
	m, err := s.recvE()
	if err != nil {
		t.Fatalf("recv: %v", err)
	}
	return m
}

func (s *sigClient) recvType(t *testing.T, typ string) map[string]any {
	t.Helper()
	m := s.recv(t)
	if m["type"] != typ {
		t.Fatalf("expected %q, got %v", typ, m)
	}
	return m
}

// expectClosed asserts the next thing on the socket is its closing.
func (s *sigClient) expectClosed(t *testing.T) {
	t.Helper()
	data, err := s.recvRawE()
	if err == nil {
		t.Fatalf("expected the socket to close, got message %q", data)
	}
	if websocket.CloseStatus(err) == -1 {
		t.Fatalf("expected a close frame, got %v", err)
	}
}

// expectError asserts an error{code} message, then a close.
func (s *sigClient) expectError(t *testing.T, code string) {
	t.Helper()
	m := s.recvType(t, "error")
	if m["code"] != code {
		t.Fatalf("expected error %q, got %v", code, m)
	}
	s.expectClosed(t)
}

func (s *sigClient) register(t *testing.T) (code, token string) {
	t.Helper()
	s.send(t, `{"v":1,"type":"register"}`)
	m := s.recvType(t, "registered")
	return m["code"].(string), m["resumeToken"].(string)
}

func pairE(url string) (host, viewer *sigClient, code string, err error) {
	if host, err = dialSignalE(url); err != nil {
		return
	}
	if err = host.sendE(`{"v":1,"type":"register"}`); err != nil {
		return
	}
	m, err := host.recvE()
	if err != nil || m["type"] != "registered" {
		return nil, nil, "", fmt.Errorf("register: %v %v", m, err)
	}
	code = m["code"].(string)
	if viewer, err = dialSignalE(url); err != nil {
		return
	}
	if err = viewer.sendE(fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code)); err != nil {
		return
	}
	if m, err = viewer.recvE(); err != nil || m["type"] != "joined" {
		return nil, nil, "", fmt.Errorf("join: %v %v", m, err)
	}
	if m, err = host.recvE(); err != nil || m["type"] != "peer-joined" {
		return nil, nil, "", fmt.Errorf("peer-joined: %v %v", m, err)
	}
	return
}

func pair(t *testing.T, url string) (host, viewer *sigClient, code string) {
	t.Helper()
	host, viewer, code, err := pairE(url)
	if err != nil {
		t.Fatalf("pair: %v", err)
	}
	t.Cleanup(func() { host.c.CloseNow(); viewer.c.CloseNow() })
	return
}

// ── Pairing ──────────────────────────────────────────────────────────────────

func TestSignal_PairingHostAndViewer(t *testing.T) {
	url := startSignalServer(t)
	host := dialSignal(t, url)
	host.send(t, `{"v":1,"type":"register"}`)
	reg := host.recvType(t, "registered")

	code, _ := reg["code"].(string)
	if len(code) != codeLength || strings.Trim(code, codeChars) != "" {
		t.Fatalf("bad code %q", code)
	}
	token, _ := reg["resumeToken"].(string)
	if raw, err := hex.DecodeString(token); err != nil || len(raw) != 32 {
		t.Fatalf("resume token should be 32 hex-encoded bytes, got %q", token)
	}
	if reg["v"] != float64(1) {
		t.Fatalf("router messages carry v:1, got %v", reg["v"])
	}

	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	joined := viewer.recvType(t, "joined")
	peerJoined := host.recvType(t, "peer-joined")
	if _, has := joined["turn"]; has {
		t.Fatalf("no TURN secret yet, turn must be omitted: %v", joined)
	}
	if _, has := peerJoined["turn"]; has {
		t.Fatalf("no TURN secret yet, turn must be omitted: %v", peerJoined)
	}
}

func TestSignal_RelaysBothDirectionsUnaltered(t *testing.T) {
	host, viewer, _ := pair(t, startSignalServer(t))

	// Odd spacing, key order, escapes and an unknown field must all survive byte for byte.
	offer := `{ "type" : "offer",  "v":1, "sdp":"v=0\r\na=é", "extra": {"x":[1,2]} }`
	host.send(t, offer)
	if got, err := viewer.recvRawE(); err != nil || string(got) != offer {
		t.Fatalf("viewer got %q (%v), want %q", got, err, offer)
	}
	answer := `{"v":1,"type":"answer","sdp":"v=0"}`
	viewer.send(t, answer)
	if got, err := host.recvRawE(); err != nil || string(got) != answer {
		t.Fatalf("host got %q (%v), want %q", got, err, answer)
	}
	for _, msg := range []string{
		`{"v":1,"type":"candidate","candidate":"candidate:1 1 udp 1 192.0.2.1 1 typ host","sdpMid":"0","sdpMLineIndex":0}`,
		`{"v":1,"type":"candidate","candidate":"","sdpMid":null,"sdpMLineIndex":null}`,
		`{"v":1,"type":"end-of-candidates"}`,
	} {
		host.send(t, msg)
		if got, _ := viewer.recvRawE(); string(got) != msg {
			t.Fatalf("viewer got %q, want %q", got, msg)
		}
		viewer.send(t, msg)
		if got, _ := host.recvRawE(); string(got) != msg {
			t.Fatalf("host got %q, want %q", got, msg)
		}
	}
}

func TestSignal_OrderPreservedPerDirection(t *testing.T) {
	host, viewer, _ := pair(t, startSignalServer(t))
	const n = 40 // each socket stays under the 100-per-10 s limit
	for i := 0; i < n; i++ {
		host.send(t, fmt.Sprintf(`{"v":1,"type":"candidate","candidate":"h%d","sdpMid":"0","sdpMLineIndex":0}`, i))
		viewer.send(t, fmt.Sprintf(`{"v":1,"type":"candidate","candidate":"v%d","sdpMid":"0","sdpMLineIndex":0}`, i))
	}
	for i := 0; i < n; i++ {
		if m := viewer.recv(t); m["candidate"] != fmt.Sprintf("h%d", i) {
			t.Fatalf("viewer message %d out of order: %v", i, m)
		}
		if m := host.recv(t); m["candidate"] != fmt.Sprintf("v%d", i) {
			t.Fatalf("host message %d out of order: %v", i, m)
		}
	}
}

func TestSignal_UnknownTypeIgnored(t *testing.T) {
	url := startSignalServer(t)

	// Before the first message: ignored, and it neither counts as the first message nor errors.
	host := dialSignal(t, url)
	host.send(t, `{"v":1,"type":"from-the-future","x":1}`)
	code, _ := host.register(t)

	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.recvType(t, "joined")
	host.recvType(t, "peer-joined")

	// After pairing: not relayed, not answered, socket stays open. The next message that does
	// arrive is the one sent after it.
	host.send(t, `{"v":1,"type":"from-the-future"}`)
	host.send(t, `{"v":1,"type":"end-of-candidates"}`)
	viewer.recvType(t, "end-of-candidates")
	viewer.send(t, `{"v":1,"type":"another-new-type"}`)
	viewer.send(t, `{"v":1,"type":"bye"}`)
	host.recvType(t, "bye")
}

// ── Malformed input ──────────────────────────────────────────────────────────

func TestSignal_OversizeFrame_TooLarge(t *testing.T) {
	url := startSignalServer(t)
	host, viewer, _ := pair(t, url)

	frame := func(size int) string {
		prefix, suffix := `{"v":1,"type":"offer","sdp":"`, `"}`
		return prefix + strings.Repeat("a", size-len(prefix)-len(suffix)) + suffix
	}

	// Exactly the limit passes.
	host.send(t, frame(64*1024))
	if got, _ := viewer.recvRawE(); len(got) != 64*1024 {
		t.Fatalf("a frame at the limit should be relayed whole, got %d bytes", len(got))
	}
	// One byte over is rejected, and the room ends.
	host.send(t, frame(64*1024+1))
	host.expectError(t, "too-large")
	viewer.recvType(t, "peer-left")
	viewer.expectClosed(t)

	// Far over the transport's read limit the library closes the socket itself.
	other := dialSignal(t, url)
	other.send(t, `{"v":1,"type":"register"}`)
	other.recvType(t, "registered")
	other.send(t, frame(2<<20))
	other.expectClosed(t)
}

func TestSignal_BadMessages(t *testing.T) {
	cases := map[string]string{
		"not json":               `hello`,
		"array":                  `[1]`,
		"null":                   `null`,
		"missing v":              `{"type":"register"}`,
		"v is a string":          `{"v":"1","type":"register"}`,
		"v is a fraction":        `{"v":1.5,"type":"register"}`,
		"v is null":              `{"v":null,"type":"register"}`,
		"missing type":           `{"v":1}`,
		"type is a number":       `{"v":1,"type":5}`,
		"empty type":             `{"v":1,"type":""}`,
		"offer before pairing":   `{"v":1,"type":"offer","sdp":"x"}`,
		"answer before pairing":  `{"v":1,"type":"answer","sdp":"x"}`,
		"bye before pairing":     `{"v":1,"type":"bye"}`,
		"end before pairing":     `{"v":1,"type":"end-of-candidates"}`,
		"join without code":      `{"v":1,"type":"join"}`,
		"join with numeric code": `{"v":1,"type":"join","code":123456}`,
		"resume without token":   `{"v":1,"type":"resume"}`,
		"router-only type":       `{"v":1,"type":"joined"}`,
	}
	url := startSignalServer(t)
	for name, msg := range cases {
		t.Run(name, func(t *testing.T) {
			c := dialSignal(t, url)
			c.send(t, msg)
			c.expectError(t, "bad-message")
		})
	}

	t.Run("binary frame", func(t *testing.T) {
		c := dialSignal(t, url)
		if err := c.c.Write(context.Background(), websocket.MessageBinary, []byte{1, 2, 3}); err != nil {
			t.Fatal(err)
		}
		c.expectError(t, "bad-message")
	})

	t.Run("relayed type while the host waits", func(t *testing.T) {
		c := dialSignal(t, url)
		c.register(t)
		c.send(t, `{"v":1,"type":"offer","sdp":"x"}`)
		c.expectError(t, "bad-message")
	})

	t.Run("second register", func(t *testing.T) {
		c := dialSignal(t, url)
		c.register(t)
		c.send(t, `{"v":1,"type":"register"}`)
		c.expectError(t, "bad-message")
	})

	// Wrong fields on a relayed message end the sender's socket and, as the room is paired,
	// the room.
	for name, msg := range map[string]string{
		"offer without sdp":       `{"v":1,"type":"offer"}`,
		"offer sdp not a string":  `{"v":1,"type":"offer","sdp":5}`,
		"candidate without mid":   `{"v":1,"type":"candidate","candidate":"c","sdpMLineIndex":0}`,
		"candidate mid a number":  `{"v":1,"type":"candidate","candidate":"c","sdpMid":0,"sdpMLineIndex":0}`,
		"candidate index string":  `{"v":1,"type":"candidate","candidate":"c","sdpMid":"0","sdpMLineIndex":"0"}`,
		"candidate index decimal": `{"v":1,"type":"candidate","candidate":"c","sdpMid":"0","sdpMLineIndex":0.5}`,
		"bye reason a number":     `{"v":1,"type":"bye","reason":7}`,
		"join when paired":        `{"v":1,"type":"join","code":"AAAAAA"}`,
	} {
		t.Run(name, func(t *testing.T) {
			host, viewer, _ := pair(t, url)
			host.send(t, msg)
			host.expectError(t, "bad-message")
			viewer.recvType(t, "peer-left")
		})
	}
}

func TestSignal_UnsupportedVersion(t *testing.T) {
	url := startSignalServer(t)
	for _, v := range []string{"2", "0", "-1", "99999999999999999999"} {
		c := dialSignal(t, url)
		c.send(t, fmt.Sprintf(`{"v":%s,"type":"register"}`, v))
		c.expectError(t, "unsupported-version")
	}
}

// ── Limits ───────────────────────────────────────────────────────────────────

func TestSignal_RateLimitPerSocket(t *testing.T) {
	url := startSignalServer(t, func() { signalMsgMax = 4 })
	c := dialSignal(t, url)
	c.register(t) // message 1
	for i := 0; i < 3; i++ {
		c.send(t, `{"v":1,"type":"ignored"}`) // messages 2 to 4
	}
	c.send(t, `{"v":1,"type":"ignored"}`)
	c.expectError(t, "rate-limited")
}

func TestSignal_RateLimitWindowSlides(t *testing.T) {
	fc := newFakeClock()
	url := startSignalServer(t, func() { signalMsgMax = 2; nowFunc = fc.Now })
	c := dialSignal(t, url)
	c.register(t)
	c.send(t, `{"v":1,"type":"ignored"}`)
	fc.Advance(signalMsgWindow)
	// A fresh window: the socket may send again.
	for i := 0; i < 2; i++ {
		c.send(t, `{"v":1,"type":"ignored"}`)
	}
	c.send(t, `{"v":1,"type":"ignored"}`)
	c.expectError(t, "rate-limited")
}

func TestSignal_SocketsPerIPLimit(t *testing.T) {
	url := startSignalServer(t, func() { signalMaxSocketsPerIP = 2 })
	a := dialSignal(t, url)
	a.register(t)
	b := dialSignal(t, url)
	b.register(t)

	c := dialSignal(t, url)
	c.expectError(t, "rate-limited")

	// A freed slot is reusable.
	a.c.CloseNow()
	waitFor(t, "slot release", func() bool { return openSockets() == 1 })
	d := dialSignal(t, url)
	d.register(t)
}

func TestSignal_RegisterAndResumeShareTheRegisterLimiter(t *testing.T) {
	url := startSignalServer(t, func() { rateLimitMax = 3 })
	_, token := dialSignal(t, url).register(t)
	dialSignal(t, url).register(t)

	c := dialSignal(t, url)
	c.send(t, fmt.Sprintf(`{"v":1,"type":"resume","resumeToken":%q}`, token))
	c.recvType(t, "resumed") // third use of the limiter

	over := dialSignal(t, url)
	over.send(t, `{"v":1,"type":"register"}`)
	over.expectError(t, "rate-limited")
}

func TestSignal_JoinSharesTheConnectLimiter(t *testing.T) {
	url := startSignalServer(t, func() { rateLimitMax = 2 })
	for i := 0; i < 2; i++ {
		c := dialSignal(t, url)
		c.send(t, `{"v":1,"type":"join","code":"AAAAAA"}`)
		c.expectError(t, "code-unknown")
	}
	c := dialSignal(t, url)
	c.send(t, `{"v":1,"type":"join","code":"AAAAAA"}`)
	c.expectError(t, "rate-limited")

	// join does not draw on the register limiter.
	h := dialSignal(t, url)
	h.register(t)
}

func TestSignal_FirstMessageTimeout(t *testing.T) {
	url := startSignalServer(t, func() { signalFirstMsgTimeout = 100 * time.Millisecond })
	c := dialSignal(t, url)
	c.expectError(t, "timeout")

	// Once the first message is in, silence is fine.
	host := dialSignal(t, url)
	code, _ := host.register(t)
	time.Sleep(250 * time.Millisecond)
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.recvType(t, "joined")
}

func TestSignal_PingTimeoutClosesSilentClient(t *testing.T) {
	url := startSignalServer(t, func() {
		signalPingInterval = 20 * time.Millisecond
		signalPongTimeout = 50 * time.Millisecond
	})
	c := dialSignal(t, url)
	c.register(t)
	// The client reads nothing from here on, so it never answers a ping.
	waitFor(t, "ping timeout", func() bool { return openSockets() == 0 })
}

func TestSignal_PingKeepsAnsweringClientAlive(t *testing.T) {
	url := startSignalServer(t, func() {
		signalPingInterval = 20 * time.Millisecond
		signalPongTimeout = 200 * time.Millisecond
	})
	c := dialSignal(t, url)
	c.register(t)
	// Reading is what answers pings.
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { defer close(done); c.c.Read(ctx) }()
	t.Cleanup(func() { cancel(); <-done })
	time.Sleep(250 * time.Millisecond) // a dozen ping intervals
	if n := openSockets(); n != 1 {
		t.Fatalf("a client that answers pings should stay connected, open sockets: %d", n)
	}
}

// ── Codes ────────────────────────────────────────────────────────────────────

func TestSignal_CodeIsOneTime(t *testing.T) {
	url := startSignalServer(t)
	host, viewer, code := pair(t, url)

	late := dialSignal(t, url)
	late.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	late.expectError(t, "code-used")

	// The pairing is undisturbed, and once the room is over the code is simply unknown.
	host.send(t, `{"v":1,"type":"end-of-candidates"}`)
	viewer.recvType(t, "end-of-candidates")
	host.c.CloseNow()
	viewer.recvType(t, "peer-left")
	waitFor(t, "room removal", func() bool { e, _ := roomState(code); return !e })

	again := dialSignal(t, url)
	again.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	again.expectError(t, "code-unknown")
}

func TestSignal_JoinUnknownCode(t *testing.T) {
	url := startSignalServer(t)
	c := dialSignal(t, url)
	c.send(t, `{"v":1,"type":"join","code":"ZZZZZZ"}`)
	c.expectError(t, "code-unknown")
}

func TestSignal_CodeExpiresAfterTTL(t *testing.T) {
	fc := newFakeClock()
	url := startSignalServer(t, func() { nowFunc = fc.Now })
	host := dialSignal(t, url)
	code, _ := host.register(t)

	fc.Advance(codeTTL + time.Second)
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.expectError(t, "code-unknown")
	if data, err := host.recvRawE(); err == nil { // the expired registration's host is dropped
		t.Fatalf("expected the host socket to be dropped, got %q", data)
	}
	if e, _ := roomState(code); e {
		t.Fatal("expired room should be deleted")
	}
}

func TestSignal_CodesAreUnique(t *testing.T) {
	url := startSignalServer(t)
	oldChars, oldLen := codeChars, codeLength
	t.Cleanup(func() { codeChars, codeLength = oldChars, oldLen })
	codeChars, codeLength = "A", 1 // a one-code space makes any collision certain

	h := dialSignal(t, url)
	if code, _ := h.register(t); code != "A" {
		t.Fatalf("expected code A, got %q", code)
	}
	// The room holds "A", so a second host cannot be issued a code.
	c := dialSignal(t, url)
	c.send(t, `{"v":1,"type":"register"}`)
	if _, err := c.recvRawE(); websocket.CloseStatus(err) != websocket.StatusInternalError {
		t.Fatalf("expected an internal-error close, got %v", err)
	}
}

// ── Leaving ──────────────────────────────────────────────────────────────────

func TestSignal_PeerLeftWhenEitherSideCloses(t *testing.T) {
	url := startSignalServer(t)

	host, viewer, code := pair(t, url)
	viewer.c.CloseNow()
	host.recvType(t, "peer-left")
	host.expectClosed(t)
	waitFor(t, "room removal", func() bool { e, _ := roomState(code); return !e })

	host, viewer, _ = pair(t, url)
	host.c.Close(websocket.StatusNormalClosure, "")
	viewer.recvType(t, "peer-left")
	viewer.expectClosed(t)
}

func TestSignal_ByeRelayedThenPeerLeft(t *testing.T) {
	host, viewer, code := pair(t, startSignalServer(t))
	bye := `{"v":1,"type":"bye","reason":"user-ended"}`
	host.send(t, bye)
	if got, _ := viewer.recvRawE(); string(got) != bye {
		t.Fatalf("viewer should get the bye unaltered, got %q", got)
	}
	viewer.recvType(t, "peer-left")
	viewer.expectClosed(t)
	host.expectClosed(t)
	waitFor(t, "room removal", func() bool { e, _ := roomState(code); return !e })
}

// ── Reconnecting ─────────────────────────────────────────────────────────────

func resumeMsg(token string) string {
	return fmt.Sprintf(`{"v":1,"type":"resume","resumeToken":%q}`, token)
}

func TestSignal_ResumeWithinWindow(t *testing.T) {
	fc := newFakeClock()
	url := startSignalServer(t, func() { nowFunc = fc.Now })
	host := dialSignal(t, url)
	code, token := host.register(t)

	host.c.CloseNow()
	waitFor(t, "host drop", func() bool { _, up := roomState(code); return !up })
	fc.Advance(signalResumeWindow - time.Second)

	host2 := dialSignal(t, url)
	host2.send(t, resumeMsg(token))
	resumed := host2.recvType(t, "resumed")
	if resumed["code"] != code {
		t.Fatalf("resume should keep the code %q, got %v", code, resumed)
	}

	// The resumed host is a normal waiting host.
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.recvType(t, "joined")
	host2.recvType(t, "peer-joined")
}

func TestSignal_ResumeAfterWindowRejected(t *testing.T) {
	fc := newFakeClock()
	url := startSignalServer(t, func() { nowFunc = fc.Now })
	host := dialSignal(t, url)
	code, token := host.register(t)
	host.c.CloseNow()
	waitFor(t, "host drop", func() bool { _, up := roomState(code); return !up })

	fc.Advance(signalResumeWindow + time.Second)
	host2 := dialSignal(t, url)
	host2.send(t, resumeMsg(token))
	host2.expectError(t, "resume-rejected")

	// The sweep deletes it, and the code is unknown.
	sweepSignalRooms(fc.Now())
	if e, _ := roomState(code); e {
		t.Fatal("sweep should remove a room whose host did not resume")
	}
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.expectError(t, "code-unknown")
}

func TestSignal_ResumeRejections(t *testing.T) {
	url := startSignalServer(t)

	c := dialSignal(t, url)
	c.send(t, resumeMsg(strings.Repeat("0", 64)))
	c.expectError(t, "resume-rejected")

	// Once paired, a token no longer resumes anything.
	pair(t, url)
	signalMu.Lock()
	var token string
	for _, r := range signalRooms {
		token = r.token
	}
	signalMu.Unlock()
	late := dialSignal(t, url)
	late.send(t, resumeMsg(token))
	late.expectError(t, "resume-rejected")
}

func TestSignal_ResumeReplacesStaleSocket(t *testing.T) {
	url := startSignalServer(t)
	old := dialSignal(t, url)
	code, token := old.register(t)

	// The router has not noticed that the old socket is dead; the new one takes over.
	fresh := dialSignal(t, url)
	fresh.send(t, resumeMsg(token))
	fresh.recvType(t, "resumed")
	if data, err := old.recvRawE(); err == nil {
		t.Fatalf("the replaced socket should be dropped, got %q", data)
	}

	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	viewer.recvType(t, "joined")
	fresh.recvType(t, "peer-joined")
}

// ── Test vectors ─────────────────────────────────────────────────────────────

func TestSignal_GoldenRoundTrip(t *testing.T) {
	want := []string{
		"register", "registered", "resume", "resumed", "join", "joined", "peer-joined",
		"peer-left", "error", "offer", "answer", "candidate", "end-of-candidates", "bye",
	}
	files, err := filepath.Glob(filepath.Join("testdata", "signaling", "*.json"))
	if err != nil {
		t.Fatal(err)
	}
	have := map[string]bool{}
	for _, f := range files {
		have[strings.TrimSuffix(filepath.Base(f), ".json")] = true
	}
	for _, name := range want {
		if !have[name] {
			t.Errorf("missing golden file for %q", name)
		}
	}
	if len(have) != len(want) {
		t.Errorf("expected %d golden files, found %d", len(want), len(have))
	}

	for _, name := range want {
		t.Run(name, func(t *testing.T) {
			data, err := os.ReadFile(filepath.Join("testdata", "signaling", name+".json"))
			if err != nil {
				t.Fatal(err)
			}
			var first any
			if err := json.Unmarshal(data, &first); err != nil {
				t.Fatalf("does not parse: %v", err)
			}
			again, err := json.Marshal(first)
			if err != nil {
				t.Fatal(err)
			}
			var second any
			if err := json.Unmarshal(again, &second); err != nil {
				t.Fatal(err)
			}
			if !reflect.DeepEqual(first, second) {
				t.Fatalf("re-serializing changed the value:\n%s\n%s", data, again)
			}

			obj := first.(map[string]any)
			if obj["v"] != float64(1) || obj["type"] != name {
				t.Fatalf("envelope should be v:1 type:%s, got %v", name, obj)
			}

			// What the router reads must pass its own validation.
			switch name {
			case "register", "resume", "join", "offer", "answer", "candidate", "end-of-candidates", "bye":
				typ, fields, errCode := parseEnvelope(data)
				if errCode != "" || typ != name {
					t.Fatalf("router rejects its own vector: %q %q", typ, errCode)
				}
				if relayedOK := validRelayed(typ, fields); knownRelayed(typ) && !relayedOK {
					t.Fatalf("router rejects its own %s vector", typ)
				}
			}
			// What the router sends must decode into its own message type.
			if name == "joined" || name == "peer-joined" {
				var out signalOut
				if err := json.Unmarshal(data, &out); err != nil || out.Turn == nil || len(out.Turn.URLs) != 2 {
					t.Fatalf("turn did not decode: %+v %v", out, err)
				}
			}
		})
	}
}

func knownRelayed(typ string) bool {
	switch typ {
	case "offer", "answer", "candidate", "end-of-candidates", "bye":
		return true
	}
	return false
}

// ── Concurrency ──────────────────────────────────────────────────────────────

func TestSignal_ManyRoomsConcurrently(t *testing.T) {
	const rooms, rounds = 25, 20
	url := startSignalServer(t, func() {
		rateLimitMax = 10000
		signalMaxSocketsPerIP = 1000
		signalMsgMax = 10000
	})

	codes := make(chan string, rooms)
	errs := make(chan error, rooms)
	var wg sync.WaitGroup
	for i := 0; i < rooms; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			errs <- func() error {
				host, viewer, code, err := pairE(url)
				if err != nil {
					return err
				}
				defer host.c.CloseNow()
				defer viewer.c.CloseNow()
				codes <- code
				for r := 0; r < rounds; r++ {
					out := fmt.Sprintf(`{"v":1,"type":"candidate","candidate":"h-%d-%d","sdpMid":"0","sdpMLineIndex":0}`, i, r)
					if err := host.sendE(out); err != nil {
						return err
					}
					if got, err := viewer.recvRawE(); err != nil || string(got) != out {
						return fmt.Errorf("room %d round %d: viewer got %q (%v)", i, r, got, err)
					}
					back := fmt.Sprintf(`{"v":1,"type":"answer","sdp":"v-%d-%d"}`, i, r)
					if err := viewer.sendE(back); err != nil {
						return err
					}
					if got, err := host.recvRawE(); err != nil || string(got) != back {
						return fmt.Errorf("room %d round %d: host got %q (%v)", i, r, got, err)
					}
				}
				if err := host.sendE(`{"v":1,"type":"bye"}`); err != nil {
					return err
				}
				if m, err := viewer.recvE(); err != nil || m["type"] != "bye" {
					return fmt.Errorf("room %d: expected bye, got %v (%v)", i, m, err)
				}
				return nil
			}()
		}(i)
	}
	wg.Wait()
	close(codes)
	close(errs)
	for err := range errs {
		if err != nil {
			t.Error(err)
		}
	}
	seen := map[string]bool{}
	for c := range codes {
		if seen[c] {
			t.Errorf("code %q issued twice", c)
		}
		seen[c] = true
	}
	waitFor(t, "all rooms to end", func() bool {
		signalMu.Lock()
		defer signalMu.Unlock()
		return len(signalRooms) == 0
	})
}
