# Kai Translation Engine

A headless .NET 10 service that turns this repository's agent-driven PDF→Vietnamese pipeline into an engine a remote backend can call.

```
React client ──REST──▶ .NET backend (auth, gallery) ──gRPC over reverse SSL/SSH tunnel──▶ Kai Engine (this) ──Copilot SDK──▶ Huong agent ──▶ .github/tools/*.ps1
                                                                                             │
                                                                          SQLite (queue/lease/audit)   _processing/jobs/*/job.json (authoritative progress)
```

The server stack uses three ports: the React port, the backend REST port, and the tunnel port. The engine itself listens on **one** port: gRPC over HTTP/2 on `127.0.0.1:7443`.

## RPC contract

The contract is [`protos/translation_engine.proto`](protos/translation_engine.proto); the backend generates its client from this same file. Every call must send the metadata header `x-engine-key: <secret>`. Calls without a valid key get `UNAUTHENTICATED` (the key is compared in constant time).

| RPC | Purpose |
| --- | --- |
| `UploadFile` (client stream) | First message is `metadata` (file name, size, optional `expected_sha256`, agent, requested_by), followed by `chunk` messages of ≤1 MiB. Returns the **SHA-256**, which is the job's permanent id, plus its status. Re-uploading the same bytes returns `deduplicated=true`; re-uploading a failed job requeues it. |
| `GetJobStatus` | Returns `QUEUED` (with a 1-based `queue_position` per agent), `RUNNING` (stage and chunk progress), `COMPLETED` (`result_available`), or `FAILED` (with a message). |
| `ListJobs` | Gallery listing, newest first, with an optional state filter and an opaque `page_token`. |
| `DownloadResult` (server stream) | The first message is a `header` (name, content type, size, SHA-256 of the artifact), followed by `data`. Resume an interrupted transfer with `offset`. Supports `PDF` (default) and `MARKDOWN`. |
| `GetEngineInfo` | Version, agents, and queued/running counts, for health checks. |

## Run

```powershell
# Secret: environment variable or user secrets only, never appsettings. At least 32 characters.
$env:Engine__SecretKey = '<long random secret>'
dotnet run --project engine\src\Kai.Engine
```

To expose the port to the server without opening it publicly, run this on the engine machine:

```powershell
ssh -N -R 127.0.0.1:9443:127.0.0.1:7443 deploy@your-server   # the backend then dials 127.0.0.1:9443 (h2c) on the server
```

Use `autossh` or a service wrapper to keep the tunnel alive. The tunnel provides transport encryption, and the key provides authentication.

Copilot authentication uses the logged-in Copilot CLI user by default. To use a token instead, set `Engine:Copilot:GitHubTokenEnvironmentVariable` to the *name* of an environment variable that holds it.

### Configuration (`appsettings.json` → `Engine`)

| Key | Default | Notes |
| --- | --- | --- |
| `ListenAddress` / `Port` | `127.0.0.1` / `7443` | The single RPC port. |
| `DatabasePath` | `_processing/engine/engine.db` | SQLite file (WAL mode, synchronous FULL). Gitignored. |
| `MaxUploadBytes` | 1 GiB | |
| `DefaultAgent`, `Agents[]` | `Huong` | Each agent has `Name`, `Enabled`, `MaxConcurrentJobs`, `Model`, `ReasoningEffort`. The name must match `.github/agents/<Name>.agent.md`. |
| `Scheduler` | 60 s tick, 300 s lease, renewed every 60 s, 3 attempts, 300 s backoff, 1 concurrent run | |
| `Copilot` | autopilot on; 240 min turn timeout; 10 min autopilot quiet period; 25 continuations; 3 stalled turns; 48 h run timeout | |

## Design

### State ownership

- **`job.json` is authoritative** for pipeline progress and completion. The engine only reads it. It creates `job.json` by calling `Initialize-TranslationJobs.ps1` and never writes it directly, so the existing data structure and the VS Code workflow are unchanged.
- **SQLite holds only engine state**: queue order, assigned agent, attempts, lease, Copilot session id, requester, and an append-only `job_events` audit log.
- Status responses merge the two sources. A job counts as completed only when `job.json` says so.

### Job lifecycle

```
upload ─▶ QUEUED ──acquire (lease)──▶ RUNNING ──job.json completed──▶ COMPLETED
            ▲  ▲                        │
            │  └── retryable failure ◀──┤  (attempts < max, not_before = now + backoff)
            │                           ├── blocked / attempts exhausted ──▶ FAILED ──re-upload──▶ QUEUED
            └──── graceful shutdown (attempt refunded) / crashed instance recovered at startup
```

### Resiliency and consistency

- **Idempotent ingest.** The upload streams to `_processing/engine/incoming/*.part` while it is hashed. The engine then checks the declared size, the maximum size, the `%PDF-` magic bytes, and the optional client hash. Under a commit lock the file moves into `_pdfs/` and the row is inserted. The SHA-256 is the primary key, so duplicates cost nothing. An existing `_pdfs` file with different content is never overwritten; a `-<sha8>` suffix is added instead, because overwriting would supersede that source's pipeline job.
- **Exactly one driver per job.** Jobs are claimed with a single `UPDATE … RETURNING` statement. The lease owner is `instanceId:runId`, and every write is fenced on that owner, so a stale run can never overwrite a newer one. The heartbeat renews the lease, and losing the lease cancels the run.
- **Single engine per repository.** An exclusive OS file lock on `_processing/engine/engine.lock` ensures this. Any lease from another instance is therefore orphaned, and is recovered at startup (requeued, or failed once attempts are exhausted, which protects against poison jobs).
- **Crash healing.** Each tick reconciles queued and failed rows against `job.json`: books finished outside the engine, or finished just before a crash, are marked completed instead of being re-run. At startup, completed pipeline jobs that were translated locally are imported so the gallery shows them.
- **Unattended agent, bounded.** Each run has one Copilot session with `PermissionHandler.ApproveAll`, auto-answered user-input requests, infinite sessions, and autopilot mode. The session id is stored, so a retry resumes the same conversation. A watchdog ends a turn on:
  - idle
  - a session error
  - `job.json` becoming terminal (after a grace period)
  - autopilot going quiet
  - the hard turn timeout

  After each turn the runner re-reads `job.json`. It continues, stops, or gives up after N turns without progress; that stall is retryable with backoff.
- **Graceful shutdown.** Runs are cancelled and their jobs released with the attempt refunded, within a 90 s shutdown timeout.

## Test

```powershell
dotnet test engine
pwsh -NoProfile -File .github\tools\Test-TranslationPipeline.ps1
```

The tests cover:
- queue ordering, per-agent isolation, and lease fencing
- retry, backoff, and poison-job handling
- orphan recovery and reconciliation
- gRPC auth, upload validation, and dedup
- no-overwrite of existing sources
- resumable download and pagination
- runner completion, stall, and resume behavior
- a full scheduler tick

Tests never start the Copilot runtime.
