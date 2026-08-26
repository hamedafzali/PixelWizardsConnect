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

func TestClientIP_ForwardedFor(t *testing.T) {
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "9.9.9.9, 1.1.1.1")
	if got := clientIP(r); got != "9.9.9.9" {
		t.Fatalf("expected 9.9.9.9, got %q", got)
	}
}

// Pinned baseline for T6: clientIP trusts X-Forwarded-For unconditionally —
// no allow-list of trusted proxies, no validation that the value is even a
// real IP address. T6 deliberately replaces this with TRUSTED_PROXY_CIDRS
// gating. Do not "fix" this here; these tests exist to freeze the current
// behaviour so T6 has a baseline to diff against.
func TestClientIP_ForwardedFor_TrustedUnconditionally_Baseline(t *testing.T) {
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "203.0.113.7")
	if got := clientIP(r); got != "203.0.113.7" {
		t.Fatalf("expected the XFF value to be trusted as-is, got %q", got)
	}
}

func TestClientIP_ForwardedFor_MultipleValues_LeftmostWins(t *testing.T) {
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "10.10.10.10, 20.20.20.20, 30.30.30.30")
	if got := clientIP(r); got != "10.10.10.10" {
		t.Fatalf("expected leftmost XFF value, got %q", got)
	}
}

// Documents actual behaviour, not a requirement: clientIP performs no IP
// syntax validation at all, so a garbage XFF value passes through unchanged.
func TestClientIP_ForwardedFor_InvalidIPValue_PassedThroughUnvalidated(t *testing.T) {
	r := &http.Request{RemoteAddr: "1.2.3.4:5678", Header: http.Header{}}
	r.Header.Set("X-Forwarded-For", "not-an-ip")
	if got := clientIP(r); got != "not-an-ip" {
		t.Fatalf("expected unvalidated passthrough of %q, got %q", "not-an-ip", got)
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
}

func postJSON(handler http.HandlerFunc, path, body string, xff string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodPost, path, bytes.NewBufferString(body))
	req.RemoteAddr = "127.0.0.1:1234"
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

// Documents actual behaviour: handleRegister performs no validation of
// required fields. An empty hostId is accepted and stored under the ""
// key in the hosts map. This is a real gap, not an assumption — see
// docs/BACKLOG.md.
func TestHandleRegister_EmptyFields_AcceptedWithoutValidation(t *testing.T) {
	resetGlobalState()
	rec := postJSON(handleRegister, "/register", `{"hostId":"","hostName":"","hostEndpoint":""}`, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("current behaviour accepts empty fields; expected 200, got %d (%s)", rec.Code, rec.Body.String())
	}
	var resp RegistrationResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("bad response JSON: %v", err)
	}
	if resp.HostID != "" {
		t.Fatalf("expected empty hostId to round-trip as empty, got %q", resp.HostID)
	}
	if len(resp.ConnectionCode) != 6 {
		t.Fatalf("expected a code to still be issued, got %q", resp.ConnectionCode)
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

// Finding (pinned as current behaviour, not fixed here — see docs/BACKLOG.md):
// handleConnect never checks codeTTL itself. Expiry is enforced only by the
// background cleanupExpiredHosts sweep. A code registered far enough in the
// past to be "expired" per codeTTL, but not yet swept, still connects
// successfully. This is the T6 baseline for expiry, established via the
// nowFunc seam — no real waiting involved.
func TestHandleConnect_ExpiredCode_NotEnforcedAtConnectTime(t *testing.T) {
	resetGlobalState()
	codeTTL = 30 * time.Minute
	t0 := time.Now()
	nowFunc = func() time.Time { return t0 }

	reg := registerHost(t, "h1", "")

	// Advance well past codeTTL. cleanupExpiredHosts is not running in this
	// test, so nothing has swept the entry yet.
	nowFunc = func() time.Time { return t0.Add(codeTTL + time.Hour) }

	rec := postJSON(handleConnect, "/connect", fmt.Sprintf(`{"connectionCode":%q,"clientId":"c1"}`, reg.ConnectionCode), "")
	if rec.Code != http.StatusOK {
		t.Fatalf("current behaviour: connect on an unswept expired code still succeeds; expected 200, got %d (%s)", rec.Code, rec.Body.String())
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

func TestRateLimiting_Register_PerIP_BoundaryAndStatusCode(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 3
	rateLimitWindow = time.Minute

	for i := 0; i < rateLimitMax; i++ {
		rec := postJSON(handleRegister, "/register", fmt.Sprintf(`{"hostId":"h%d","hostName":"h"}`, i), "1.1.1.1")
		if rec.Code != http.StatusOK {
			t.Fatalf("call %d: expected 200, got %d", i+1, rec.Code)
		}
	}
	over := postJSON(handleRegister, "/register", `{"hostId":"hN","hostName":"h"}`, "1.1.1.1")
	if over.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429 once over limit, got %d", over.Code)
	}

	// A different source IP has an independent budget.
	other := postJSON(handleRegister, "/register", `{"hostId":"hOther","hostName":"h"}`, "2.2.2.2")
	if other.Code != http.StatusOK {
		t.Fatalf("expected different IP to be unaffected, got %d", other.Code)
	}
}

func TestRateLimiting_Connect_PerIP(t *testing.T) {
	resetGlobalState()
	rateLimitMax = 2
	rateLimitWindow = time.Minute

	for i := 0; i < rateLimitMax; i++ {
		rec := postJSON(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "3.3.3.3")
		if rec.Code != http.StatusNotFound {
			t.Fatalf("call %d: expected 404 (unknown code, not yet rate-limited), got %d", i+1, rec.Code)
		}
	}
	over := postJSON(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "3.3.3.3")
	if over.Code != http.StatusTooManyRequests {
		t.Fatalf("expected 429 once over limit, got %d", over.Code)
	}

	other := postJSON(handleConnect, "/connect", `{"connectionCode":"NOPE00","clientId":"c"}`, "4.4.4.4")
	if other.Code != http.StatusNotFound {
		t.Fatalf("expected different IP to be unaffected (404, not 429), got %d", other.Code)
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
