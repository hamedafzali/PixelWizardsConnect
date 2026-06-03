package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
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
)

func initConfig() {
	serverPort      = getEnv("PORT", "9000")
	codeTTL         = getDurationEnv("CODE_TTL", 30*time.Minute)
	cleanupInterval = getDurationEnv("CLEANUP_INTERVAL", 5*time.Minute)
	rateLimitWindow = getDurationEnv("RATE_LIMIT_WINDOW", time.Minute)
	rateLimitMax    = getIntEnv("RATE_LIMIT_MAX", 10)
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
	now := time.Now()
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

	secret, err := generateSecret()
	if err != nil {
		http.Error(w, "Internal error", http.StatusInternalServerError)
		return
	}

	if reg.HostEndpoint == "" {
		reg.HostEndpoint = fmt.Sprintf("%s:8888", clientIP(r))
	}
	reg.SessionSecret = secret
	reg.RegisteredAt  = time.Now()

	// Generate a unique code and store — all under a single lock to avoid TOCTOU races.
	hostsMutex.Lock()
	code, codeErr := generateUniqueCodeLocked()
	if codeErr == nil {
		reg.ConnectionCode = code
		hosts[reg.HostID]  = &reg
		hostsByCode[code]  = &reg
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
		// One-time use: delete immediately so the code cannot be reused.
		delete(hostsByCode, req.ConnectionCode)
		delete(hosts, host.HostID)
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

func clientIP(r *http.Request) string {
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		return strings.TrimSpace(strings.SplitN(xff, ",", 2)[0])
	}
	if idx := strings.LastIndex(r.RemoteAddr, ":"); idx >= 0 {
		return r.RemoteAddr[:idx]
	}
	return r.RemoteAddr
}

func cleanupExpiredHosts() {
	ticker := time.NewTicker(cleanupInterval)
	defer ticker.Stop()
	for range ticker.C {
		hostsMutex.Lock()
		now := time.Now()
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
