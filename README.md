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
Browser (SignalR client)
        │
ASP.NET Core API ── SignalR hub (/hubs/game)
        │                  │
   PostgreSQL            Redis
   (players, matches,   (queue sorted set, lobby state
    MMR history,         with TTL, join timestamps)
    refresh tokens)
        │
Matchmaker BackgroundService (scans queue every 2s)
```

- **PostgreSQL** — everything that must survive: players, match history, MMR change log, refresh tokens.
- **Redis** — everything fast and ephemeral: matchmaking queue (sorted set keyed by MMR), lobby state (hash with TTL, so abandoned lobbies clean themselves up).
- **SignalR** — real-time events: `MatchFound`, `MatchStarted`, `RoundResult`, `MatchEnded`.
- **BackgroundService** — matchmaking runs isolated from API requests on a 2-second tick.

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

## Concurrency & Resilience (current state)

- **Idempotent queue join** — `ZADD NX`: a second join request (e.g. from a second device) is rejected.
- **Queue-leave race** — the matchmaker re-checks removal results; if a player left between scan and pairing, the other player is re-queued instead of being stranded.
- **Per-lobby serialization** — hub method invocations for the same lobby are serialized (in-process lock in MVP; will move to Redis distributed locks in v1.1 for multi-replica).
- **Server-side move validation** — the client is never trusted; moves and turn order are validated in the hub.
- **Self-cleaning state** — lobby keys carry a 10-minute TTL; abandoned lobbies disappear on their own.

Planned for v1.1: Redis `SETNX` distributed locks, disconnect/reconnect grace periods, 2 API replicas behind nginx with a SignalR Redis backplane, k6 load-test numbers.

## Tests

```bash
dotnet test
```

Unit tests cover the Elo calculator (K-factor behavior, underdog gains, zero-sum property), the expanding match window, and rock-paper-scissors resolution.

## Project Layout

```
src/Matchmaking.Api/
  Domain/     Elo, match window, RPS rules, entities (pure, unit-testable)
  Data/       EF Core DbContext (PostgreSQL)
  Auth/       JWT service, auth controller (access + refresh flow)
  Players/    profile & MMR history endpoints
  Queue/      Redis queue service, controller, matchmaker BackgroundService
  Lobby/      Redis lobby store, SignalR GameHub, match finalizer
  wwwroot/    minimal demo client
tests/Matchmaking.Tests/
```

## Roadmap

- **v1.1** — Redis distributed locks, reconnect flow, 2 replicas + nginx + SignalR backplane, k6 load tests, GitHub Actions + Testcontainers.
- **v2** — 2v2 party queue with team balancing, OpenTelemetry + Prometheus + Grafana, seasonal leaderboard, MMR decay.

See [PLAN.md](PLAN.md) for the full design document.
