# Forge

[![CI](https://github.com/Waris10/forge/actions/workflows/ci.yml/badge.svg)](https://github.com/Waris10/forge/actions/workflows/ci.yml)

Forge is a distributed background job queue built on .NET 10, Postgres, and Redis. It's the kind of system that sits behind a web app to run work asynchronously — send an email, resize an image, call a flaky third-party API — with at-least-once delivery, automatic retries with backoff, dead-letter handling, dead-worker recovery, and a live operations dashboard.

It's split into small single-purpose services rather than one monolith, so each concern (accepting jobs, running jobs, scheduling delayed jobs, recovering from crashes, observing the system) can be reasoned about, scaled, and restarted independently.

## What makes this interesting

- **Atomic Redis operations in Lua** for scheduled-job promotion and dead-worker recovery — no client-side races between check and write.
- **Distributed leader election** via a Redis lock with correct release semantics (check-and-DEL, not just DEL), so scheduler and janitor instances can be run redundantly.
- **W3C trace propagation across the queue boundary** — the `traceparent` is stamped onto the job's Redis hash at enqueue and rehydrated at pickup, so a single Jaeger trace spans HTTP API submit → Redis wait → worker execute.
- **Two independent retry counters** — application-level `attempts` (per-job retry budget) and system-level `requeue_count` (poison-pill protection against handlers that crash the worker process itself).
- **Hybrid broadcaster pattern** in the dashboard: one shared poller per resource fans out to all connected browser tabs via SignalR, so N viewers cost the same in Postgres/Redis as one.
- **Per-queue worker pools** via env var overrides — each worker process listens on one named queue, scales independently, and attaches to any process supervisor. Adding a new queue requires no code changes, just a new worker pointed at it.

## Architecture

```
                 ┌─────────────┐
   HTTP POST     │  Forge.Api  │──────────────┐
  ──────────────▶│             │              │
                 └─────────────┘              │
                        │                      ▼
                        │              ┌───────────────┐
                        │ INSERT       │   Postgres     │◀──── source of truth
                        ▼              │   (jobs table) │      for job records
                 ┌─────────────┐       └───────────────┘
                 │   Redis     │              ▲
                 │             │              │
      ready queue│ scheduled zset │ dlq       │ status updates
                 └─────────────┘              │
                   │        ▲                 │
        BLMOVE     │        │ LPUSH           │
                    ▼        │ (promote)       │
            ┌───────────────┐│         ┌───────────────┐
            │ Forge.Worker  ││         │ Forge.Scheduler│
            │ (puller +     │◀────────│ (promotes due  │
            │  executors)   │         │  delayed jobs) │
            └───────────────┘         └───────────────┘
                    │
                    │ heartbeat / processing list
                    ▼
            ┌───────────────┐         ┌───────────────┐
            │ Forge.Janitor │         │Forge.Dashboard│
            │ (recovers     │         │ (live Blazor  │
            │  dead workers)│         │  ops UI)      │
            └───────────────┘         └───────────────┘
```

Postgres is the durable record of every job and its history. Redis is the live queue: a ready list per queue name, a sorted set for delayed/scheduled jobs, a per-worker processing list (for reliable, at-least-once delivery via `BLMOVE`), and a dead-letter list. Every write to Redis is paired with a Postgres row, so the dashboard and API can always answer "what happened to job X" even after it leaves the queue.

## In action

<p align="center">
  <img src="docs/screenshots/jaeger-unified-trace.png" alt="Single Jaeger trace spanning API submit → worker execute" width="800" />
  <br />
  <em>One job, one trace, from HTTP request through Redis handoff to handler completion — enabled by W3C traceparent propagation across the queue boundary.</em>
</p>

<p align="center">
  <img src="docs/screenshots/dashboard-jobs.png" alt="Live jobs list in the dashboard" width="800" />
  <br />
  <em>The dashboard's live jobs list — status transitions pushed to every open tab via SignalR, no client polling.</em>
</p>

<p align="center">
  <img src="docs/screenshots/dashboard-dlq.png" alt="Dead-letter queue with bulk retry" width="800" />
  <br />
  <em>Dead-letter queue with per-job and bulk retry, confirmation modal, and per-job Jaeger deep-links from the detail page.</em>
</p>

<p align="center">
  <img src="docs/screenshots/grafana-dashboard.png" alt="Grafana dashboard showing throughput, queue depth, DLQ, and worker count" width="800" />
  <br />
  <em>The pre-provisioned Grafana dashboard: submission rate, completion by status, p50/p95/p99 duration, queue depth, DLQ depth, workers alive.</em>
</p>

## Components

| Project                                  | Type                | Responsibility                                                                                                                                                                                                                              |
| ---------------------------------------- | ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [`Forge.Api`](src/Forge.Api)             | ASP.NET minimal API | Accepts job submissions (`POST /jobs`), exposes job lookup and retry endpoints. Writes to Postgres, then enqueues to Redis.                                                                                                                 |
| [`Forge.Worker`](src/Forge.Worker)       | Worker service      | Pulls jobs from Redis (`PullerService`), runs the registered handler (`ExecutorService`), and reports success/failure/retry/dead back to Postgres and Redis. Sends heartbeats so the janitor can detect crashes.                            |
| [`Forge.Scheduler`](src/Forge.Scheduler) | Worker service      | Singleton (leader-elected via a Redis lock) background loop that promotes delayed jobs from the scheduled sorted set back onto their ready queue once due, and publishes queue-depth gauges.                                                |
| [`Forge.Janitor`](src/Forge.Janitor)     | Worker service      | Singleton background loop that scans for workers whose heartbeat has expired while jobs were still in their processing list, and recovers those jobs — requeues them, or sends them to the DLQ as poison pills once a requeue limit is hit. |
| [`Forge.Dashboard`](src/Forge.Dashboard) | Blazor Server app   | Live operational view: overview gauges, job list with status filters, job detail with Jaeger deep-links, DLQ with one-click/bulk retry, active worker health — all pushed to the browser over a SignalR circuit.                            |
| [`Forge.Storage`](src/Forge.Storage)     | Class library       | All persistence: the Postgres job repository (Dapper) and the Redis job queue / read-store / distributed lock (StackExchange.Redis). Every other service depends on this instead of touching Postgres or Redis directly.                    |
| [`Forge.Core`](src/Forge.Core)           | Class library       | Shared domain types with no infrastructure dependencies: `Job`, `JobStatus`, `RetryPolicy` (backoff math), plus cross-cutting `LoggingSetup`, `TracingSetup`, and centrally-defined `Metrics`.                                              |

## Job lifecycle

```
queued ──▶ running ──▶ succeeded          (terminal)
              │
              ├──▶ failed ──▶ (backoff delay) ──▶ queued   (retry loop)
              │
              └──▶ dead                            (terminal, attempts exhausted)
```

- **Submission**: `POST /jobs` inserts a `queued` row in Postgres, then either `LPUSH`es the job id onto its Redis ready queue, or — if `delaySeconds` was given — adds it to the `forge:scheduled` sorted set instead.
- **Delayed promotion**: `Forge.Scheduler` periodically pops due entries off `forge:scheduled` and pushes them onto the ready queue.
- **Pickup**: a worker's puller does a blocking `BLMOVE` from the ready queue into its own `forge:processing:{workerId}` list — this is what makes delivery reliable: if the worker dies mid-job, the job is still visible somewhere.
- **Execution**: the executor loads the full job from Postgres, resolves a handler by `jobType`, and calls it.
  - Success → row marked `succeeded`, job acked (removed from the processing list).
  - Failure with attempts remaining → row marked `failed`, job rescheduled onto `forge:scheduled` with an exponential-backoff-plus-jitter delay ([`RetryPolicy`](src/Forge.Core/RetryPolicy.cs): 2s, 4s, 8s… capped at 5 minutes).
  - Failure with attempts exhausted → row marked `dead`, job moved to the DLQ (`forge:dlq`).
- **Crash recovery**: `Forge.Janitor` watches for processing lists whose owning worker's heartbeat key has expired (worker crashed or was killed) and either requeues those jobs or, past a requeue-count threshold, sends them to the DLQ as poison pills.
- **Manual recovery**: `POST /jobs/{id}/retry` or `POST /dlq/retry-all` (single or bulk) resets a `failed`/`dead` job back to `queued`, from the API or the Dashboard's DLQ page.

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (for Postgres/Redis/observability stack).

```bash
# 1. Start infrastructure: Postgres, Redis, Prometheus, Grafana, Jaeger
docker compose up -d

# 2. Apply the schema — either against a local psql...
psql "postgresql://forge:forge@localhost:5432/forge" -f src/Forge.Storage/Postgres/Migrations/001_init.sql

#    ...or via the running Postgres container (no local psql needed)
docker exec -i forge-postgres psql -U forge -d forge < src/Forge.Storage/Postgres/Migrations/001_init.sql

# 3. Run each service in its own terminal
dotnet run --project src/Forge.Api          # http://localhost:5171
dotnet run --project src/Forge.Worker
dotnet run --project src/Forge.Scheduler
dotnet run --project src/Forge.Janitor
dotnet run --project src/Forge.Dashboard    # http://localhost:5200

# 4. Submit a job
curl -X POST http://localhost:5171/jobs \
  -H "Content-Type: application/json" \
  -d '{"jobType":"NoOp","payload":{}}'

# 5. Or seed a realistic mix (succeeded/retried/dead/scheduled) for the dashboard
FORGE_API=http://localhost:5171 bash scripts/seed.sh
```

Open the dashboard at `http://localhost:5200` — the overview page shows the top-line gauges, with `/jobs`, `/dlq`, and `/workers` in the nav.

**Running multiple workers:** each worker is configured by three env vars. Start as many as you need, each on a different port and with a different ID:

```bash
# Default queue — two workers
dotnet run --project src/Forge.Worker --no-build
Worker__MetricsPort=9102 Worker__WorkerId=worker-2 dotnet run --project src/Forge.Worker --no-build

# Dedicated pool for a named queue (e.g. email)
Worker__Queue=email Worker__WorkerId=worker-email Worker__MetricsPort=9111 dotnet run --project src/Forge.Worker --no-build
```

Multiple `Forge.Scheduler` and `Forge.Janitor` instances are also safe — the Redis distributed lock ensures only one acts as leader at a time.

## Configuration

Each service reads `ConnectionStrings:Postgres` and `ConnectionStrings:Redis` from its `appsettings.json` (defaults point at the `docker-compose.yml` containers on localhost). Service-specific tuning lives under its own config section, e.g. `Forge.Worker`'s `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=forge;Username=forge;Password=forge",
    "Redis": "localhost:6379"
  },
  "Worker": {
    "Queue": "default",
    "PullTimeout": "00:00:05",
    "MetricsPort": 9101
  }
}
```

Notable options (see the `*Options.cs` file in each service): `WorkerOptions.ExecutorCount` (parallel handler slots per worker process, defaults to CPU count), `WorkerOptions.HeartbeatTtl` (30s — how long before the janitor considers a worker dead), `SchedulerOptions`/`JanitorOptions` lock TTLs and tick intervals.

## API reference (`Forge.Api`)

| Endpoint                | Description                                                                                                                                                                                                                                                    |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /jobs`            | Submit a job. Body: `{ jobType, payload, queue?, priority?, maxAttempts?, delaySeconds?, idempotencyKey? }`. Returns `202` with `{ id, status }`. Submitting with a previously-used `idempotencyKey` returns the existing job instead of creating a duplicate. |
| `GET /jobs/{id}`        | Fetch a job's current state from Postgres.                                                                                                                                                                                                                     |
| `POST /jobs/{id}/retry` | Reset a single `failed`/`dead` job back to `queued` and re-enqueue it. `409` if the job isn't in a retryable state.                                                                                                                                            |
| `POST /dlq/retry-all`   | Bulk retry. Body `{ ids?: [] }` — an explicit id list, or omit/empty for every dead job in the database. Returns per-job success/failure counts.                                                                                                               |
| `GET /healthz`          | Liveness check.                                                                                                                                                                                                                                                |
| `GET /metrics`          | Prometheus scrape endpoint.                                                                                                                                                                                                                                    |

## Dashboard (`Forge.Dashboard`)

A Blazor Server app using interactive server render mode — state lives server-side and pushes to the browser over SignalR, so every open tab sees updates in real time with no client polling.

- **Overview** (`/`) — throughput chart, queue depth / DLQ / workers gauges, and a recent failures list.
- **Jobs** (`/jobs`) — live-updating table of recent jobs, filterable by status; click through to job detail with per-job Jaeger deep-link.
- **DLQ** (`/dlq`) — dead jobs with their last error, retry a single job or bulk-retry (visible page vs. every dead job in the DB), with a confirmation modal before bulk actions.
- **Workers** (`/workers`) — active workers, last heartbeat, time until heartbeat expiry, in-flight job count, and a health badge.

Each page is backed by a dedicated `*Broadcaster` hosted service (in [`Live/`](src/Forge.Dashboard/Live)) that polls Redis/Postgres on an interval and fans out snapshots to subscribed components — a hybrid push model rather than every browser tab hitting the database independently.

## Observability

- **Metrics** — every service exposes `/metrics` for Prometheus (`prometheus-net`). Centrally defined in [`Forge.Core/Metrics.cs`](src/Forge.Core/Metrics.cs): `forge_jobs_submitted_total`, `forge_jobs_completed_total{status}`, `forge_job_duration_seconds`, `forge_queue_depth`, `forge_dlq_depth`, `forge_workers_alive`, `forge_jobs_recovered_total{outcome}`.
- **Dashboards** — a pre-provisioned Grafana dashboard ([`deploy/grafana`](deploy/grafana)) and Prometheus scrape config ([`deploy/prometheus.yml`](deploy/prometheus.yml)) come up with `docker compose up`, at `http://localhost:3001` (admin/admin) and `http://localhost:9090`.
- **Tracing** — `Forge.Api` and `Forge.Worker` emit OpenTelemetry traces via OTLP to Jaeger (`http://localhost:16686`). The API's submit span and the worker's execute span are linked into a single trace by propagating the W3C `traceparent` through the job's Redis hash — you can follow one job from HTTP request to handler completion.
- **Logs** — structured JSON logging via Serilog ([`Forge.Core/LoggingSetup.cs`](src/Forge.Core/LoggingSetup.cs)), with per-job context (`JobId`, `JobType`, `Attempts`) pushed onto every log line for the duration of execution.

## Test job handlers

`Forge.Worker` ships three handlers ([`Handlers/`](src/Forge.Worker/Handlers)) purely for exercising the system end-to-end — submit jobs of these types to watch retries, backoff, and the DLQ in action without writing real work:

| Job type | Behavior                                                                                                                                         |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `NoOp`   | Succeeds immediately.                                                                                                                            |
| `Slow`   | Sleeps ~60s before succeeding — useful for watching the `running` state and worker concurrency.                                                  |
| `Flaky`  | Fails with configurable probability (`payload.successRate`, default 0.3) — exercises retry/backoff and eventual DLQ once attempts are exhausted. |

`scripts/seed.sh` submits a realistic mix of all three, plus delayed and unknown-type jobs, against a running API.

## Benchmarks

Measured on a Windows dev machine (WSL2 + Docker Desktop, .NET 10 Release build, Postgres and Redis running in Docker containers).

| Scenario        | Workers            | Jobs   | Throughput   | Notes                              |
| --------------- | ------------------ | ------ | ------------ | ---------------------------------- |
| NoOp handlers   | 1 (real process)   | 10,000 | ~11 jobs/sec | Dev machine, Docker on WSL2        |
| NoOp handlers   | 2 (real processes) | 10,000 | ~17 jobs/sec | 1.5x scaling — see bottleneck note |
| Chaos test (CI) | 3 (simulated)      | 1,000  | All terminal | Succeeded: 1000, Dead: 0           |

**Bottleneck: Postgres write latency.** Each job requires 3 Postgres round trips — INSERT on submit, UPDATE on pickup (`MarkRunning`), UPDATE on completion (`MarkSucceeded`). Redis queue operations (`BLMOVE`, `LREM`, `HSET`) are sub-millisecond and are not the constraint. The 2-worker result shows ~1.5x throughput rather than 2x because both workers share the same Postgres instance; on a server with Postgres co-located with the workers, throughput would be significantly higher.

**Chaos resilience:** 1,000 jobs submitted, 3 workers running, 1 killed mid-run without acking in-flight jobs. The janitor recovered all orphaned jobs. Final state: Succeeded: 1,000, Dead: 0. This test runs automatically in CI on every push.

## Design notes

- **Postgres is the source of truth; Redis is the live queue.** Every job write is Postgres-first, then Redis. If Redis is unavailable the job exists in Postgres but nothing sees it on the queue side — a background reconciler could sweep for such orphans, but it's a known corner rather than something the current build handles.
- **At-least-once, not exactly-once.** The `BLMOVE`-into-processing-list pattern combined with janitor recovery means a crashed worker's in-flight job will run again on another worker. Handlers must be idempotent.
- **Manual retry preserves the row, not the audit trail.** `POST /jobs/{id}/retry` resets the existing row's status to `queued` and bumps `max_attempts` by one — same identity, same audit position, but the previous trace/last_error is overwritten by the retried execution. This matches Laravel's `queue:retry` semantics; the tradeoff is that pre-retry state isn't preserved without external logging.
- **Per-queue worker pools** are supported today via env var overrides (`Worker__Queue=email`, `Worker__WorkerId=worker-email-1`, `Worker__MetricsPort=9111`) — each process listens on one queue. Different pools can run different handler sets, scale independently, and attach to any process supervisor. The routing is handled by Redis lists (`forge:queue:{name}`), so adding a new queue requires no code changes — just start a worker pointed at it.

## Tech stack

.NET 10 / C# · ASP.NET Core minimal APIs · Blazor Server (Interactive Server render mode) · Postgres 16 + Dapper · Redis 7 (StackExchange.Redis, `BLMOVE` reliable queue pattern, Lua-based atomic promotion) · Prometheus + Grafana · OpenTelemetry + Jaeger · Serilog

## Project layout

```
src/
  Forge.Api/         HTTP entry point for job submission
  Forge.Core/        Shared domain model, metrics, logging/tracing setup
  Forge.Storage/     Postgres repository + Redis queue/read-store/lock
  Forge.Worker/      Job execution (puller + executors + heartbeat)
  Forge.Scheduler/   Delayed-job promotion (singleton, Redis-locked)
  Forge.Janitor/     Dead-worker recovery (singleton, Redis-locked)
  Forge.Dashboard/   Blazor Server live operations UI
deploy/              Prometheus + Grafana provisioning
scripts/             seed.sh + benchmark.sh
docs/screenshots/    Images referenced from this README
tests/               xUnit unit + integration tests (Testcontainers)
```

## Roadmap

Built milestone by milestone, each a working end-to-end increment:

1. Solution skeleton + Postgres-backed job submission
2. Redis queue + end-to-end worker execution
3. Retries + DLQ (exponential backoff with jitter)
4. Standalone scheduler (Lua promotion + singleton lock)
5. Janitor and reliability (heartbeats, dead-worker recovery, poison pills)
6. Observability (structured logs, Prometheus metrics, Grafana, distributed tracing)
7. Live dashboard (Blazor Server, hybrid broadcaster pattern, DLQ bulk retry, worker health)
8. Ship — integration tests (Testcontainers), CI (GitHub Actions), benchmarks, multi-worker scaling

Ahead: a proper migration runner, per-queue priority tiers, LISTEN/NOTIFY for zero-lag dashboard updates, and per-tenant isolation for a hosted-multi-tenant story.
