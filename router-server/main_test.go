package main

import (
	"encoding/hex"
	"net/http"
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
