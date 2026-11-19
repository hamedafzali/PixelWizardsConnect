package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"log"
	"strconv"
	"strings"
	"time"
)

// TURN credentials follow coturn's REST-API scheme (`use-auth-secret`): the username is
// "<expiry-unix-seconds>:<random-id>" and the credential is base64(HMAC-SHA1(secret, username)).
// coturn recomputes the HMAC from the same shared secret, so the router keeps no per-credential
// state. The secret, usernames and credentials are never logged.

var (
	turnURLs   []string
	turnSecret string
	turnTTL    time.Duration
)

// initTurnConfig loads the TURN settings. A bad TURN_URLS is fatal, like TRUSTED_PROXY_CIDRS: a
// typo that silently dropped TURN would only show up as failed calls behind strict NATs.
func initTurnConfig() {
	urls, err := parseTurnURLs(getEnv("TURN_URLS", ""))
	if err != nil {
		log.Fatalf("invalid TURN_URLS: %v", err)
	}
	turnURLs = urls
	turnSecret = getEnv("TURN_SECRET", "")
	turnTTL = getDurationEnv("TURN_CREDENTIAL_TTL", time.Hour)
}

// parseTurnURLs parses a comma-separated list of ICE server URLs. Entries are trimmed and empty
// ones dropped; each must use the turn:, turns: or stun: scheme.
func parseTurnURLs(raw string) ([]string, error) {
	var urls []string
	for _, entry := range strings.Split(raw, ",") {
		entry = strings.TrimSpace(entry)
		if entry == "" {
			continue
		}
		if !strings.HasPrefix(entry, "turn:") && !strings.HasPrefix(entry, "turns:") &&
			!strings.HasPrefix(entry, "stun:") {
			return nil, fmt.Errorf("%q must start with turn:, turns: or stun:", entry)
		}
		urls = append(urls, entry)
	}
	return urls, nil
}

// turnCredential is base64(HMAC-SHA1(secret, username)).
func turnCredential(secret, username string) string {
	mac := hmac.New(sha1.New, []byte(secret))
	mac.Write([]byte(username))
	return base64.StdEncoding.EncodeToString(mac.Sum(nil))
}

// issueTurnCredentials returns a fresh pair valid until now+turnTTL, or nil when TURN is not
// configured (no secret, or no URLs): peers then get STUN and host candidates only. There is no
// static fallback.
func issueTurnCredentials(now time.Time) *signalTurn {
	if turnSecret == "" || len(turnURLs) == 0 {
		return nil
	}
	id := make([]byte, 5)
	if _, err := rand.Read(id); err != nil {
		return nil
	}
	username := strconv.FormatInt(now.Add(turnTTL).Unix(), 10) + ":" + hex.EncodeToString(id)
	return &signalTurn{
		URLs:       append([]string(nil), turnURLs...),
		Username:   username,
		Credential: turnCredential(turnSecret, username),
	}
}
