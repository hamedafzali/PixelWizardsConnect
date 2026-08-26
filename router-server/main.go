package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ── Config (overridable via environment variables) ───────────────────────────

var (
	serverPort      string
	codeTTL         time.Duration
	cleanupInterval time.Duration
	rateLimitWindow time.Duration
	rateLimitMax    int

	// trustedProxyCIDRs is empty by default: no proxy is trusted, so
	// X-Forwarded-For is always ignored and the raw socket peer is used as
	// the client identity. Secure by default for direct-exposure deployments.
	trustedProxyCIDRs []*net.IPNet
)

func initConfig() {
	serverPort = getEnv("PORT", "9000")
	codeTTL = getDurationEnv("CODE_TTL", 30*time.Minute)
	cleanupInterval = getDurationEnv("CLEANUP_INTERVAL", 5*time.Minute)
	rateLimitWindow = getDurationEnv("RATE_LIMIT_WINDOW", time.Minute)
	rateLimitMax = getIntEnv("RATE_LIMIT_MAX", 10)

	cidrs, err := parseTrustedProxyCIDRs(getEnv("TRUSTED_PROXY_CIDRS", ""))
	if err != nil {
		log.Fatalf("invalid TRUSTED_PROXY_CIDRS: %v", err)
	}
	trustedProxyCIDRs = cidrs
}

// parseTrustedProxyCIDRs parses a comma-separated CIDR list. An empty string
// yields no trusted proxies (nil, not an error) — that is the secure default.
// Any entry that fails to parse as a CIDR is a hard error: a typo'd CIDR
// silently disabling proxy trust (and thus the rate limiter) is worse than
// refusing to start.
func parseTrustedProxyCIDRs(raw string) ([]*net.IPNet, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil, nil
	}
	entries := strings.Split(raw, ",")
	nets := make([]*net.IPNet, 0, len(entries))
	for _, entry := range entries {
		entry = strings.TrimSpace(entry)
		if entry == "" {
			continue
		}
		_, ipNet, err := net.ParseCIDR(entry)
		if err != nil {
			return nil, fmt.Errorf("%q is not a valid CIDR: %w", entry, err)
		}
		nets = append(nets, ipNet)
	}
	return nets, nil
}

func isTrustedProxy(ip net.IP) bool {
	if ip == nil {
		return false
	}
	for _, ipNet := range trustedProxyCIDRs {
		if ipNet.Contains(ip) {
			return true
		}
	}
	return false
}

// ── Data types ───────────────────────────────────────────────────────────────

type HostRegistration struct {
	HostID         string    `json:"hostId"`
	HostName       string    `json:"hostName"`
	HostEndpoint   string    `json:"hostEndpoint"`
	ConnectionCode string    `json:"connectionCode"`
	SessionSecret  string    `json:"sessionSecret"`
	RegisteredAt   time.Time `json:"registeredAt"`
}

// Length bounds on caller-supplied /register fields. hostId and hostName are
// required (non-empty after trimming whitespace); hostEndpoint may be empty
// (handleRegister defaults it from the socket peer) but is still bounded
// since it is stored for up to codeTTL. 256 comfortably covers legitimate
// values (UUIDs, hostnames, "host:port" pairs) while capping the per-entry
// memory a malicious or buggy caller can force onto the hosts map.
const (
	maxHostIDLength       = 256
	maxHostNameLength     = 256
	maxHostEndpointLength = 256
)

// validateRegistration checks presence and length bounds on the fields a
// caller supplies to /register. connectionCode, sessionSecret, and
// registeredAt are excluded: handleRegister overwrites all three itself
// after decoding, so any client-supplied values are discarded before being
// persisted and need no validation here.
func validateRegistration(reg *HostRegistration) error {
	if strings.TrimSpace(reg.HostID) == "" {
		return fmt.Errorf("hostId is required")
	}
	if len(reg.HostID) > maxHostIDLength {
		return fmt.Errorf("hostId exceeds maximum length of %d", maxHostIDLength)
	}
	if strings.TrimSpace(reg.HostName) == "" {
		return fmt.Errorf("hostName is required")
	}
	if len(reg.HostName) > maxHostNameLength {
		return fmt.Errorf("hostName exceeds maximum length of %d", maxHostNameLength)
	}
	if len(reg.HostEndpoint) > maxHostEndpointLength {
		return fmt.Errorf("hostEndpoint exceeds maximum length of %d", maxHostEndpointLength)
	}
	return nil
}

type ConnectionRequest struct {
	ConnectionCode string `json:"connectionCode"`
	ClientID       string `json:"clientId"`
}

type RegistrationResponse struct {
	ConnectionCode string `json:"connectionCode"`
	HostID         string `json:"hostId"`
	SessionSecret  string `json:"sessionSecret"`
}

type ConnectionResponse struct {
	HostEndpoint  string `json:"hostEndpoint"`
	SessionSecret string `json:"sessionSecret"`
	Success       bool   `json:"success"`
	Message       string `json:"message"`
}

// ── State ────────────────────────────────────────────────────────────────────

var (
	hosts       = make(map[string]*HostRegistration)
	hostsByCode = make(map[string]*HostRegistration)
	hostsMutex  sync.Mutex
	codeChars   = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	codeLength  = 6
)

// ── Rate limiter ─────────────────────────────────────────────────────────────

// nowFunc is a seam so tests can control TTL/rate-limit-window elapsing
// without sleeping through real windows. Production code always uses the
// default (time.Now); only tests swap it.
var nowFunc = time.Now

type rateLimiter struct {
	mu     sync.Mutex
	counts map[string][]time.Time
}

func newRateLimiter() *rateLimiter {
	return &rateLimiter{counts: make(map[string][]time.Time)}
}

func (r *rateLimiter) Allow(ip string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	now := nowFunc()
	prev := r.counts[ip]
	valid := prev[:0]
	for _, t := range prev {
		if now.Sub(t) < rateLimitWindow {
			valid = append(valid, t)
		}
	}
	if len(valid) >= rateLimitMax {
		r.counts[ip] = valid
		return false
	}
	r.counts[ip] = append(valid, now)
	return true
}

var (
	registerLimiter = newRateLimiter()
	connectLimiter  = newRateLimiter()
)

// ── Entry point ──────────────────────────────────────────────────────────────

var startTime = time.Now()

func main() {
	initConfig()
	go cleanupExpiredHosts()

	http.HandleFunc("/register", handleRegister)
	http.HandleFunc("/connect", handleConnect)
	http.HandleFunc("/health", handleHealth)

	fmt.Printf("Router Server starting on port %s\n", serverPort)
	fmt.Printf("  code_ttl=%s  cleanup=%s  rate_limit=%d/%s\n",
		codeTTL, cleanupInterval, rateLimitMax, rateLimitWindow)

	if err := http.ListenAndServe(":"+serverPort, nil); err != nil {
		log.Fatal("Server failed to start:", err)
	}
}

// ── Handlers ─────────────────────────────────────────────────────────────────

func handleRegister(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !registerLimiter.Allow(clientIP(r)) {
		http.Error(w, "Too many requests", http.StatusTooManyRequests)
		return
	}

	var reg HostRegistration
	if err := json.NewDecoder(r.Body).Decode(&reg); err != nil {
		http.Error(w, "Invalid request body", http.StatusBadRequest)
		return
	}

	if err := validateRegistration(&reg); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	secret, err := generateSecret()
	if err != nil {
		http.Error(w, "Internal error", http.StatusInternalServerError)
		return
	}

	if reg.HostEndpoint == "" {
		reg.HostEndpoint = fmt.Sprintf("%s:8888", clientIP(r))
	}
	reg.SessionSecret = secret
	reg.RegisteredAt = nowFunc()

	// Generate a unique code and store — all under a single lock to avoid TOCTOU races.
	hostsMutex.Lock()
	code, codeErr := generateUniqueCodeLocked()
	if codeErr == nil {
		reg.ConnectionCode = code
		hosts[reg.HostID] = &reg
		hostsByCode[code] = &reg
	}
	hostsMutex.Unlock()

	if codeErr != nil {
		http.Error(w, "Failed to generate unique code", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(RegistrationResponse{
		ConnectionCode: code,
		HostID:         reg.HostID,
		SessionSecret:  secret,
	})
	log.Printf("Host registered: ID=%s Code=%s Endpoint=%s", reg.HostID, code, reg.HostEndpoint)
}

func handleConnect(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !connectLimiter.Allow(clientIP(r)) {
		http.Error(w, "Too many requests", http.StatusTooManyRequests)
		return
	}

	var req ConnectionRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "Invalid request body", http.StatusBadRequest)
		return
	}

	hostsMutex.Lock()
	host, exists := hostsByCode[req.ConnectionCode]
	if exists {
		// One-time use: delete immediately so the code cannot be reused,
		// whether or not it turns out to be expired below.
		delete(hostsByCode, req.ConnectionCode)
		delete(hosts, host.HostID)
		if nowFunc().Sub(host.RegisteredAt) > codeTTL {
			// Expired but not yet swept by cleanupExpiredHosts. Treat
			// identically to "never existed": exists=false and no
			// reference to `host` below, so the caller gets the exact
			// same response as an unknown code and cannot distinguish
			// "valid then expired" from "never issued".
			exists = false
		}
	}
	hostsMutex.Unlock()

	if !exists {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusNotFound)
		json.NewEncoder(w).Encode(ConnectionResponse{Success: false, Message: "Invalid or expired connection code"})
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(ConnectionResponse{
		HostEndpoint:  host.HostEndpoint,
		SessionSecret: host.SessionSecret,
		Success:       true,
		Message:       "Connected",
	})
	log.Printf("Client connected: Code=%s Endpoint=%s", req.ConnectionCode, host.HostEndpoint)
}

func handleHealth(w http.ResponseWriter, r *http.Request) {
	hostsMutex.Lock()
	count := len(hosts)
	hostsMutex.Unlock()
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"status": "healthy",
		"hosts":  count,
		"uptime": time.Since(startTime).String(),
	})
}

// ── Helpers ───────────────────────────────────────────────────────────────────

// generateUniqueCodeLocked must be called with hostsMutex held.
func generateUniqueCodeLocked() (string, error) {
	for i := 0; i < 10; i++ {
		code, err := generateCode()
		if err != nil {
			return "", err
		}
		if _, exists := hostsByCode[code]; !exists {
			return code, nil
		}
	}
	return "", fmt.Errorf("could not generate unique code after 10 attempts")
}

func generateCode() (string, error) {
	b := make([]byte, codeLength)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	code := make([]byte, codeLength)
	for i, v := range b {
		code[i] = codeChars[int(v)%len(codeChars)]
	}
	return string(code), nil
}

func generateSecret() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

// clientIP resolves the caller's identity for rate limiting. X-Forwarded-For
// is honoured only when the direct socket peer is a configured trusted
// proxy; otherwise it is ignored entirely and the socket peer is used,
// regardless of what the header claims. This is a single-hop trust model:
// when trusted, the RIGHTMOST X-Forwarded-For entry is taken, since that is
// the value the trusted proxy itself observed on its inbound connection
// (each proxy appends the peer it sees to the right of the header as it
// forwards the request) — everything to its left is whatever the client
// or an upstream (untrusted, from our point of view) hop claimed.
func clientIP(r *http.Request) string {
	peer := r.RemoteAddr
	if idx := strings.LastIndex(peer, ":"); idx >= 0 {
		peer = peer[:idx]
	}

	if xff := r.Header.Get("X-Forwarded-For"); xff != "" && isTrustedProxy(net.ParseIP(peer)) {
		parts := strings.Split(xff, ",")
		if rightmost := strings.TrimSpace(parts[len(parts)-1]); rightmost != "" {
			return rightmost
		}
	}

	return peer
}

func cleanupExpiredHosts() {
	ticker := time.NewTicker(cleanupInterval)
	defer ticker.Stop()
	for range ticker.C {
		hostsMutex.Lock()
		now := nowFunc()
		for id, host := range hosts {
			if now.Sub(host.RegisteredAt) > codeTTL {
				delete(hosts, id)
				delete(hostsByCode, host.ConnectionCode)
				log.Printf("Cleaned up expired host: ID=%s", id)
			}
		}
		hostsMutex.Unlock()
	}
}

// ── Env helpers ───────────────────────────────────────────────────────────────

func getEnv(key, defaultVal string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return defaultVal
}

func getDurationEnv(key string, defaultVal time.Duration) time.Duration {
	if v := os.Getenv(key); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			return d
		}
	}
	return defaultVal
}

func getIntEnv(key string, defaultVal int) int {
	if v := os.Getenv(key); v != "" {
		if i, err := strconv.Atoi(v); err == nil {
			return i
		}
	}
	return defaultVal
}
