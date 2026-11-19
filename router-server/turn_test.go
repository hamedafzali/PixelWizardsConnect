package main

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"reflect"
	"strconv"
	"strings"
	"testing"
	"time"
)

const testTurnSecret = "test-secret-do-not-log"

var testTurnURLs = []string{
	"turn:turn.example.org:3478?transport=udp",
	"turns:turn.example.org:443?transport=tcp",
}

func withTurn(secret string, urls []string, ttl time.Duration) func() {
	return func() { turnSecret, turnURLs, turnTTL = secret, urls, ttl }
}

func TestTurnCredential_FixedVector(t *testing.T) {
	// Derived independently with:
	//   printf '%s' "1800003600:0123456789" | openssl dgst -sha1 -hmac "test-secret" -binary | base64
	const want = "rDD39/nicQ03sxTbbeJEErDos0U="
	if got := turnCredential("test-secret", "1800003600:0123456789"); got != want {
		t.Fatalf("credential = %q, want %q", got, want)
	}
}

func TestIssueTurnCredentials_UsernameAndExpiry(t *testing.T) {
	resetSignalState()
	t.Cleanup(resetSignalState)
	withTurn(testTurnSecret, testTurnURLs, 90*time.Minute)()
	now := time.Unix(1_800_000_000, 0)

	info := issueTurnCredentials(now)
	if info == nil {
		t.Fatal("expected credentials")
	}
	expiry, id, ok := strings.Cut(info.Username, ":")
	if !ok {
		t.Fatalf("username %q is not <expiry>:<id>", info.Username)
	}
	if n, err := strconv.ParseInt(expiry, 10, 64); err != nil || n != now.Add(90*time.Minute).Unix() {
		t.Fatalf("expiry %q, want %d", expiry, now.Add(90*time.Minute).Unix())
	}
	if _, err := hex.DecodeString(id); err != nil || id == "" {
		t.Fatalf("random id %q is not hex", id)
	}
	if info.Credential != turnCredential(testTurnSecret, info.Username) {
		t.Fatal("credential does not match the username")
	}
	if !reflect.DeepEqual(info.URLs, testTurnURLs) {
		t.Fatalf("urls = %v", info.URLs)
	}
}

func TestIssueTurnCredentials_FreshEachCall(t *testing.T) {
	resetSignalState()
	t.Cleanup(resetSignalState)
	withTurn(testTurnSecret, testTurnURLs, time.Hour)()
	now := time.Unix(1_800_000_000, 0)
	a, b := issueTurnCredentials(now), issueTurnCredentials(now)
	if a.Username == b.Username || a.Credential == b.Credential {
		t.Fatalf("two calls gave the same pair: %v %v", a, b)
	}
}

func TestIssueTurnCredentials_NilWhenNotConfigured(t *testing.T) {
	resetSignalState()
	t.Cleanup(resetSignalState)
	now := time.Now()
	for name, opt := range map[string]func(){
		"no secret": withTurn("", testTurnURLs, time.Hour),
		"no urls":   withTurn(testTurnSecret, nil, time.Hour),
	} {
		opt()
		if got := issueTurnCredentials(now); got != nil {
			t.Fatalf("%s: expected nil, got %v", name, got)
		}
	}
}

func TestParseTurnURLs(t *testing.T) {
	got, err := parseTurnURLs(" turn:a.example.org:3478 ,, turns:a.example.org:443?transport=tcp, stun:a.example.org:3478 ")
	want := []string{"turn:a.example.org:3478", "turns:a.example.org:443?transport=tcp", "stun:a.example.org:3478"}
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v, %v", got, err)
	}
	if got, err := parseTurnURLs(" , "); err != nil || len(got) != 0 {
		t.Fatalf("empty list: %v, %v", got, err)
	}
	for _, bad := range []string{"turn.example.org:3478", "http://turn.example.org", "turn:a.example.org,bogus"} {
		if _, err := parseTurnURLs(bad); err == nil {
			t.Errorf("%q should be rejected", bad)
		}
	}
}

func TestInitTurnConfig_ReadsEnvironment(t *testing.T) {
	t.Cleanup(resetSignalState)
	t.Setenv("TURN_URLS", "turn:turn.example.org:3478")
	t.Setenv("TURN_SECRET", "s")
	t.Setenv("TURN_CREDENTIAL_TTL", "10m")
	initTurnConfig()
	if !reflect.DeepEqual(turnURLs, []string{"turn:turn.example.org:3478"}) || turnSecret != "s" || turnTTL != 10*time.Minute {
		t.Fatalf("config = %v %q %v", turnURLs, turnSecret, turnTTL)
	}
	os.Unsetenv("TURN_CREDENTIAL_TTL")
	initTurnConfig()
	if turnTTL != time.Hour {
		t.Fatalf("default TTL = %v, want 1h", turnTTL)
	}
}

func TestSignal_PairingIssuesTurnToBothSides(t *testing.T) {
	fc := newFakeClock()
	url := startSignalServer(t, func() { nowFunc = fc.Now }, withTurn(testTurnSecret, testTurnURLs, time.Hour))

	host := dialSignal(t, url)
	code, _ := host.register(t)
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	joined := viewer.recvType(t, "joined")
	peerJoined := host.recvType(t, "peer-joined")

	var users, creds []string
	for _, m := range []map[string]any{joined, peerJoined} {
		turn, ok := m["turn"].(map[string]any)
		if !ok {
			t.Fatalf("missing turn: %v", m)
		}
		var urls []string
		for _, u := range turn["urls"].([]any) {
			urls = append(urls, u.(string))
		}
		if !reflect.DeepEqual(urls, testTurnURLs) {
			t.Fatalf("urls = %v", urls)
		}
		user, cred := turn["username"].(string), turn["credential"].(string)
		if !strings.HasPrefix(user, strconv.FormatInt(fc.Now().Add(time.Hour).Unix(), 10)+":") {
			t.Fatalf("username %q does not expire at now+TTL", user)
		}
		if cred != turnCredential(testTurnSecret, user) {
			t.Fatal("credential does not match username")
		}
		users, creds = append(users, user), append(creds, cred)
	}
	if users[0] == users[1] || creds[0] == creds[1] {
		t.Fatal("each side must get its own pair")
	}
}

func TestSignal_NoTurnWithoutSecret(t *testing.T) {
	url := startSignalServer(t, withTurn("", testTurnURLs, time.Hour))
	host := dialSignal(t, url)
	code, _ := host.register(t)
	viewer := dialSignal(t, url)
	viewer.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	for _, c := range []*sigClient{viewer, host} {
		raw, err := c.recvRawE()
		if err != nil {
			t.Fatal(err)
		}
		if strings.Contains(string(raw), `"turn"`) {
			t.Fatalf("turn must be absent: %s", raw)
		}
	}
}

func TestSignal_TurnSecretNeverLeaks(t *testing.T) {
	var logs bytes.Buffer
	log.SetOutput(&logs)
	t.Cleanup(func() { log.SetOutput(os.Stderr) })

	url := startSignalServer(t, withTurn(testTurnSecret, testTurnURLs, time.Hour))
	host, viewer, _ := pair(t, url) // pair() consumed joined/peer-joined; issue a second pair to inspect
	host.c.CloseNow()
	viewer.c.CloseNow()

	h := dialSignal(t, url)
	code, _ := h.register(t)
	v := dialSignal(t, url)
	v.send(t, fmt.Sprintf(`{"v":1,"type":"join","code":%q}`, code))
	var received [][]byte
	var secrets []string
	for _, c := range []*sigClient{v, h} {
		raw, err := c.recvRawE()
		if err != nil {
			t.Fatal(err)
		}
		received = append(received, raw)
		var m struct{ Turn signalTurn }
		if err := json.Unmarshal(raw, &m); err != nil || m.Turn.Username == "" {
			t.Fatalf("no turn in %s", raw)
		}
		secrets = append(secrets, m.Turn.Username, m.Turn.Credential)
	}
	h.send(t, `{"v":1,"type":"offer","sdp":"x"}`)
	received = append(received, mustRecvRaw(t, v))
	v.send(t, `{"v":1,"type":"bye"}`)
	received = append(received, mustRecvRaw(t, h)) // bye relayed
	h.c.CloseNow()
	v.c.CloseNow()
	waitFor(t, "rooms to clear", func() bool { e, _ := roomState(code); return !e })

	for _, raw := range received {
		if strings.Contains(string(raw), testTurnSecret) {
			t.Fatalf("secret reached a client: %s", raw)
		}
	}
	// Stop the handlers so every log line is written before it is inspected.
	signalWG.Wait()
	out := logs.String()
	for _, s := range append(secrets, testTurnSecret) {
		if strings.Contains(out, s) {
			t.Fatalf("log output contains a secret value:\n%s", out)
		}
	}
}

func mustRecvRaw(t *testing.T, c *sigClient) []byte {
	t.Helper()
	raw, err := c.recvRawE()
	if err != nil {
		t.Fatal(err)
	}
	return raw
}
