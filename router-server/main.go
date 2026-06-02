package main

import (
	"encoding/json"
	"fmt"
	"log"
	"math/rand"
	"net/http"
	"sync"
	"time"
)

type HostRegistration struct {
	HostID        string `json:"hostId"`
	HostName      string `json:"hostName"`
	HostEndpoint  string `json:"hostEndpoint"`
	ConnectionCode string `json:"connectionCode"`
	RegisteredAt  time.Time `json:"registeredAt"`
}

type ConnectionRequest struct {
	ConnectionCode string `json:"connectionCode"`
	ClientID       string `json:"clientId"`
}

type RegistrationResponse struct {
	ConnectionCode string `json:"connectionCode"`
	HostID         string `json:"hostId"`
}

type ConnectionResponse struct {
	HostEndpoint string `json:"hostEndpoint"`
	Success      bool   `json:"success"`
	Message      string `json:"message"`
}

var (
	hosts         = make(map[string]*HostRegistration)
	hostsByCode   = make(map[string]*HostRegistration)
	hostsMutex    sync.RWMutex
	codeChars     = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	codeLength    = 6
	hostCleanup   = 30 * time.Minute
)

func main() {
	// Start cleanup goroutine
	go cleanupExpiredHosts()

	// Setup HTTP routes
	http.HandleFunc("/register", handleRegister)
	http.HandleFunc("/connect", handleConnect)
	http.HandleFunc("/health", handleHealth)

	port := "9000"
	fmt.Printf("Router Server starting on port %s...\n", port)
	fmt.Printf("Endpoints:\n")
	fmt.Printf("  POST /register - Register a host and get connection code\n")
	fmt.Printf("  POST /connect - Connect to a host using connection code\n")
	fmt.Printf("  GET /health - Health check\n")

	if err := http.ListenAndServe(":"+port, nil); err != nil {
		log.Fatal("Server failed to start:", err)
	}
}

func handleRegister(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var reg HostRegistration
	if err := json.NewDecoder(r.Body).Decode(&reg); err != nil {
		http.Error(w, "Invalid request body", http.StatusBadRequest)
		return
	}

	// Generate unique connection code
	var code string
	var exists bool
	
	hostsMutex.Lock()
	for i := 0; i < 10; i++ {
		code = generateCode()
		_, exists = hostsByCode[code]
		if !exists {
			break
		}
	}
	hostsMutex.Unlock()

	if exists {
		http.Error(w, "Failed to generate unique code", http.StatusInternalServerError)
		return
	}

	// Store host registration
	reg.ConnectionCode = code
	reg.RegisteredAt = time.Now()
	
	// Use provided endpoint or default
	if reg.HostEndpoint == "" {
		reg.HostEndpoint = fmt.Sprintf("%s:8888", getRemoteIP(r))
	}

	hostsMutex.Lock()
	hosts[reg.HostID] = &reg
	hostsByCode[code] = &reg
	hostsMutex.Unlock()

	response := RegistrationResponse{
		ConnectionCode: code,
		HostID:         reg.HostID,
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
	
	log.Printf("Host registered: ID=%s, Code=%s, Endpoint=%s", reg.HostID, code, reg.HostEndpoint)
}

func handleConnect(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var req ConnectionRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "Invalid request body", http.StatusBadRequest)
		return
	}

	hostsMutex.RLock()
	host, exists := hostsByCode[req.ConnectionCode]
	hostsMutex.RUnlock()

	if !exists {
		response := ConnectionResponse{
			Success: false,
			Message: "Invalid connection code",
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusNotFound)
		json.NewEncoder(w).Encode(response)
		return
	}

	response := ConnectionResponse{
		HostEndpoint: host.HostEndpoint,
		Success:      true,
		Message:      "Connected",
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
	
	log.Printf("Client connected: Code=%s, Endpoint=%s", req.ConnectionCode, host.HostEndpoint)
}

func handleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"status":  "healthy",
		"hosts":   len(hosts),
		"uptime":  time.Since(startTime).String(),
	})
}

var startTime = time.Now()

func generateCode() string {
	rand.Seed(time.Now().UnixNano())
	code := make([]byte, codeLength)
	for i := range code {
		code[i] = codeChars[rand.Intn(len(codeChars))]
	}
	return string(code)
}

func getRemoteIP(r *http.Request) string {
	// Check X-Forwarded-For header first (for proxies)
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		return xff
	}
	
	// Fall back to RemoteAddr
	return r.RemoteAddr
}

func cleanupExpiredHosts() {
	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()

	for range ticker.C {
		hostsMutex.Lock()
		now := time.Now()
		for id, host := range hosts {
			if now.Sub(host.RegisteredAt) > hostCleanup {
				delete(hosts, id)
				delete(hostsByCode, host.ConnectionCode)
				log.Printf("Cleaned up expired host: ID=%s", id)
			}
		}
		hostsMutex.Unlock()
	}
}
