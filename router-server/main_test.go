package main

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
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
// keep tests independent, since hosts/hostsByCode/rate limiters are globals.
func resetGlobalState() {
	hostsMutex.Lock()
	hosts = make(map[string]*HostRegistration)
	hostsByCode = make(map[string]*HostRegistration)
	hostsMutex.Unlock()

	registerLimiter = newRateLimiter()
	connectLimiter = newRateLimiter()
	nowFunc = time.Now
	rateLimitWindow = time.Minute
	rateLimitMax = 10
	codeTTL = 30 * time.Minute
	trustedProxyCIDRs = nil // secure default: no trusted proxies
}

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

func registerHost(t *testing.T, hostID, xff string) RegistrationResponse {
	t.Helper()
	body := fmt.Sprintf(`{"hostId":%q,"hostName":"h","hostEndpoint":"10.1.1.1:9999"}`, hostID)
	rec := postJSON(handleRegister, "/register", body, xff)
	if rec.Code != http.StatusOK {
		t.Fatalf("registerHost: expected 200, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp RegistrationResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("registerHost: bad response JSON: %v", err)
	}
	return resp
}

// ── /register ────────────────────────────────────────────────────────────────

func TestHandleRegister_HappyPath(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostId":"h1","hostName":"Host One","hostEndpoint":"1.2.3.4:8888"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp RegistrationResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("bad response JSON: %v", err)
	}
	if resp.HostID != "h1" {
		t.Fatalf("expected hostId h1, got %q", resp.HostID)
	}
	if len(resp.ConnectionCode) != 6 {
		t.Fatalf("expected 6-char code, got %q", resp.ConnectionCode)
	}
	for _, c := range resp.ConnectionCode {
		if !strings.ContainsRune(codeChars, c) {
			t.Fatalf("code %q has char outside codeChars", resp.ConnectionCode)
		}
	}
	if len(resp.SessionSecret) != 64 {
		t.Fatalf("expected non-trivial 64-char secret, got %d chars", len(resp.SessionSecret))
	}
}

func TestHandleRegister_TwoRegistrations_DifferentCodesAndSecrets(t *testing.T) {
	resetGlobalState()
	r1 := registerHost(t, "h1", "")
	r2 := registerHost(t, "h2", "")
	if r1.ConnectionCode == r2.ConnectionCode {
		t.Fatalf("expected different codes, both were %q", r1.ConnectionCode)
	}
	if r1.SessionSecret == r2.SessionSecret {
		t.Fatal("expected different secrets, got the same value")
	}
}

func TestHandleRegister_MalformedJSON(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{not valid json`, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", rec.Code)
	}
}

// T6 (C3) baseline flip: TestHandleRegister_EmptyFields_AcceptedWithoutValidation
// previously pinned that handleRegister performed no validation and accepted
// empty hostId/hostName at 200. C3 adds presence/length validation, so this
// now asserts the opposite: empty required fields are rejected at 400, and
// nothing is stored.
func TestHandleRegister_EmptyRequiredFields_Rejected(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostId":"","hostName":"","hostEndpoint":""}`, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected empty hostId/hostName to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}
	hostsMutex.Lock()
	n := len(hosts)
	hostsMutex.Unlock()
	if n != 0 {
		t.Fatalf("expected no host to be stored after a rejected registration, got %d", n)
	}
}

func TestHandleRegister_MissingRequiredFields_Rejected(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostEndpoint":"1.2.3.4:8888"}`, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected missing hostId/hostName to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}
}

func TestHandleRegister_WhitespaceOnlyFields_Rejected(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostId":"   ","hostName":"h"}`, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected whitespace-only hostId to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}
}

func TestHandleRegister_OversizedFields_Rejected(t *testing.T) {
	resetGlobalState()
	oversized := strings.Repeat("a", maxHostIDLength+1)
	body := fmt.Sprintf(`{"hostId":%q,"hostName":"h"}`, oversized)
	rec := postJSON(handleRegister, "/register", body, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected oversized hostId to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}

	resetGlobalState()
	oversizedName := strings.Repeat("b", maxHostNameLength+1)
	body = fmt.Sprintf(`{"hostId":"h1","hostName":%q}`, oversizedName)
	rec = postJSON(handleRegister, "/register", body, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected oversized hostName to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}

	resetGlobalState()
	oversizedEndpoint := strings.Repeat("c", maxHostEndpointLength+1)
	body = fmt.Sprintf(`{"hostId":"h1","hostName":"h","hostEndpoint":%q}`, oversizedEndpoint)
	rec = postJSON(handleRegister, "/register", body, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected oversized hostEndpoint to be rejected with 400, got %d (%s)", rec.Code, rec.Body.String())
	}
}

// hostEndpoint remains optional: empty is a legitimate "use the socket peer
// as the default" signal, not a validation failure.
func TestHandleRegister_ValidRegistration_StillSucceeds(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostId":"h1","hostName":"My Computer","hostEndpoint":"192.168.1.100:8888"}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("expected valid registration to succeed, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp RegistrationResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("bad response JSON: %v", err)
	}
	if resp.HostID != "h1" || len(resp.ConnectionCode) != 6 {
		t.Fatalf("unexpected response: %+v", resp)
	}
}

func TestHandleRegister_WrongMethod(t *testing.T) {
	resetGlobalState()
	req := httptest.NewRequest(http.MethodGet, "/register", nil)
	rec := httptest.NewRecorder()
	handleRegister(rec, req)
	if rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("expected 405, got %d", rec.Code)
	}
}

func TestHandleRegister_EndpointDefaulting(t *testing.T) {
	resetGlobalState()
	req := httptest.NewRequest(http.MethodPost, "/register", bytes.NewBufferString(`{"hostId":"h1","hostName":"h"}`))
	req.RemoteAddr = "5.6.7.8:4321"
	rec := httptest.NewRecorder()
	handleRegister(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp RegistrationResponse
	json.Unmarshal(rec.Body.Bytes(), &resp)

	hostsMutex.Lock()
	stored, ok := hostsByCode[resp.ConnectionCode]
	hostsMutex.Unlock()
	if !ok {
		t.Fatal("expected host to be stored under its code")
	}
	want := "5.6.7.8:8888"
	if stored.HostEndpoint != want {
		t.Fatalf("expected default endpoint %q, got %q", want, stored.HostEndpoint)
	}
}

// ── /connect ─────────────────────────────────────────────────────────────────

func TestHandleConnect_HappyPath(t *testing.T) {
	resetGlobalState()
	reg := registerHost(t, "h1", "")
	rec := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp ConnectionResponse
	json.Unmarshal(rec.Body.Bytes(), &resp)
	if !resp.Success {
		t.Fatal("expected Success=true")
	}
	if resp.SessionSecret != reg.SessionSecret {
		t.Fatalf("expected matching secret, got %q want %q", resp.SessionSecret, reg.SessionSecret)
	}
	if resp.HostEndpoint != "10.1.1.1:9999" {
		t.Fatalf("expected registered endpoint, got %q", resp.HostEndpoint)
	}
}

func TestHandleConnect_OneTimeUse(t *testing.T) {
	resetGlobalState()
	reg := registerHost(t, "h1", "")
	body := fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode)

	first := postJSON(handleConnect, "/connect", body, "")
	if first.Code != http.StatusOK {
		t.Fatalf("first connect: expected 200, got %d", first.Code)
	}

	second := postJSON(handleConnect, "/connect", body, "")
	if second.Code != http.StatusNotFound {
		t.Fatalf("second connect with same code: expected 404, got %d", second.Code)
	}
}

func TestHandleConnect_UnknownCode(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleConnect, "/connect", `{"connectionCode":"ZZZZZZ","clientId":"c1"}`, "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404, got %d", rec.Code)
	}
}

// T6 (C2) baseline flip: TestHandleConnect_ExpiredCode_NotEnforcedAtConnectTime
// previously pinned that handleConnect never checked codeTTL itself, relying
// solely on the background sweep. C2 adds an expiry check directly in
// handleConnect, so this now asserts the opposite: an unswept but expired
// code is rejected at connect time, using the same nowFunc seam (no real
// waiting) that the old baseline test used.
func TestHandleConnect_ExpiredCode_EnforcedAtConnectTime_WithoutWaitingForSweep(t *testing.T) {
	resetGlobalState()
	codeTTL = 30 * time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }

	reg := registerHost(t, "h1", "")

	// Advance well past codeTTL. cleanupExpiredHosts is not running in this
	// test, so nothing has swept the entry yet — enforcement must come from
	// handleConnect itself, not the sweep.
	nowFunc = func() time.Time { return t0.Add(codeTTL + time.Hour) }

	rec := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected an expired-but-unswept code to be rejected at connect time (404), got %d (%s)", rec.Code, rec.Body.String())
	}
}

// Required: the caller must not be able to distinguish "was valid, now
// expired" from "never existed" — same status code and same response body.
func TestHandleConnect_ExpiredCode_IndistinguishableFromUnknownCode(t *testing.T) {
	resetGlobalState()
	codeTTL = 30 * time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }
	reg := registerHost(t, "h1", "")
	nowFunc = func() time.Time { return t0.Add(codeTTL + time.Hour) }

	expired := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
	unknown := postJSON(handleConnect, "/connect", `{"connectionCode":"ZZZZZZ","clientId":"c1"}`, "")

	if expired.Code != unknown.Code {
		t.Fatalf("status codes differ: expired=%d unknown=%d", expired.Code, unknown.Code)
	}
	if expired.Body.String() != unknown.Body.String() {
		t.Fatalf("response bodies differ, callers could enumerate expired vs unknown codes:\nexpired: %s\nunknown: %s",
			expired.Body.String(), unknown.Body.String())
	}
}

// A code registered exactly at the TTL boundary (not yet strictly over it)
// must still connect — codeTTL is an inclusive "valid through" duration, not
// an off-by-one trap. Matches cleanupExpiredHosts' own `>` comparison.
func TestHandleConnect_CodeAtExactTTLBoundary_StillValid(t *testing.T) {
	resetGlobalState()
	codeTTL = 30 * time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }
	reg := registerHost(t, "h1", "")

	nowFunc = func() time.Time { return t0.Add(codeTTL) }

	rec := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("expected a code exactly at the TTL boundary to still be valid, got %d (%s)", rec.Code, rec.Body.String())
	}
}

// Positive counterpart: proves codeTTL does eventually take effect once the
// background cleanup goroutine actually runs. Uses tiny real durations
// (milliseconds) plus a short bounded poll rather than sleeping through the
// production 30-minute window — the cleanup ticker fires on real wall-clock
// time and cannot be redirected through the nowFunc seam without a second
// production edit, which is out of scope for this task.
func TestCleanupExpiredHosts_EventuallyRemovesExpiredCode(t *testing.T) {
	resetGlobalState()
	codeTTL = 5 * time.Millisecond
	cleanupInterval = 5 * time.Millisecond

	reg := registerHost(t, "h1", "")

	go cleanupExpiredHosts()

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		hostsMutex.Lock()
		_, exists := hostsByCode[reg.ConnectionCode]
		hostsMutex.Unlock()
		if !exists {
			// Swept. Confirm /connect now reports it gone.
			rec := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
			if rec.Code != http.StatusNotFound {
				t.Fatalf("expected 404 after cleanup sweep, got %d", rec.Code)
			}
			return
		}
		time.Sleep(2 * time.Millisecond)
	}
	t.Fatal("expired code was not swept by cleanupExpiredHosts within 2s")
}

func TestHandleConnect_MalformedJSON(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleConnect, "/connect", `{bad json`, "")
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", rec.Code)
	}
}

func TestHandleConnect_MissingFields(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleConnect, "/connect", `{}`, "")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404 for empty connectionCode, got %d", rec.Code)
	}
}

func TestHandleConnect_WrongMethod(t *testing.T) {
	resetGlobalState()
	req := httptest.NewRequest(http.MethodGet, "/connect", nil)
	rec := httptest.NewRecorder()
	handleConnect(rec, req)
	if rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("expected 405, got %d", rec.Code)
	}
}

// Documents actual behaviour: codes are matched by exact map key, so lookups
// are case-sensitive. A lowercased variant of a valid (uppercase-generated)
// code is treated as an unknown code.
func TestHandleConnect_CaseSensitivity(t *testing.T) {
	resetGlobalState()
	hostsMutex.Lock()
	h := &HostRegistration{HostID: "h1", HostEndpoint: "e:1", SessionSecret: "s", ConnectionCode: "ABC123", RegisteredAt: time.Now()}
	hosts["h1"] = h
	hostsByCode["ABC123"] = h
	hostsMutex.Unlock()

	lower := postJSON(handleConnect, "/connect", `{"connectionCode":"abc123","clientId":"c1"}`, "")
	if lower.Code != http.StatusNotFound {
		t.Fatalf("expected lowercase variant to be treated as unknown (404), got %d", lower.Code)
	}

	exact := postJSON(handleConnect, "/connect", `{"connectionCode":"ABC123","clientId":"c1"}`, "")
	if exact.Code != http.StatusOK {
		t.Fatalf("expected exact-case code to succeed, got %d", exact.Code)
	}
}

// ── /health ──────────────────────────────────────────────────────────────────

func TestHandleHealth(t *testing.T) {
	resetGlobalState()
	registerHost(t, "h1", "")

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

// ── Rate limiter (handler-level) ─────────────────────────────────────────────

// T6 (C1) note: this test previously differentiated "different IPs" via
// X-Forwarded-For while every request shared the same socket peer. Now that
// XFF is ignored by default (no trusted proxy configured — the secure
// default), that no longer simulates two different clients; it would
// collapse onto one rate-limit bucket and the "different IP" assertion
// would fail. Rewritten to differentiate by socket peer instead, which is
// the correct way to simulate distinct real clients hitting the limiter
// directly (the deployment this default is for).
func TestRateLimiting_Register_PerIP_BoundaryAndStatusCode(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 3
	rateLimitWindow = time.Minute

	for i := 0; i < rateLimitMax; i++ {
		rec := postJSONFrom(handleRegister, "/register", fmt.Sprintf(`{"hostId":"h%d","hostName":"h"}`, i), "1.1.1.1:1", "")
		if rec.Code != http.StatusOK {
			t.Fatalf("call %d: expected 200, got %d", i+1, rec.Code)
		}
	}
	over := postJSONFrom(handleRegister, "/register", `{"hostId":"hN","hostName":"h"}`, "1.1.1.1:1", "")
	if over.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429 once over limit, got %d", over.Code)
	}

	// A different source peer has an independent budget.
	other := postJSONFrom(handleRegister, "/register", `{"hostId":"hOther","hostName":"h"}`, "2.2.2.2:1", "")
	if other.Code != http.StatusOK {
		t.Fatalf("expected different peer IP to be unaffected, got %d", other.Code)
	}
}

// See note on TestRateLimiting_Register_PerIP_BoundaryAndStatusCode above —
// same rewrite, same reason.
func TestRateLimiting_Connect_PerIP(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 2
	rateLimitWindow = time.Minute

	for i := 0; i < rateLimitMax; i++ {
		rec := postJSONFrom(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "3.3.3.3:1", "")
		if rec.Code != http.StatusNotFound {
			t.Fatalf("call %d: expected 404 (unknown code, not yet rate-limited), got %d", i+1, rec.Code)
		}
	}
	over := postJSONFrom(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "3.3.3.3:1", "")
	if over.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429 once over limit, got %d", over.Code)
	}

	other := postJSONFrom(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "4.4.4.4:1", "")
	if other.Code != http.StatusNotFound {
		t.Fatalf("expected different peer IP to be unaffected (404, not 429), got %d", other.Code)
	}
}

// Explicit spec requirement: rate limiting keys off the resolved IP in both
// TRUSTED_PROXY_CIDRS configurations.
func TestRateLimiting_KeysOffPeer_WhenNoTrustedProxyConfigured(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1
	rateLimitWindow = time.Minute

	// Same peer, different (spoofed) XFF values: since no proxy is trusted,
	// XFF must be ignored, so the second call shares the first's budget.
	first := postJSONFrom(handleRegister, "/register", `{"hostId":"h1","hostName":"h"}`, "9.9.9.9:1", "1.1.1.1")
	if first.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", first.Code)
	}
	second := postJSONFrom(handleRegister, "/register", `{"hostId":"h2","hostName":"h"}`, "9.9.9.9:1", "2.2.2.2")
	if second.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429: spoofed differing XFF must not grant a separate budget, got %d", second.Code)
	}
}

func TestRateLimiting_KeysOffForwardedFor_WhenPeerIsTrustedProxy(t *testing.T) {
	resetGlobalState()
	resetTrustedProxies("9.9.9.9/32")
	rateLimitMax = 1
	rateLimitWindow = time.Minute

	// Same (trusted) peer, different XFF: each XFF value gets its own budget.
	first := postJSONFrom(handleRegister, "/register", `{"hostId":"h1","hostName":"h"}`, "9.9.9.9:1", "1.1.1.1")
	if first.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", first.Code)
	}
	second := postJSONFrom(handleRegister, "/register", `{"hostId":"h2","hostName":"h"}`, "9.9.9.9:1", "1.1.1.1")
	if second.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429: same resolved XFF IP should share the budget, got %d", second.Code)
	}
	other := postJSONFrom(handleRegister, "/register", `{"hostId":"h3","hostName":"h"}`, "9.9.9.9:1", "2.2.2.2")
	if other.Code != http.StatusOK {
		t.Fatalf("expected 200: a different XFF IP behind the trusted proxy has its own budget, got %d", other.Code)
	}
}

func TestRateLimiting_WindowSlides(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1
	rateLimitWindow = time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }

	first := postJSON(handleRegister, "/register", `{"hostId":"h1","hostName":"h"}`, "5.5.5.5")
	if first.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", first.Code)
	}
	blocked := postJSON(handleRegister, "/register", `{"hostId":"h2","hostName":"h"}`, "5.5.5.5")
	if blocked.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429 within window, got %d", blocked.Code)
	}

	// Slide fully past the window via the seam — no real sleeping.
	nowFunc = func() time.Time { return t0.Add(rateLimitWindow + time.Second) }
	after := postJSON(handleRegister, "/register", `{"hostId":"h3","hostName":"h"}`, "5.5.5.5")
	if after.Code != http.StatusOK {
		t.Fatalf("expected 200 after window slides, got %d", after.Code)
	}
}

// ── Concurrency ──────────────────────────────────────────────────────────────

func TestConcurrentRegister_NoDuplicateCodesNoRace(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1000

	const n = 50
	var wg sync.WaitGroup
	codes := make([]string, n)
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			xff := fmt.Sprintf("10.0.%d.%d", i/256, i%256)
			body := fmt.Sprintf(`{"hostId":"h%d","hostName":"h"}`, i)
			rec := postJSON(handleRegister, "/register", body, xff)
			var resp RegistrationResponse
			json.Unmarshal(rec.Body.Bytes(), &resp)
			codes[i] = resp.ConnectionCode
		}(i)
	}
	wg.Wait()

	seen := make(map[string]bool, n)
	for i, c := range codes {
		if c == "" {
			t.Fatalf("goroutine %d got an empty code", i)
		}
		if seen[c] {
			t.Fatalf("duplicate code %q generated", c)
		}
		seen[c] = true
	}
}

func TestConcurrentConnect_SameCode_ExactlyOneWins(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1000
	reg := registerHost(t, "h1", "")

	const n = 25
	var wg sync.WaitGroup
	var successCount int32
	var mu sync.Mutex
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			xff := fmt.Sprintf("20.0.%d.%d", i/256, i%256)
			body := fmt.Sprintf(`{"connectionCode":%q,"clientId":"c%d"}`, reg.ConnectionCode, i)
			rec := postJSON(handleConnect, "/connect", body, xff)
			if rec.Code == http.StatusOK {
				mu.Lock()
				successCount++
				mu.Unlock()
			}
		}(i)
	}
	wg.Wait()

	if successCount != 1 {
		t.Fatalf("expected exactly 1 winner, got %d", successCount)
	}
}

func TestRegistrationRacingCleanup(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 1000
	// Long TTL: registrations made during this test must survive the sweep.
	codeTTL = time.Hour
	cleanupInterval = 2 * time.Millisecond

	go cleanupExpiredHosts()

	const n = 30
	var wg sync.WaitGroup
	codes := make([]string, n)
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			xff := fmt.Sprintf("30.0.%d.%d", i/256, i%256)
			body := fmt.Sprintf(`{"hostId":"racer%d","hostName":"h"}`, i)
			rec := postJSON(handleRegister, "/register", body, xff)
			var resp RegistrationResponse
			json.Unmarshal(rec.Body.Bytes(), &resp)
			codes[i] = resp.ConnectionCode
		}(i)
	}
	wg.Wait()

	hostsMutex.Lock()
	defer hostsMutex.Unlock()
	for i, c := range codes {
		if _, ok := hostsByCode[c]; !ok {
			t.Fatalf("host %d (code %q) was swept despite long codeTTL — cleanup raced registration incorrectly", i, c)
		}
	}
}
