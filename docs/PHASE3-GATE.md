# Phase 3 exit gate (P3.8): record and runbook

The gate, from PHASE3-PLAN D1:

> Two peers on two different mobile carriers, signaled only through the deployed router, reach
> ICE `connected` and pass a data-channel round trip. This happens twice:
> (a) with the normal transport policy, recording the selected candidate pair type;
> (b) with `iceTransportPolicy = relay`, with the allocation confirmed in coturn's log.

Run (b) proves TURN works even when direct traversal would have succeeded.

This file is public. Record carriers, OS and pair types only. Do not write a user name, PC
name, domain or IP address of a personal device or of the real server here; use the placeholders
`router.example.org`, `turn.example.org` and `203.0.113.x`.

## Status

**Not yet run.**

## Runbook

### 1. Deploy the router and coturn

Details are in [router-server/README.md](../router-server/README.md#coturn).

1. DNS: `router.example.org` points at the router's HTTPS entry (reverse proxy or tunnel).
   `turn.example.org` is a DNS-only (not proxied) record pointing at the public IP, because
   proxies and tunnels do not carry UDP.
2. In `router-server/`, put these in `.env` next to `docker-compose.https.yml`:

   ```
   TURN_SECRET=<output of: openssl rand -hex 32>
   TURN_URLS=turn:turn.example.org:3478?transport=udp,turn:turn.example.org:3478?transport=tcp
   TURN_REALM=turn.example.org
   TURN_LISTEN_IP=<the server's own address: public IP on a VPS, LAN address behind NAT>
   TURN_EXTERNAL_IP=<203.0.113.10, or 203.0.113.10/<LAN address> behind NAT>
   ```

   coturn refuses to start if `TURN_SECRET`, `TURN_REALM`, `TURN_LISTEN_IP` or
   `TURN_EXTERNAL_IP` is empty. If any of `TURN_URLS`/`TURN_SECRET` is empty on the router, it
   issues no TURN credentials and run (b) cannot work.
3. Start both with the `turn` compose profile:

   ```bash
   docker compose -f docker-compose.https.yml --profile turn up -d
   ```

4. Forward or open these ports to `TURN_LISTEN_IP` (same port numbers):

   | Port | Protocol | Purpose |
   |---|---|---|
   | 3478 | UDP and TCP | TURN/STUN |
   | 49160-49200 | UDP | relay ports |

5. The router listens on port 9000 and must be reached over HTTPS (the probe uses `wss://` for
   an `https://` URL). Put it behind a reverse proxy or tunnel, and set `TRUSTED_PROXY_CIDRS` on
   the router to that proxy's address range so per-IP limits see the real client address. The
   compose file's default (`172.16.0.0/12`) covers a Caddy container on the compose network;
   change it for any other proxy or tunnel.
6. Check: `curl https://router.example.org/health` returns `"status":"healthy"`.

### 2. Probe commands

Use two devices on two different mobile carriers (for example two phones, each tethering a
laptop). From the repository root on each device, with the .NET 9 SDK installed:

Device A (host). It prints `CODE <code>` and waits for a peer:

```bash
dotnet run --project tools/PixelWizard.IceProbe -- --router https://router.example.org --host
```

Device B (viewer), using the code Device A printed:

```bash
dotnet run --project tools/PixelWizard.IceProbe -- --router https://router.example.org --join <code>
```

**(a) Direct attempt**: the commands above, normal transport policy (`POLICY all` in the output).

**(b) Relay only**: run again with a new code, adding `--relay-only` on both devices
(`POLICY relay`):

```bash
dotnet run --project tools/PixelWizard.IceProbe -- --router https://router.example.org --host --relay-only
dotnet run --project tools/PixelWizard.IceProbe -- --router https://router.example.org --join <code> --relay-only
```

Both sides must print `TURN issued`. `TURN none` means the router has no TURN settings, so
fix the deployment before continuing. `--timeout <seconds>` (default 60) and `--stun <url>` are
optional. Each run exits 0 only after `RESULT ok`. TURN credentials are never printed.

Keep (a) and (b) on the same pair of devices and carriers.

### 3. What to copy into the tables

From each device's output:

- the `LOCAL-CANDIDATE <type> ...` lines: note the types seen (host, srflx, relay) but do not
  copy addresses;
- `SELECTED-PAIR local=<type> remote=<type>`;
- `RTT <ms>` lines and the `rtt_avg=` value in the host's `RESULT ok` line;
- the `RESULT` line, with any `pair=` value.

From the server, right after run (b): `docker compose -f docker-compose.https.yml logs coturn`,
and copy the allocation line for that run. It looks like this, with the user being the
credential's expiry and a random id:

```
session ...: realm <turn.example.org> user <1795123541:f7c7702ade>: incoming packet ALLOCATE processed, success
```

A preceding `error 401` is the normal first, unauthenticated request. Replace the session id and
any client address with `...` before pasting, since client addresses are personal data.

If (a) selects `relay` by itself, record it as it is. That is information about the carrier's NAT
(CGNAT), not a failure. It does not replace (b), which is still run.

## Results

Not yet run. Fill these in when the run happens.

### Devices

| Device | Role in runs | OS | Carrier |
|---|---|---|---|
| A | host | | |
| B | viewer | | |

The carriers must differ.

### (a) Normal transport policy

| Date | Selected pair (local/remote) | Pair type (host, srflx, relay) | RTT avg (ms) | RESULT | Notes |
|---|---|---|---|---|---|
| | | | | | |

### (b) `--relay-only`

| Date | Selected pair (local/remote) | RTT avg (ms) | RESULT | coturn allocation log line |
|---|---|---|---|---|
| | | | | |

### Outcome

Not yet run. Gate passed: yes / no. When this is filled in, update the Phase 3 row of the
phase table in [ADR-001](ADR-001-architecture.md).
