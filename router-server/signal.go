package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// The /signal endpoint: two WebRTC peers meet on a connection code and swap SDP and ICE
// candidates. docs/SIGNALING.md is the protocol; this file is its implementation. The router
// never reads or changes a relayed message, and never logs SDP, candidates, TURN credentials or
// resume tokens.

// ── Config (overridable via environment variables) ───────────────────────────

var (
	signalMaxFrame        int
	signalMsgMax          int
	signalMsgWindow       time.Duration
	signalMaxSocketsPerIP int
	signalFirstMsgTimeout time.Duration
	signalPingInterval    time.Duration
	signalPongTimeout     time.Duration
	signalResumeWindow    time.Duration
)

// signalWriteTimeout bounds one write to a peer, so a peer that stopped reading cannot hold up
// the other peer's relay forever.
const signalWriteTimeout = 10 * time.Second

// The WebSocket library closes a connection on its own, without a chance to send an error, when
// a frame exceeds its read limit. The limit is therefore a multiple of the protocol's frame
// size: frames over signalMaxFrame but under the limit get a proper `too-large` error.
const signalReadLimitFactor = 16

func initSignalConfig() {
	signalMaxFrame = getIntEnv("SIGNAL_MAX_FRAME_BYTES", 64*1024)
	signalMsgMax = getIntEnv("SIGNAL_MSG_RATE_MAX", 100)
	signalMsgWindow = getDurationEnv("SIGNAL_MSG_RATE_WINDOW", 10*time.Second)
	signalMaxSocketsPerIP = getIntEnv("SIGNAL_MAX_SOCKETS_PER_IP", 20)
	signalFirstMsgTimeout = getDurationEnv("SIGNAL_FIRST_MSG_TIMEOUT", 10*time.Second)
	signalPingInterval = getDurationEnv("SIGNAL_PING_INTERVAL", 20*time.Second)
	signalPongTimeout = getDurationEnv("SIGNAL_PONG_TIMEOUT", 10*time.Second)
	signalResumeWindow = getDurationEnv("SIGNAL_RESUME_WINDOW", 60*time.Second)
	initTurnConfig()
}

// ── Messages ─────────────────────────────────────────────────────────────────

// signalTurn is the TURN credential pair a peer receives at pairing (see turn.go).
type signalTurn struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username"`
	Credential string   `json:"credential"`
}

// signalOut is every message the router originates. Relayed messages are forwarded as the
// original bytes instead.
type signalOut struct {
	V           int         `json:"v"`
	Type        string      `json:"type"`
	Code        string      `json:"code,omitempty"`
	ResumeToken string      `json:"resumeToken,omitempty"`
	Turn        *signalTurn `json:"turn,omitempty"`
	Message     string      `json:"message,omitempty"`
}

// issueTurn returns the credentials for one peer at pairing time, or nil when the router has
// none to give; peers cope with nil (STUN and host candidates only). Every call is a fresh pair.
func issueTurn() *signalTurn { return issueTurnCredentials(nowFunc()) }

// Types a client can legitimately send, plus the ones only the router sends. All are "known":
// at the wrong point in the sequence they are bad-message, not ignored.
var knownSignalTypes = map[string]bool{
	"register": true, "registered": true, "resume": true, "resumed": true,
	"join": true, "joined": true, "peer-joined": true, "peer-left": true, "error": true,
	"offer": true, "answer": true, "candidate": true, "end-of-candidates": true, "bye": true,
}

// isJSONString and friends look at raw field values without decoding them, so a relayed
// field is checked for its type and nothing more.
func isJSONString(raw json.RawMessage) bool { return len(raw) >= 2 && raw[0] == '"' }

func isJSONNull(raw json.RawMessage) bool { return string(raw) == "null" }

func isJSONInteger(raw json.RawMessage) bool {
	s := string(raw)
	if len(s) > 0 && s[0] == '-' {
		s = s[1:]
	}
	if s == "" {
		return false
	}
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

// parseEnvelope checks the envelope rules and returns the type and raw fields. A non-empty
// errCode is the error to send before closing.
func parseEnvelope(data []byte) (typ string, fields map[string]json.RawMessage, errCode string) {
	if err := json.Unmarshal(data, &fields); err != nil || fields == nil {
		return "", nil, "bad-message"
	}
	v, ok := fields["v"]
	if !ok || !isJSONInteger(v) {
		return "", nil, "bad-message"
	}
	if err := json.Unmarshal(fields["type"], &typ); err != nil || typ == "" {
		return "", nil, "bad-message"
	}
	if string(v) != "1" {
		return "", nil, "unsupported-version"
	}
	return typ, fields, ""
}

// validRelayed checks the per-type field rules of a relayed message.
func validRelayed(typ string, f map[string]json.RawMessage) bool {
	str := func(name string) bool { return isJSONString(f[name]) }
	switch typ {
	case "offer", "answer":
		return str("sdp")
	case "candidate":
		mid, hasMid := f["sdpMid"]
		idx, hasIdx := f["sdpMLineIndex"]
		return str("candidate") &&
			hasMid && (isJSONString(mid) || isJSONNull(mid)) &&
			hasIdx && (isJSONInteger(idx) || isJSONNull(idx))
	case "end-of-candidates":
		return true
	case "bye":
		reason, has := f["reason"]
		return !has || isJSONString(reason)
	}
	return false
}

// ── State ────────────────────────────────────────────────────────────────────

// signalRoom is one registration: a host, and once paired, a viewer. Fields are guarded by
// signalMu. Lock order, where both are needed: codeMu, then signalMu.
type signalRoom struct {
	code      string
	token     string // resume token
	createdAt time.Time
	host      *signalPeer // nil while the host is disconnected and may still resume
	hostGone  time.Time   // when host became nil
	viewer    *signalPeer // non-nil once paired
	ended     bool
}

type signalPeer struct {
	conn *websocket.Conn
	ip   string
	role string // "", "host" or "viewer"; touched only by the peer's own goroutine

	room *signalRoom // guarded by signalMu

	established chan struct{} // closed once the first message has been accepted
	closeOnce   sync.Once
	bg          sync.WaitGroup

	windowStart time.Time // per-socket message rate, own goroutine only
	windowCount int
}

var (
	signalRooms   = make(map[string]*signalRoom)
	signalSockets = make(map[string]int) // open sockets per IP
	signalMu      sync.Mutex

	// signalWG counts running /signal handlers, so tests can wait for all of them to finish.
	signalWG sync.WaitGroup
)

// signalCodeInUse reports whether a /signal room holds code. Called with codeMu held, from
// generateUniqueCodeLocked.
func signalCodeInUse(code string) bool {
	signalMu.Lock()
	defer signalMu.Unlock()
	_, used := signalRooms[code]
	return used
}

// sweepSignalRooms deletes unpaired rooms whose code has expired or whose host did not resume
// in time, and closes the socket of an expired room whose host is still connected.
func sweepSignalRooms(now time.Time) {
	var closing []*signalPeer
	signalMu.Lock()
	for code, room := range signalRooms {
		if room.viewer != nil {
			continue
		}
		abandoned := room.host == nil && now.Sub(room.hostGone) > signalResumeWindow
		if abandoned || now.Sub(room.createdAt) > codeTTL {
			room.ended = true
			delete(signalRooms, code)
			if room.host != nil {
				closing = append(closing, room.host)
			}
			log.Printf("Cleaned up expired signal room: Code=%s", code)
		}
	}
	signalMu.Unlock()
	for _, p := range closing {
		p.conn.CloseNow()
	}
}

// ── Handler ──────────────────────────────────────────────────────────────────

func handleSignal(w http.ResponseWriter, r *http.Request) {
	signalWG.Add(1)
	defer signalWG.Done()

	// Origin is not checked in v1 (docs/SIGNALING.md); the Phase 5 browser viewer adds an
	// allow-list.
	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		InsecureSkipVerify: true,
		CompressionMode:    websocket.CompressionDisabled,
	})
	if err != nil {
		return // Accept has already answered the HTTP request
	}
	conn.SetReadLimit(int64(signalMaxFrame) * signalReadLimitFactor)

	ctx, cancel := context.WithCancel(r.Context())
	p := &signalPeer{conn: conn, ip: clientIP(r), established: make(chan struct{})}

	signalMu.Lock()
	counted := signalSockets[p.ip] < signalMaxSocketsPerIP
	if counted {
		signalSockets[p.ip]++
	}
	signalMu.Unlock()

	defer func() {
		cancel()
		p.bg.Wait()
		p.disconnect()
		if counted {
			signalMu.Lock()
			if signalSockets[p.ip]--; signalSockets[p.ip] <= 0 {
				delete(signalSockets, p.ip)
			}
			signalMu.Unlock()
		}
		conn.Close(websocket.StatusNormalClosure, "")
	}()

	if !counted {
		p.fail("rate-limited", "too many open sockets from this address")
		return
	}

	p.bg.Add(2)
	go p.firstMessageWatchdog(ctx)
	go p.pingLoop(ctx)

	for {
		typ, data, err := conn.Read(ctx)
		if err != nil {
			return
		}
		if typ != websocket.MessageText {
			p.fail("bad-message", "binary frames are not supported")
			return
		}
		if len(data) > signalMaxFrame {
			p.fail("too-large", "frame exceeds the size limit")
			return
		}
		if !p.allowMessage() {
			p.fail("rate-limited", "too many messages")
			return
		}
		mtype, fields, errCode := parseEnvelope(data)
		if errCode != "" {
			p.fail(errCode, "malformed envelope")
			return
		}
		if !knownSignalTypes[mtype] {
			continue // a newer peer's type: ignored, not relayed, socket stays open
		}
		if !p.handle(mtype, fields, data) {
			return
		}
	}
}

// allowMessage is the per-socket rate limit: signalMsgMax messages per signalMsgWindow.
func (p *signalPeer) allowMessage() bool {
	now := nowFunc()
	if p.windowStart.IsZero() || now.Sub(p.windowStart) >= signalMsgWindow {
		p.windowStart, p.windowCount = now, 0
	}
	p.windowCount++
	return p.windowCount <= signalMsgMax
}

func (p *signalPeer) firstMessageWatchdog(ctx context.Context) {
	defer p.bg.Done()
	timer := time.NewTimer(signalFirstMsgTimeout)
	defer timer.Stop()
	select {
	case <-p.established:
	case <-ctx.Done():
	case <-timer.C:
		p.fail("timeout", "no first message")
	}
}

// pingLoop sends a ping every signalPingInterval and drops the socket when the pong does not
// come within signalPongTimeout. The read loop must be running to see the pong.
func (p *signalPeer) pingLoop(ctx context.Context) {
	defer p.bg.Done()
	ticker := time.NewTicker(signalPingInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		pctx, cancel := context.WithTimeout(ctx, signalPongTimeout)
		err := p.conn.Ping(pctx)
		cancel()
		if err != nil {
			p.conn.CloseNow()
			return
		}
	}
}

// handle processes one known message and reports whether the socket should stay open.
func (p *signalPeer) handle(typ string, fields map[string]json.RawMessage, raw []byte) bool {
	switch typ {
	case "register", "resume", "join":
		if p.role != "" {
			p.fail("bad-message", typ+" is only valid as the first message")
			return false
		}
		return p.handleFirst(typ, fields)
	case "offer", "answer", "candidate", "end-of-candidates", "bye":
		if !validRelayed(typ, fields) {
			p.fail("bad-message", "invalid fields for "+typ)
			return false
		}
		other := p.pairedPeer()
		if other == nil {
			p.fail("bad-message", typ+" before pairing")
			return false
		}
		ctx, cancel := context.WithTimeout(context.Background(), signalWriteTimeout)
		defer cancel()
		if err := other.conn.Write(ctx, websocket.MessageText, raw); err != nil {
			other.conn.CloseNow() // that peer's own goroutine ends the room
		}
		// A bye ends the room: the read loop stops and disconnect() tells the other peer.
		return typ != "bye"
	default:
		p.fail("bad-message", typ+" is not valid from a client")
		return false
	}
}

func (p *signalPeer) handleFirst(typ string, fields map[string]json.RawMessage) bool {
	// register and resume share one per-IP limiter, join has its own.
	limiter := hostLimiter
	if typ == "join" {
		limiter = joinLimiter
	}
	if !limiter.Allow(p.ip) {
		p.fail("rate-limited", "too many requests")
		return false
	}

	switch typ {
	case "register":
		return p.register()
	case "resume":
		var token string
		if !isJSONString(fields["resumeToken"]) || json.Unmarshal(fields["resumeToken"], &token) != nil {
			p.fail("bad-message", "resume needs a resumeToken")
			return false
		}
		return p.resume(token)
	default:
		var code string
		if !isJSONString(fields["code"]) || json.Unmarshal(fields["code"], &code) != nil {
			p.fail("bad-message", "join needs a code")
			return false
		}
		return p.join(code)
	}
}

func (p *signalPeer) register() bool {
	token, err := generateSecret()
	if err != nil {
		p.conn.Close(websocket.StatusInternalError, "internal error")
		return false
	}

	// codeMu serializes code generation, so a code is unique.
	codeMu.Lock()
	code, err := generateUniqueCodeLocked()
	if err == nil {
		signalMu.Lock()
		signalRooms[code] = &signalRoom{code: code, token: token, createdAt: nowFunc(), host: p}
		p.room = signalRooms[code]
		signalMu.Unlock()
	}
	codeMu.Unlock()
	if err != nil {
		p.conn.Close(websocket.StatusInternalError, "internal error")
		return false
	}

	p.role = "host"
	close(p.established)
	log.Printf("Signal room created: Code=%s", code)
	return p.send(signalOut{V: 1, Type: "registered", Code: code, ResumeToken: token})
}

func (p *signalPeer) resume(token string) bool {
	var old *signalPeer
	var code string
	signalMu.Lock()
	now := nowFunc()
	for c, room := range signalRooms {
		// Constant-time compare; the token is the only thing that authorizes a resume.
		if subtle.ConstantTimeCompare([]byte(token), []byte(room.token)) != 1 {
			continue
		}
		expired := now.Sub(room.createdAt) > codeTTL ||
			(room.host == nil && now.Sub(room.hostGone) > signalResumeWindow)
		if room.viewer != nil || expired {
			break // paired rooms do not resume in v1; expired ones are swept
		}
		// A half-open socket the router has not noticed yet is replaced by the new one.
		old = room.host
		if old != nil {
			old.room = nil
		}
		room.host, room.hostGone = p, time.Time{}
		p.room, code = room, c
		break
	}
	signalMu.Unlock()

	if code == "" {
		p.fail("resume-rejected", "unknown or expired token")
		return false
	}
	if old != nil {
		old.conn.CloseNow()
	}
	p.role = "host"
	close(p.established)
	return p.send(signalOut{V: 1, Type: "resumed", Code: code})
}

func (p *signalPeer) join(code string) bool {
	var host *signalPeer
	var errCode string
	signalMu.Lock()
	room := signalRooms[code]
	switch {
	case room == nil:
		errCode = "code-unknown"
	case room.viewer != nil:
		errCode = "code-used"
	case nowFunc().Sub(room.createdAt) > codeTTL:
		room.ended = true
		delete(signalRooms, code)
		if room.host != nil {
			defer room.host.conn.CloseNow()
		}
		errCode = "code-unknown"
	case room.host == nil:
		errCode = "code-unknown" // the host is between sockets; nobody to pair with yet
	default:
		room.viewer, p.room = p, room
		host = room.host
	}
	signalMu.Unlock()

	if errCode != "" {
		p.fail(errCode, "cannot join with that code")
		return false
	}
	p.role = "viewer"
	close(p.established)

	// The viewer's joined goes out first, so nothing the host sends after peer-joined can
	// reach the viewer ahead of it. TURN credentials are per socket.
	if !p.send(signalOut{V: 1, Type: "joined", Turn: issueTurn()}) {
		return false
	}
	host.send(signalOut{V: 1, Type: "peer-joined", Turn: issueTurn()})
	return true
}

// pairedPeer returns the other side of a paired room, or nil when there is none.
func (p *signalPeer) pairedPeer() *signalPeer {
	signalMu.Lock()
	defer signalMu.Unlock()
	room := p.room
	if room == nil || room.ended || room.viewer == nil {
		return nil
	}
	if p == room.host {
		return room.viewer
	}
	return room.host
}

// disconnect runs when a socket's read loop ends. A paired room ends and the other peer is told;
// an unpaired host keeps its registration for the resume window.
func (p *signalPeer) disconnect() {
	var other *signalPeer
	signalMu.Lock()
	room := p.room
	p.room = nil
	if room != nil && !room.ended {
		if room.viewer != nil {
			room.ended = true
			delete(signalRooms, room.code)
			if p == room.host {
				other = room.viewer
			} else {
				other = room.host
			}
		} else if room.host == p {
			room.host, room.hostGone = nil, nowFunc()
		}
	}
	signalMu.Unlock()

	if other != nil {
		other.sendAndClose(signalOut{V: 1, Type: "peer-left"}, websocket.StatusNormalClosure, "peer left")
	}
}

// ── Writing ──────────────────────────────────────────────────────────────────

func (p *signalPeer) send(msg signalOut) bool {
	data, err := json.Marshal(msg)
	if err != nil {
		return false
	}
	ctx, cancel := context.WithTimeout(context.Background(), signalWriteTimeout)
	defer cancel()
	return p.conn.Write(ctx, websocket.MessageText, data) == nil
}

// sendAndClose writes a final message, then closes. Only the first call has any effect.
func (p *signalPeer) sendAndClose(msg signalOut, status websocket.StatusCode, reason string) {
	p.closeOnce.Do(func() {
		p.send(msg)
		p.conn.Close(status, reason)
	})
}

// fail sends error{code,message} and closes. message is for logs, not for users.
func (p *signalPeer) fail(code, message string) {
	p.sendAndClose(signalOut{V: 1, Type: "error", Code: code, Message: message},
		websocket.StatusPolicyViolation, code)
}
