//go:build coturn

package main

// Integration test: does a real coturn, running coturn/turnserver.conf, accept the credentials
// that issueTurnCredentials produces, and refuse what it should? It needs a running coturn
// container, so it is behind the "coturn" build tag and is only run by the `coturn` CI job:
//
//	TURN_TEST_CONTAINER=<container>  TURN_TEST_HOST=<coturn listening IP>  \
//	TURN_TEST_SECRET=<static-auth-secret>  go test -tags coturn -run TestCoturn -v ./...
//
// turnutils_uclient runs inside the container via `docker exec`; a turnutils_peer must be
// listening on TURN_TEST_HOST:3480 there, and coturn must have been started with
// --allowed-peer-ip=<TURN_TEST_HOST> (the CI host is itself in a denied RFC 1918 range).

import (
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"
)

func dockerOut(args ...string) (string, int) {
	out, err := exec.Command("docker", args...).CombinedOutput()
	code := 0
	if err != nil {
		code = -1
		if ee, ok := err.(*exec.ExitError); ok {
			code = ee.ExitCode()
		}
	}
	return string(out), code
}

func TestCoturnAcceptsRouterCredentials(t *testing.T) {
	container := os.Getenv("TURN_TEST_CONTAINER")
	host := os.Getenv("TURN_TEST_HOST")
	secret := os.Getenv("TURN_TEST_SECRET")
	if container == "" || host == "" || secret == "" {
		t.Fatal("TURN_TEST_CONTAINER, TURN_TEST_HOST and TURN_TEST_SECRET must be set")
	}

	// Credentials come from the router's own code, not a reimplementation.
	turnSecret = secret
	turnURLs = []string{"turn:" + host + ":3478?transport=udp"}
	turnTTL = time.Hour

	uclient := func(user, cred, peer string) (string, int) {
		return dockerOut("exec", container, "turnutils_uclient",
			"-u", user, "-w", cred, "-e", peer, "-n", "3", "-c", host)
	}
	logs := func() string {
		out, _ := dockerOut("logs", container)
		return out
	}

	t.Run("a_valid_credentials_allocate", func(t *testing.T) {
		c := issueTurnCredentials(time.Now())
		if c == nil {
			t.Fatal("router issued no credentials")
		}
		out, code := uclient(c.Username, c.Credential, host)
		log := logs()
		want := "user <" + c.Username + ">: incoming packet ALLOCATE processed, success"
		if code != 0 || !strings.Contains(log, want) {
			t.Fatalf("FAIL (a): uclient exit=%d, coturn allocation line present=%v\n%s",
				code, strings.Contains(log, want), out)
		}
		if strings.Contains(log, c.Credential) {
			t.Fatal("FAIL (a): coturn logged the credential")
		}
		t.Logf("PASS (a): router-issued credentials for %q allocated and relayed (coturn logged ALLOCATE success)", c.Username)
	})

	t.Run("b_expired_credentials_refused", func(t *testing.T) {
		// Expiry = now - 2*TTL + TTL = one hour in the past.
		c := issueTurnCredentials(time.Now().Add(-2 * turnTTL))
		out, code := uclient(c.Username, c.Credential, host)
		log := logs()
		if code == 0 || !strings.Contains(out, "Cannot complete Allocation") ||
			!strings.Contains(log, "Cannot find credentials of user <"+c.Username+">") ||
			strings.Contains(log, "user <"+c.Username+">: incoming packet ALLOCATE processed, success") {
			t.Fatalf("FAIL (b): uclient exit=%d\n%s", code, out)
		}
		t.Logf("PASS (b): expired router-issued credentials for %q refused (uclient exit=%d, coturn rejected the user)", c.Username, code)
	})

	t.Run("c_denied_peer_refused", func(t *testing.T) {
		c := issueTurnCredentials(time.Now())
		const peer = "10.255.255.254"
		out, code := uclient(c.Username, c.Credential, peer)
		log := logs()
		wantLog := "A peer IP " + peer + " denied in the range: 10.0.0.0-10.255.255.255"
		if code == 0 || !strings.Contains(out, "403 (Forbidden IP)") || !strings.Contains(log, wantLog) {
			t.Fatalf("FAIL (c): uclient exit=%d, coturn denial line present=%v\n%s",
				code, strings.Contains(log, wantLog), out)
		}
		t.Logf("PASS (c): relay to %s refused (403 Forbidden IP; coturn: denied in 10.0.0.0/8)", peer)
	})
}
