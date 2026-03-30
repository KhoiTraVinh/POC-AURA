# POC AURA — Architecture

## Overview

POC AURA is a multi-tenant, real-time processing system built with:

- **Backend** — ASP.NET Core 8 Web API + SignalR hub
- **Frontend** — Angular 17 (standalone components, signals)
- **Processor** — Blazor Server (SmartHub), acts as a headless worker

All real-time communication flows through a **single unified SignalR hub** (`AuraHub`) backed by JWT authentication. The system demonstrates three independent features that share the same hub and token infrastructure:

1. **Print Job Pipeline** — multi-tenant print job submission and processing
2. **Bank Transaction Pipeline** — globally-locked EFT/POS transaction processing
3. **Collaborative Document Editing** — field-level pessimistic locking with heartbeat TTL

---

## Technology Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 8, SignalR, EF Core 8 |
| Database | SQL Server 2022 (via Docker) |
| Frontend | Angular 17, `@microsoft/signalr` |
| Processor | Blazor Server (SmartHub), SQLite |
| Auth | JWT Bearer (symmetric HMAC-SHA256) |
| Container | Docker + Docker Compose |

---

## High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Angular (Browser)                                                        │
│  PrintHubService    ──── WebSocket ──┐                                   │
│  TransactionService ──── WebSocket ──┤ (all to /hubs/aura)               │
│  DocumentService    ──── WebSocket ──┤                                   │
└─────────────────────────────────────┤                                    │
                                      ▼
                         ┌─────────────────────┐
                         │   ASP.NET Core API   │
                         │                      │
                         │  ┌───────────────┐   │
                         │  │   AuraHub     │◄──┼── /hubs/aura (SignalR)
                         │  │  (SignalR)    │   │
                         │  └───────┬───────┘   │
                         │          │            │
                         │  ┌───────▼───────┐   │
                         │  │ IJobRepository│   │
                         │  │  (EF Core)    │   │
                         │  └───────┬───────┘   │
                         │          │            │
                         │  ┌───────▼───────┐   │
                         │  │  SQL Server   │   │
                         │  │  (Messages)   │   │
                         │  └───────────────┘   │
                         │                      │
                         │  HTTP REST API:       │
                         │  POST /api/print/complete        ◄──────────────┐
                         │  POST /api/transaction/complete  ◄───────────────┤
                         └─────────────────────┘                           │
                                                                            │
┌───────────────────────────────────────────────────────────────────────────┘
│  Blazor SmartHub (Server)
│  ┌───────────────────────────────────────────────────────────────┐
│  │  HubConnectionWorker — single SignalR connection per server   │
│  │    client_type = smarthub → added to smarthub-{tenantId}      │
│  │                           AND bank-{tenantId}                  │
│  │                                                               │
│  │  On ExecutePrintJob   → PrintService.ProcessAsync()           │
│  │                       → POST /api/print/complete              │
│  │  On ExecuteTransaction → EftPosService.ProcessAsync()         │
│  │                       → POST /api/transaction/complete        │
│  └───────────────────────────────────────────────────────────────┘
```

---

## Authentication

All connections (Angular UI, Blazor SmartHub) authenticate with **JWT Bearer tokens**.

### Token Claims

| Claim | Key | Example Values |
|---|---|---|
| Tenant ID | `tenant_id` | `TenantA`, `TenantB` |
| Client Type | `client_type` | `ui`, `smarthub` |
| User Name | `name` (standard JWT) | `alice@TenantA` |
| Token Type | `token_type` | `access` |

> **Note**: The `bank` client type was removed. SmartHub now uses a single `smarthub` token and is added to both `smarthub-{tenantId}` and `bank-{tenantId}` groups automatically on connect.

### Token Issuance

`GET /api/auth/token?tenantId=TenantA&clientType=ui&userName=alice`

Returns `{ accessToken, refreshToken }`. No password — this is a POC with open token issuance.

`GET /api/auth/refresh?refreshToken=<token>`

Returns a refreshed `{ accessToken, refreshToken }` pair.

### SignalR Token Transport

WebSocket connections cannot carry custom headers. Angular sends the JWT via query string:

```
wss://api/hubs/aura?access_token=<jwt>
```

`JwtSecurityTokenHandler.DefaultMapInboundClaims = false` prevents ASP.NET Core from remapping standard JWT claim names, keeping claim names predictable across hubs and controllers.

---

## SignalR Hub — AuraHub

Single hub at `/hubs/aura` for all features. The hub:

1. Authenticates the connection via JWT (enforced by `[Authorize]`)
2. Adds the connection to the appropriate SignalR group(s)
3. Handles hub method invocations from clients
4. Routes events to the correct groups or individual connections

### SignalR Groups

| Group Name | Members | Purpose |
|---|---|---|
| `ui-{tenantId}` | Angular clients | Receive job notifications for a tenant |
| `smarthub-{tenantId}` | Blazor SmartHub | Receive print jobs to process |
| `bank-{tenantId}` | Blazor SmartHub (same conn) | Receive bank transactions to process |
| `ui-broadcast` | All Angular clients | Receive global bank status updates |
| `doc-all` | All Angular clients | Receive collaborative document lock events |

On connect:
- Every client is added to its `{clientType}-{tenantId}` group.
- `smarthub` clients are additionally added to `bank-{tenantId}` (single connection handles both pipelines).
- `ui` clients are additionally added to `ui-broadcast`, `doc-all`, and receive an immediate `LockSnapshot` and `BankStatus` push.

On disconnect:
- `ui` clients have all their field locks released automatically, and `FieldUnlocked` is broadcast to `doc-all` for each released lock.

### Hub Methods (callable by clients)

| Method | Called By | Description |
|---|---|---|
| `SubmitPrintJob(request)` | Angular | Persist + route a print job to SmartHub |
| `SyncPrintJobs(jobIds[])` | Angular | Re-query DB for jobs still shown as pending; emits `PrintJobComplete` for each resolved job |
| `SubmitTransaction(request)` | Angular | Attempt to acquire the global bank lock and begin processing |
| `AcquireFieldLock(docId, fieldId)` | Angular | Try to acquire exclusive edit lock on a document field |
| `ReleaseFieldLock(docId, fieldId)` | Angular | Release a previously acquired field lock |
| `HeartbeatFieldLock(docId, fieldId)` | Angular | Extend the TTL of an active lock (called every ~8 s) |
| `UpdateFieldValue(docId, fieldId, value)` | Angular | Broadcast a field value change; caller must hold the lock |

### Events Pushed by Server

| Event | Recipients | Description |
|---|---|---|
| `PrintJobQueued` | Caller only | Confirmation that a print job was accepted |
| `ExecutePrintJob` | `smarthub-{tenantId}` | Print job payload routed to SmartHub |
| `PrintJobComplete` | Submitter's connections | Job finished (success or failure) |
| `PrintJobStatusUpdate` | Other UI clients in tenant | Notifies peer tabs/users of job completion |
| `ExecuteTransaction` | `bank-{tenantId}` | Transaction payload routed to bank processor |
| `TransactionStatusChanged` | `ui-broadcast` | Bank state lifecycle (processing → completed/failed) |
| `BankStatus` | Caller on connect / `ui-broadcast` | Full bank snapshot (busy flag, current txn, history) |
| `ClientConnected` | `ui-{tenantId}` | Another client connected to the same tenant |
| `ClientDisconnected` | `ui-{tenantId}` | A client disconnected |
| `LockSnapshot` | Caller on connect | Full list of all active field locks |
| `FieldLocked` | `doc-all` (except caller) | Another user acquired a field lock |
| `FieldUnlocked` | `doc-all` | A field lock was released or the holder disconnected |
| `FieldValueChanged` | `doc-all` (except caller) | The lock holder changed a field's value |
| `FieldsExpiredUnlocked` | `doc-all` | One or more locks expired due to missed heartbeats |

---

## Print Job Pipeline

```
Angular              AuraHub               SmartHub           PrintController
   │                    │                      │                     │
   │── SubmitPrintJob ──►│                      │                     │
   │                    │── DB: INSERT Message  │                     │
   │                    │── ExecutePrintJob ───►│                     │
   │◄── PrintJobQueued ──│                      │                     │
   │                    │                      │── processes job      │
   │                    │                      │── POST /api/print/complete ──►│
   │                    │                      │                     │── DB: UPDATE Message
   │                    │                      │                     │── SignalR: PrintJobComplete
   │◄───────────────────────────────────────────────────────────────►│
```

### Reconnect Recovery — Angular

When Angular reconnects, `onReconnected$` fires. The component calls `SyncPrintJobs(pendingIds[])`. The hub queries the DB for each job ID; if a job is no longer `pending`, the hub sends `PrintJobComplete` back to the caller. This resolves the race condition where SmartHub processes jobs while Angular was disconnected and not yet in the `ui-{tenantId}` group.

### Reconnect Recovery — SmartHub

When SmartHub reconnects it calls `GET /api/print/pending` to retrieve all `pending` jobs for its tenant, then processes them in order. No print job is permanently lost due to a processor restart.

---

## Bank Transaction Pipeline

```
Angular              AuraHub          TransactionQueueService        SmartHub
   │                    │                       │                       │
   │── SubmitTransaction►│                       │                       │
   │                    │── TrySubmitAsync ─────►│                       │
   │                    │  (acquire global lock) │                       │
   │◄─ accepted/rejected │                       │── DB: INSERT Message   │
   │                    │                       │── ExecuteTransaction ──►│
   │                    │                       │                       │── processes txn
   │                    │                       │◄── POST /api/transaction/complete
   │                    │                       │── CompleteAsync (DB)   │
   │                    │                       │── release lock         │
   │◄── TransactionStatusChanged (broadcast to ui-broadcast) ────────────│
   │◄── BankStatus (broadcast to ui-broadcast) ──────────────────────────│
```

**Key design**: the bank is a **globally shared resource** — only one transaction can be in-flight at any time across all tenants. `TrySubmitAsync` is fail-fast (0 ms wait) and rejects concurrent submissions immediately. The lock is released *before* broadcasting the result, so the next submission can proceed while events are still in flight.

**On connect**: UI clients receive an immediate `BankStatus` push with the current global bank state.

---

## Collaborative Document Pipeline

```
Angular (User A)     AuraHub          DocumentLockService   Angular (User B)
   │                    │                    │                    │
   │── AcquireFieldLock►│                    │                    │
   │                    │── TryAcquire ─────►│                    │
   │◄─ { acquired:true }│                    │                    │
   │                    │── FieldLocked ──────────────────────────►│
   │── HeartbeatFieldLock every 8s           │                    │
   │                    │── Heartbeat ───────►│                    │
   │── UpdateFieldValue►│                    │                    │
   │                    │── FieldValueChanged ────────────────────►│
   │── ReleaseFieldLock►│                    │                    │
   │                    │── Release ─────────►│                    │
   │                    │── FieldUnlocked ────────────────────────►│
```

**Lock lifecycle**:
- TTL is **30 seconds**. If the client misses heartbeats (e.g. browser tab backgrounded, network lag), the `DocumentLockService` hosted service sweeps expired locks and broadcasts `FieldsExpiredUnlocked`.
- On disconnect, the hub calls `ReleaseAllByConnection(connectionId)` to release all locks held by that connection, then broadcasts `FieldUnlocked` for each.
- On reconnect, heartbeat timers are stopped immediately to prevent zombie lock re-creation on the new connection.

---

## Database

Single `Messages` table stores both print jobs and bank transactions.

### Messages Table Schema

| Column | Type | Description |
|---|---|---|
| `Id` | `int` PK | Auto-increment |
| `Type` | `nvarchar(50)` | `print_job` or `bank_txn` |
| `Ref` | `nvarchar(50)` | 10-char uppercase alphanumeric job ID |
| `TenantId` | `nvarchar(100)` | Owning tenant |
| `Payload` | `nvarchar(max)` | JSON job details |
| `Status` | `nvarchar(20)` | `pending` → `completed` or `failed` |
| `RequestorUserId` | `nvarchar(200)` | JWT `name` claim of the submitter |
| `RequestorConnectionId` | `nvarchar(200)` | SignalR connection ID at submission time |
| `CompletedAt` | `datetime2` | Set when job finishes |
| `ResultMessage` | `nvarchar(500)` | Completion message |
| `CreatedAt` | `datetime2` | Submission timestamp |

### Repository Pattern

All DB access flows through `IJobRepository` / `JobRepository` (Scoped). Hubs, controllers, and services never use `AppDbContext` directly.

`TransactionQueueService` (Singleton) resolves `IJobRepository` via `IServiceScopeFactory` to respect the Scoped lifetime boundary.

---

## Connection Tracking

`IConnectionTracker` / `ConnectionTracker` (Singleton) maintains a bidirectional map between `userId` and `connectionId`.

**Why it exists**: A new SignalR `connectionId` is issued on every reconnect, making the `connectionId` recorded in the DB stale after any disconnect. Users may also have multiple browser tabs open simultaneously.

**How it works**:
- `Register(userId, connectionId)` — called in `OnConnectedAsync`
- `Unregister(connectionId)` — called in `OnDisconnectedAsync`
- `GetConnectionIds(userId)` — returns all active connection IDs for a user

**Usage in `PrintController.Complete`**:
1. Look up `_tracker.GetConnectionIds(message.RequestorUserId)`
2. If the user is online → send `PrintJobComplete` to all their connections + `PrintJobStatusUpdate` to the rest of the tenant group
3. If the user is offline → broadcast `PrintJobComplete` to the whole `ui-{tenantId}` group so they see the result on reconnect

---

## Service Lifetimes

| Service | Lifetime | Reason |
|---|---|---|
| `IJobRepository` / `JobRepository` | **Scoped** | One `AppDbContext` per request |
| `JwtService` | **Singleton** | Stateless; holds the signing key |
| `IConnectionTracker` / `ConnectionTracker` | **Singleton** | Must persist across all requests |
| `ITransactionQueueService` / `TransactionQueueService` | **Singleton** | Holds the global semaphore and in-memory state |
| `IDocumentLockService` / `DocumentLockService` | **Singleton** | Holds all active field locks; also runs as `IHostedService` to sweep expired locks |

---

## Directory Structure

```
POC-AURA/
├── backend/
│   └── POC.AURA.Api/
│       ├── Common/
│       │   ├── Constants/      # AuraConstants — MessageTypes, JobStatuses, ClaimNames,
│       │   │                   #   ClientTypes, HubGroups, HubEvents
│       │   ├── Extensions/     # ClaimsPrincipal helpers (GetTenantId, GetClientType, GetUserName)
│       │   └── Models/         # Request/response DTOs (PrintModels, TransactionModels, DocumentModels)
│       ├── Data/
│       │   ├── AppDbContext.cs
│       │   ├── Entities/       # Message (EF entity)
│       │   ├── Configurations/ # MessageConfiguration (EF fluent config)
│       │   └── Repositories/   # IJobRepository / JobRepository
│       ├── Migrations/         # EF Core migrations (5 migrations → final Messages schema)
│       └── Server/
│           ├── Auth/           # JwtService — token generation & signing key
│           ├── Controllers/    # AuthController (token + refresh), PrintController,
│           │                   #   TransactionController, ConnectionsController (debug)
│           ├── Hubs/           # AuraHub — unified SignalR hub
│           └── Services/       # ConnectionTracker, TransactionQueueService,
│                               #   DocumentLockService (+ IHostedService sweep)
├── frontend/
│   └── src/app/
│       ├── core/
│       │   ├── models/         # TypeScript interfaces (print.models.ts, transaction.models.ts)
│       │   └── services/
│       │       ├── auth.service.ts         # Fetches + caches JWT tokens; handles refresh
│       │       ├── session.service.ts      # Current user session (tenantId, userName)
│       │       ├── print-hub.service.ts    # Per-tenant SignalR connection for print jobs
│       │       ├── transaction.service.ts  # SignalR connection for bank transactions
│       │       ├── document.service.ts     # SignalR connection for collaborative doc locking
│       │       └── signalr-retry.ts        # Infinite exponential backoff retry policy
│       └── features/
│           ├── login/                      # Login / tenant selection form
│           ├── multi-tenant/               # Print job submission & result UI
│           ├── transaction-queue/          # Bank transaction submission & history UI
│           └── collaborative-doc/          # Multi-user field-level pessimistic locking demo
└── blazor/
    └── POC.AURA.SmartHub/
        ├── Common/
        │   ├── Constants/      # AppConstants, ClientOAuthConstant, EclipseApiUrl
        │   ├── Models/         # Auth/connection DTOs
        │   └── ConnectionStatus.cs
        ├── Components/         # Blazor pages (Index, Print, Bank, ServerEntry)
        ├── Data/
        │   ├── SmartHubDbContext.cs     # SQLite — stores server connections + auth tokens
        │   ├── Entities/               # ServerConnection, AuthToken
        │   └── ServerConnectionRepository.cs
        └── Server/
            ├── Hubs/           # BlazorConnectionHub — push notifications to Blazor UI
            ├── Services/       # PrintService, EftPosService, ServerConnectionService,
            │                   #   ConnectionEventService, TokenSchedulerService
            └── Workers/        # HubConnectionWorker — long-lived SignalR client connections;
                                #   InfiniteRetryPolicy
```

---

## Docker Compose Services

### Production (`docker-compose.yml`)

| Service | Port | Notes |
|---|---|---|
| `db` | 1433 | SQL Server 2022 |
| `cloudbeaver` | 8978 | DB admin UI (CloudBeaver) |
| `backend` (api) | 8080 | ASP.NET Core 8; runs EF migrations on startup with 5-retry loop |
| `smarthub` | 5001 | Blazor Server; connects to API as `smarthub` client |
| `frontend` | 4200 | Angular served by nginx; `/api/` and `/hubs/` proxied to `backend:8080` |

### Development (`docker-compose.dev.yml`)

| Service | Notes |
|---|---|
| `db` | SQL Server 2022 |
| `cloudbeaver` | DB admin UI |
| `workspace` | Dev container with source mounted; maps ports for LAN access |
| `jmeter` | JMeter load testing container |

The frontend `.dockerignore` excludes `node_modules/` and `dist/` to prevent BuildKit from following symlinks in `node_modules/.bin/`.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| Single unified hub (`AuraHub`) | All real-time features share one WebSocket per client; simpler token and group management |
| Single SmartHub connection handles print + bank | SmartHub token has `client_type=smarthub`; hub adds it to both `smarthub-{tenantId}` and `bank-{tenantId}` groups automatically |
| Fail-fast bank lock | No server-side queuing; clients retry explicitly, keeping the API responsive |
| `RequestorUserId` stored in Messages | Stable identity across reconnects; raw `connectionId` becomes stale after any disconnect |
| `ConnectionTracker` (userId → connectionIds) | Routes notification to all active tabs of the submitter; survives reconnect |
| `SyncPrintJobs` hub method | Resolves race condition where SmartHub processes jobs before Angular joins its group |
| SmartHub fetches pending jobs on reconnect | Guarantees no work is lost if the processor container restarts |
| Field lock TTL 30 s + heartbeat every 8 s | Locks auto-expire if the editing user crashes or navigates away without releasing |
| Stop heartbeats on `onreconnecting` | Prevents zombie locks: the server releases all locks for the old connection in `OnDisconnectedAsync`; without stopping timers, a heartbeat via the new connection would re-create a lock with an empty `ConnectionId` that can never be released |
| `LockSnapshot` + `BankStatus` pushed on connect | UI clients get full current state immediately without polling |
| Repository pattern | Keeps EF Core queries out of hubs and controllers; single testable DB layer |
| First migration creates complete final schema | Avoids EF Core alphabetical sort issues with dependent migrations; schema is correct from the very first `Migrate()` call |
