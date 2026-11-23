package main

import (
	"context"
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
	initSignalConfig()

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

// ── State ────────────────────────────────────────────────────────────────────

var (
	// codeMu serializes code generation with room creation in signal.go.
	codeMu sync.Mutex
	// No 0/O or 1/I: people read codes aloud and retype them. 32 characters also divide
	// 256 evenly, so each is equally likely.
	codeChars  = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
	codeLength = 6
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
	// hostLimiter covers the /signal register and resume messages, joinLimiter covers join.
	hostLimiter = newRateLimiter()
	joinLimiter = newRateLimiter()
)

// ── Entry point ──────────────────────────────────────────────────────────────

var startTime = time.Now()

func main() {
	initConfig()
	go cleanupExpiredRooms(context.Background())

	mux := newMux()

	fmt.Printf("Router Server starting on port %s\n", serverPort)
	fmt.Printf("  code_ttl=%s  cleanup=%s  rate_limit=%d/%s\n",
		codeTTL, cleanupInterval, rateLimitMax, rateLimitWindow)

	if err := http.ListenAndServe(":"+serverPort, mux); err != nil {
		log.Fatal("Server failed to start:", err)
	}
}

// newMux builds the router's routes. /register and /connect (the HTTP session routes) were
// removed in Phase 4; they now fall through to the mux's 404.
func newMux() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/signal", handleSignal)
	mux.HandleFunc("/health", handleHealth)
	return mux
}

// ── Handlers ─────────────────────────────────────────────────────────────────

func handleHealth(w http.ResponseWriter, r *http.Request) {
	signalMu.Lock()
	count := len(signalRooms)
	signalMu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"status": "healthy",
		"hosts":  count,
		"uptime": time.Since(startTime).String(),
	})
}

// ── Helpers ───────────────────────────────────────────────────────────────────

// generateUniqueCodeLocked must be called with codeMu held. A code is unique among the /signal
// rooms.
func generateUniqueCodeLocked() (string, error) {
	for i := 0; i < 10; i++ {
		code, err := generateCode()
		if err != nil {
			return "", err
		}
		if !signalCodeInUse(code) {
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

// cleanupExpiredRooms sweeps expired /signal rooms until ctx is cancelled.
func cleanupExpiredRooms(ctx context.Context) {
	ticker := time.NewTicker(cleanupInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		sweepSignalRooms(nowFunc())
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
