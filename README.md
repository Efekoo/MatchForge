# Real-Time Matchmaking & Lobby Service

A standalone, scalable backend service solving the three core problems behind competitive online games: **authentication**, **skill-based matchmaking**, and **real-time session management**. No game engine required — the mini-game (rock-paper-scissors, best of 3) is intentionally trivial because the point is the infrastructure, not the game logic.

**Stack:** C# / ASP.NET Core 8 · PostgreSQL · Redis · SignalR · JWT · Docker Compose

## Quick Start

```bash
docker compose up --build
```

Then open **http://localhost:8080** in two different browsers (or one normal + one incognito window), register two players, and hit *Find Match* in both.

Swagger UI: http://localhost:8080/swagger · Health: http://localhost:8080/health

## The Loop

```
Register/Login → Join queue → Match found → Lobby → Play match
      ↑                                                  │
      └── MMR updated (Elo) ← Result persisted ←─────────┘
```

The loop closes end-to-end: match results are written to PostgreSQL and ratings update via a hand-rolled Elo implementation — not just a "match found" screen.

## Architecture

```
Browser (SignalR client, WebSocket-only)
        │
      nginx (round-robin load balancer)
        │
ASP.NET Core API ×2 replicas ── SignalR hub (/hubs/game)
        │                            │
   PostgreSQL                      Redis
   (players, matches,   (queue sorted set, lobby state with TTL,
    MMR history,         SignalR backplane, distributed locks)
    refresh tokens)
        │
Matchmaker + LobbyReaper BackgroundServices
(run on every replica, serialized via Redis locks)
```

- **PostgreSQL** — everything that must survive: players, match history, MMR change log, refresh tokens.
- **Redis** — everything fast and ephemeral: matchmaking queue (sorted set keyed by MMR), lobby state (hash with TTL, so abandoned lobbies clean themselves up), distributed locks, SignalR backplane.
- **SignalR** — real-time events: `MatchFound`, `MatchStarted`, `RoundResult`, `MatchEnded`, `OpponentDisconnected`, `OpponentReconnected`, `MatchCancelled`. The **Redis backplane** ensures messages flow even when two players in the same lobby are connected to different replicas.
- **BackgroundServices** — matchmaking (2 s tick) and lobby reaping (5 s tick) run on every replica but are serialized with Redis locks, so scaling out doesn't break correctness.

## Matchmaking: Expanding MMR Window

The core trade-off of every competitive matchmaker: **match quality vs. wait time.**

| Wait time | Acceptable MMR gap |
|---|---|
| 0–10 s | ±50 |
| each +10 s | +50 |
| cap | ±400 |

Two players match only when the gap fits **both** players' windows. Implemented in `Domain/MatchWindow.cs`, scanned by `Queue/MatchmakerService.cs`.

## Rating: Hand-Rolled Elo

`Domain/EloCalculator.cs` — no library:

- Expected score: `E = 1 / (1 + 10^((Rb − Ra) / 400))`
- Update: `Ra' = Ra + K × (S − E)`
- **K-factor:** 40 for the first 20 games (fast convergence for unknown players), 20 after (stability for established ratings).

Every MMR change is stored as a separate `mmr_history` row for auditability.

## API Surface

```
POST   /auth/register        POST   /auth/login
POST   /auth/refresh         POST   /auth/logout
GET    /players/me           GET    /players/me/history
POST   /queue/join           DELETE /queue/leave
GET    /queue/status
GET    /lobbies/{id}         GET    /lobbies/mine
GET    /health
WS     /hubs/game            (SignalR)
```

Auth uses **access + refresh tokens** with rotation; only SHA-256 hashes of refresh tokens are stored. Passwords hashed with bcrypt. SignalR connections authenticate via JWT (`access_token` query param, as WebSockets can't carry headers).

## Concurrency & Resilience

**Race conditions:**

- **Can the same player be assigned to two matches?** No. Every pairing acquires a per-player distributed lock (`mm:lock:{playerId}`, `SETNX` + TTL); a matchmaking attempt that can't get the lock skips that player. Lock release is a compare-and-delete Lua script, so an expired lock taken over by someone else is never deleted by mistake.
- **What if two replicas run the matchmaker simultaneously?** The matchmaking round itself is serialized with `mm:matchmaker:lock` — every replica runs the service, but only one executes a round at a time. Horizontal scaling stays intact.
- **Queueing from two devices at once?** Queue join is idempotent (`ZADD NX`); the second request is rejected.
- **Player leaves between scan and pairing?** The matchmaker checks removal results; the stranded player is re-queued.
- **Concurrent moves in the same lobby?** Hub invocations for a lobby are serialized with a Redis lock (`lock:lobby:{id}`) — replica-independent. The reaper takes the same lock before closing a lobby, so a match can't be forfeited mid-move.
- **Server-side move validation** — the client is never trusted; moves and turn order are validated in the hub.

**Disconnect / reconnect** (connection id is ephemeral, player identity comes from the JWT):

- **In queue:** a disconnect sets a grace marker; if the player isn't back within **15 s**, the matchmaker sweep drops them from the queue. Reconnecting in time clears the marker — no requeue needed.
- **In match:** the lobby stays open for **30 s**. The opponent sees `OpponentDisconnected`; on return (new connection id, same JWT identity) the player rejoins via `JoinLobby`, receives the current match state, and the opponent sees `OpponentReconnected`. If the grace period expires, the `LobbyReaper` awards a **win by forfeit** to the connected player — with full Elo/MMR consequences.
- **Self-cleaning state** — lobby keys carry a 10-minute TTL as a last-resort backstop.

Planned for v1.1 completion: k6 load-test numbers, GitHub Actions + Testcontainers integration tests.

## Tests

```bash
dotnet test
```

Unit tests cover the Elo calculator (K-factor behavior, underdog gains, zero-sum property), the expanding match window, and rock-paper-scissors resolution.

## Project Layout

```
src/Matchmaking.Api/
  Domain/         Elo, match window, RPS rules, entities (pure, unit-testable)
  Data/           EF Core DbContext (PostgreSQL)
  Infrastructure/ Redis distributed lock service (SETNX + Lua release)
  Auth/           JWT service, auth controller (access + refresh flow)
  Players/        profile & MMR history endpoints
  Queue/          Redis queue service, controller, matchmaker BackgroundService
  Lobby/          Redis lobby store, SignalR GameHub, match finalizer, lobby reaper
  wwwroot/        minimal demo client
tests/Matchmaking.Tests/
nginx.conf        WebSocket-aware reverse proxy for the 2 API replicas
```

## Roadmap

- **v1.1** — ~~Redis distributed locks~~, ~~reconnect flow~~, ~~2 replicas + nginx + SignalR backplane~~ (done) · k6 load tests, GitHub Actions + Testcontainers (remaining).
- **v2** — 2v2 party queue with team balancing, OpenTelemetry + Prometheus + Grafana, seasonal leaderboard, MMR decay.

See [PLAN.md](PLAN.md) for the full design document.
