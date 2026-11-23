package main

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestGenerateCode(t *testing.T) {
	for i := 0; i < 100; i++ {
		code, err := generateCode()
		if err != nil {
			t.Fatalf("generateCode returned error: %v", err)
		}
		if len(code) != codeLength {
			t.Fatalf("expected code length %d, got %d (%q)", codeLength, len(code), code)
		}
		if code == "" {
			t.Fatal("expected non-empty code")
		}
		for _, c := range code {
			if !strings.ContainsRune(codeChars, c) {
				t.Fatalf("code %q contains char %q not in codeChars", code, c)
			}
		}
	}
}

func TestGenerateCode_HasNoLookalikeCharacters(t *testing.T) {
	for i := 0; i < 2000; i++ {
		code, err := generateCode()
		if err != nil {
			t.Fatalf("generateCode returned error: %v", err)
		}
		if strings.ContainsAny(code, "0O1I") {
			t.Fatalf("code %q contains a character that reads like another (0/O, 1/I)", code)
		}
	}
}

func TestGenerateSecret(t *testing.T) {
	secret, err := generateSecret()
	if err != nil {
		t.Fatalf("generateSecret returned error: %v", err)
	}
	if len(secret) != 64 {
		t.Fatalf("expected 64-char hex secret, got %d (%q)", len(secret), secret)
	}
	if _, err := hex.DecodeString(secret); err != nil {
		t.Fatalf("secret is not valid hex: %v", err)
	}
}

func TestClientIP_RemoteAddr(t *testing.T) {
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	if got := clientIP(r); got != "1.2.3.4" {
		t.Fatalf("expected 1.2.3.4, got %q", got)
	}
}

// ── T6 (C1): clientIP / TRUSTED_PROXY_CIDRS ─────────────────────────────────
//
// T3 pinned the old behaviour — clientIP trusted X-Forwarded-For
// unconditionally, from any peer, taking the leftmost entry, with no IP
// syntax validation. T6 replaces all of that with trusted-proxy gating.
// The following four T3 baseline tests are DELETED, not silently rewritten,
// because their assertions describe the exact vulnerability C1 closes:
//   - TestClientIP_ForwardedFor (leftmost entry, "9.9.9.9" from "9.9.9.9, 1.1.1.1")
//   - TestClientIP_ForwardedFor_TrustedUnconditionally_Baseline (any peer trusted)
//   - TestClientIP_ForwardedFor_MultipleValues_LeftmostWins (leftmost wins)
//   - TestClientIP_ForwardedFor_InvalidIPValue_PassedThroughUnvalidated (no gating at all)
// Replaced below by tests asserting the new, secure-by-default behaviour.

func resetTrustedProxies(cidrs ...string) {
	nets, err := parseTrustedProxyCIDRs(strings.Join(cidrs, ","))
	if err != nil {
		panic(err) // test setup only; a bad literal here is a test bug
	}
	trustedProxyCIDRs = nets
}

// This is the security assertion C1 exists for: with no trusted proxy
// configured (the default), a spoofed X-Forwarded-For header is completely
// ignored and the real socket peer is used instead.
func TestClientIP_SpoofedForwardedFor_IgnoredWhenNoTrustedProxyConfigured(t *testing.T) {
	resetTrustedProxies()
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "203.0.113.7")
	if got := clientIP(r); got != "1.2.3.4" {
		t.Fatalf("expected spoofed XFF to be ignored in favour of the real peer 1.2.3.4, got %q", got)
	}
}

func TestClientIP_ForwardedFor_HonouredWhenPeerIsTrustedProxy(t *testing.T) {
	resetTrustedProxies("10.0.0.0/8")
	r := &http.Request{RemoteAddr: "10.1.2.3:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "203.0.113.7")
	if got := clientIP(r); got != "203.0.113.7" {
		t.Fatalf("expected XFF from a trusted peer to be honoured, got %q", got)
	}
}

// Rightmost-untrusted: with a single-hop trust model (one flat list of
// trusted proxies, no per-hop chain-of-custody), the rightmost XFF entry is
// the one the trusted proxy itself appended for the connection it directly
// observed. Entries to its left are whatever an untrusted upstream claimed.
func TestClientIP_ForwardedFor_MultipleValues_RightmostWins_WhenTrusted(t *testing.T) {
	resetTrustedProxies("10.0.0.0/8")
	r := &http.Request{RemoteAddr: "10.1.2.3:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "30.30.30.30, 20.20.20.20, 10.10.10.10")
	if got := clientIP(r); got != "10.10.10.10" {
		t.Fatalf("expected rightmost XFF value 10.10.10.10, got %q", got)
	}
}

func TestClientIP_ForwardedFor_MultipleValues_IgnoredWhenNotTrusted(t *testing.T) {
	resetTrustedProxies()
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "30.30.30.30, 20.20.20.20, 10.10.10.10")
	if got := clientIP(r); got != "1.2.3.4" {
		t.Fatalf("expected real peer 1.2.3.4 regardless of XFF contents, got %q", got)
	}
}

// Documents an intentional scope limit: once a proxy is trusted, C1 gates
// only on the peer's identity — it does not additionally validate that the
// chosen XFF segment is a syntactically valid IP address.
func TestClientIP_ForwardedFor_InvalidValue_PassedThroughUnvalidated_WhenTrusted(t *testing.T) {
	resetTrustedProxies("10.0.0.0/8")
	r := &http.Request{RemoteAddr: "10.1.2.3:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "not-an-ip")
	if got := clientIP(r); got != "not-an-ip" {
		t.Fatalf("expected unvalidated passthrough of %q, got %q", "not-an-ip", got)
	}
}

func TestParseTrustedProxyCIDRs_Empty_YieldsNoTrust(t *testing.T) {
	nets, err := parseTrustedProxyCIDRs("")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(nets) != 0 {
		t.Fatalf("expected no trusted proxies for empty config, got %d", len(nets))
	}
}

func TestParseTrustedProxyCIDRs_ValidList_Parses(t *testing.T) {
	nets, err := parseTrustedProxyCIDRs(" 10.0.0.0/8 , 172.16.0.0/12 ")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(nets) != 2 {
		t.Fatalf("expected 2 parsed CIDRs, got %d", len(nets))
	}
}

// The startup-safety requirement: a malformed CIDR must fail loudly (the
// caller, initConfig, turns this error into log.Fatal) rather than silently
// disabling proxy trust or silently ignoring the bad entry.
func TestParseTrustedProxyCIDRs_Malformed_ReturnsError(t *testing.T) {
	if _, err := parseTrustedProxyCIDRs("10.0.0.0/8, not-a-cidr"); err == nil {
		t.Fatal("expected an error for a malformed CIDR entry, got nil")
	}
}

func TestRateLimiter(t *testing.T) {
	// Configure the package-level limiter knobs directly for a deterministic test.
	rateLimitWindow = time.Minute
	rateLimitMax = 3

	rl := newRateLimiter()

	for i := 0; i < rateLimitMax; i++ {
		if !rl.Allow("10.0.0.1") {
			t.Fatalf("call %d for 10.0.0.1 should be allowed", i+1)
		}
	}
	if rl.Allow("10.0.0.1") {
		t.Fatal("call beyond rateLimitMax for 10.0.0.1 should be denied")
	}

	// A different IP has its own independent budget.
	if !rl.Allow("10.0.0.2") {
		t.Fatal("first call for a different IP should be allowed")
	}
}

// ── Test helpers ─────────────────────────────────────────────────────────────

// resetGlobalState clears the shared package-level state that handlers and
// the cleanup goroutine mutate. Must run before every handler-level test to
// keep tests independent, since the rooms/rate limiters are globals.
func resetGlobalState() {
	signalMu.Lock()
	signalRooms = make(map[string]*signalRoom)
	signalMu.Unlock()

	hostLimiter = newRateLimiter()
	joinLimiter = newRateLimiter()
	nowFunc = time.Now
	rateLimitWindow = time.Minute
	rateLimitMax = 10
	codeTTL = 30 * time.Minute
	trustedProxyCIDRs = nil // secure default: no trusted proxies
}

// startCleanup runs the cleanup goroutine for one test. It is stopped, and
// waited for, before the next test resets the globals it reads.
func postJSON(handler http.HandlerFunc, path, body string, xff string) *httptest.ResponseRecorder {
	return postJSONFrom(handler, path, body, "127.0.0.1:1234", xff)
}

// postJSONFrom lets a test vary the simulated socket peer, needed for any
// test exercising trusted-proxy XFF gating or per-peer rate limiting under
// the no-trusted-proxy (default) configuration.
func postJSONFrom(handler http.HandlerFunc, path, body, remoteAddr, xff string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodPost, path, bytes.NewBufferString(body))
	req.RemoteAddr = remoteAddr
	if xff != "" {
		req.Header.Set("X-Forwarded-For", xff)
	}
	rec := httptest.NewRecorder()
	handler(rec, req)
	return rec
}

// ── Retired HTTP session routes ──────────────────────────────────────────────

// /register and /connect were removed in Phase 4 (D7); the mux must not serve them.
func TestRetiredRoutes_Return404(t *testing.T) {
	resetGlobalState()
	mux := newMux()
	for _, path := range []string{"/register", "/connect"} {
		for _, method := range []string{http.MethodGet, http.MethodPost} {
			req := httptest.NewRequest(method, path, bytes.NewBufferString(`{}`))
			rec := httptest.NewRecorder()
			mux.ServeHTTP(rec, req)
			if rec.Code != http.StatusNotFound {
				t.Fatalf("%s %s: expected 404, got %d", method, path, rec.Code)
			}
		}
	}
}

// ── /health ──────────────────────────────────────────────────────────────────

func TestHandleHealth(t *testing.T) {
	resetGlobalState()
	signalMu.Lock()
	signalRooms["ABC234"] = &signalRoom{code: "ABC234", createdAt: time.Now()}
	signalMu.Unlock()

	req := httptest.NewRequest(http.MethodGet, "/health", nil)
	rec := httptest.NewRecorder()
	handleHealth(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var body map[string]interface{}
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatalf("bad response JSON: %v", err)
	}
	if body["status"] != "healthy" {
		t.Fatalf("expected status=healthy, got %v", body["status"])
	}
	if hosts, ok := body["hosts"].(float64); !ok || hosts != 1 {
		t.Fatalf("expected hosts=1, got %v", body["hosts"])
	}
	if _, ok := body["uptime"].(string); !ok {
		t.Fatalf("expected uptime string field, got %v", body["uptime"])
	}
}

// ── Rate limiter keyed by clientIP ───────────────────────────────────────────

// allow asks a limiter whether a request from the given socket peer (and
// X-Forwarded-For value) may proceed, resolving the identity as /signal does.
func allow(rl *rateLimiter, remoteAddr, xff string) bool {
	req := httptest.NewRequest(http.MethodGet, "/signal", nil)
	req.RemoteAddr = remoteAddr
	if xff != "" {
		req.Header.Set("X-Forwarded-For", xff)
	}
	return rl.Allow(clientIP(req))
}

// Distinct real clients are simulated by distinct socket peers: with no trusted
// proxy configured (the secure default) X-Forwarded-For is ignored.
func TestRateLimiting_PerIP_Boundary(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 3
	for i := 0; i < rateLimitMax; i++ {
		if !allow(hostLimiter, "1.1.1.1:1", "") {
			t.Fatalf("call %d: expected allowed", i+1)
		}
	}
	if allow(hostLimiter, "1.1.1.1:1", "") {
		t.Fatal("expected denial once over the limit")
	}
	if !allow(hostLimiter, "2.2.2.2:1", "") {
		t.Fatal("a different peer has an independent budget")
	}
}

// Rate limiting keys off the resolved IP in both TRUSTED_PROXY_CIDRS configurations.
func TestRateLimiting_KeysOffPeer_WhenNoTrustedProxyConfigured(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1
	if !allow(hostLimiter, "9.9.9.9:1", "1.1.1.1") {
		t.Fatal("first call should be allowed")
	}
	if allow(hostLimiter, "9.9.9.9:1", "2.2.2.2") {
		t.Fatal("spoofed differing XFF must not grant a separate budget")
	}
}

func TestRateLimiting_KeysOffForwardedFor_WhenPeerIsTrustedProxy(t *testing.T) {
	resetGlobalState()
	resetTrustedProxies("9.9.9.9/32")
	rateLimitMax = 1
	if !allow(hostLimiter, "9.9.9.9:1", "1.1.1.1") {
		t.Fatal("first call should be allowed")
	}
	if allow(hostLimiter, "9.9.9.9:1", "1.1.1.1") {
		t.Fatal("same resolved XFF IP should share the budget")
	}
	if !allow(hostLimiter, "9.9.9.9:1", "2.2.2.2") {
		t.Fatal("a different XFF IP behind the trusted proxy has its own budget")
	}
}

func TestRateLimiting_WindowSlides(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1
	rateLimitWindow = time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }

	if !allow(hostLimiter, "5.5.5.5:1", "") {
		t.Fatal("first call should be allowed")
	}
	if allow(hostLimiter, "5.5.5.5:1", "") {
		t.Fatal("expected denial within the window")
	}
	nowFunc = func() time.Time { return t0.Add(rateLimitWindow + time.Second) }
	if !allow(hostLimiter, "5.5.5.5:1", "") {
		t.Fatal("expected allowed after the window slides")
	}
}
