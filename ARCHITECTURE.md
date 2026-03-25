# POC AURA — Architecture

## Overview

POC AURA is a multi-tenant, real-time processing system built with:

- **Backend** — ASP.NET Core 8 Web API + SignalR hub
- **Frontend** — Angular 17 (standalone components, signals)
- **Processor** — Blazor Server (SmartHub), acts as a headless worker

All real-time communication flows through a **single unified SignalR hub** (`AuraHub`) backed by JWT authentication. The system demonstrates two independent processing pipelines that share the same hub and token infrastructure.

---

## Technology Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 8, SignalR, EF Core 8 |
| Database | SQL Server (via Docker) |
| Frontend | Angular 17, `@microsoft/signalr` |
| Processor | Blazor Server (SmartHub) |
| Auth | JWT Bearer (symmetric HMAC-SHA256) |
| Container | Docker + Docker Compose |

---

## High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Angular (Browser)                                                        │
│  PrintHubService  ──── WebSocket ──┐                                     │
│  BankHubService   ──── WebSocket ──┤                                     │
└────────────────────────────────────┤                                     │
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
                          │  POST /api/print/complete     ◄────────────────┐
                          │  POST /api/transaction/complete ◄───────────────┤
                          └─────────────────────┘                         │
                                                                           │
┌──────────────────────────────────────────────────────────────────────────┘
│  Blazor SmartHub (Server)
│  ┌──────────────────────────────────────────────────────┐
│  │  PrintProcessorService  ── SignalR (smarthub client) │
│  │  BankProcessorService   ── SignalR (bank client)     │
│  │                                                      │
│  │  On job complete → POST /api/print/complete          │
│  │                  → POST /api/transaction/complete    │
│  └──────────────────────────────────────────────────────┘
```

---

## Authentication

All connections (Angular UI, Blazor SmartHub) authenticate with **JWT Bearer tokens**.

### Token Claims

| Claim | Key | Example Values |
|---|---|---|
| Tenant ID | `tenant_id` | `TenantA`, `TenantB` |
| Client Type | `client_type` | `ui`, `smarthub`, `bank` |
| User Name | `name` (standard JWT) | `alice@TenantA` |
| Token Type | `token_type` | `access` |

### Token Issuance

`GET /api/auth/token?tenantId=TenantA&clientType=ui&userName=alice`

Returns `{ accessToken: "..." }`. No password — this is a POC with open token issuance.

### SignalR Token Transport

WebSocket connections cannot carry custom headers. Angular sends the JWT via query string:

```
wss://api/hubs/aura?access_token=<jwt>
```

`JwtSecurityTokenHandler.DefaultMapInboundClaims = false` prevents ASP.NET Core from remapping standard JWT claim names (e.g. `sub` → `ClaimTypes.NameIdentifier`), keeping claim names predictable across hubs and controllers.

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
| `smarthub-{tenantId}` | Blazor SmartHub (print) | Receive print jobs to process |
| `bank-{tenantId}` | Blazor SmartHub (bank) | Receive bank transactions to process |
| `ui-broadcast` | All Angular clients | Receive global bank status updates |

On connect, each client is added to its `{clientType}-{tenantId}` group. Angular (`ui`) clients are additionally added to `ui-broadcast`.

### Hub Methods (callable by clients)

| Method | Called By | Description |
|---|---|---|
| `SubmitPrintJob(request)` | Angular | Persist + route a print job to SmartHub |
| `SyncPrintJobs(jobIds[])` | Angular | Re-query DB for jobs still shown as pending; emits `PrintJobComplete` for each resolved job |
| `SubmitTransaction(request)` | Angular | Attempt to acquire the global bank lock and begin processing |

### Events Pushed by Server

| Event | Recipients | Description |
|---|---|---|
| `PrintJobQueued` | Caller only | Confirmation that a print job was accepted |
| `ExecutePrintJob` | `smarthub-{tenantId}` | Print job payload routed to SmartHub |
| `PrintJobComplete` | Submitter's connections | Job finished (success or failure) |
| `PrintJobStatusUpdate` | Other UI clients in tenant | Notifies peer tabs/users of job completion |
| `ExecuteTransaction` | `bank-{tenantId}` | Transaction payload routed to bank processor |
| `TransactionStatusChanged` | `ui-broadcast` | Bank state lifecycle (processing → completed/failed) |
| `BankStatus` | `ui-broadcast` / caller | Full bank snapshot (busy flag, current txn, history) |
| `ClientConnected` | `ui-{tenantId}` | Another client connected to the same tenant |
| `ClientDisconnected` | `ui-{tenantId}` | A client disconnected |

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

When SmartHub reconnects to the hub it calls `GET /api/print/pending` to retrieve all `pending` jobs for its tenant, then processes them in order. No print job is permanently lost due to a processor restart.

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

---

## Directory Structure

```
POC-AURA/
├── backend/
│   └── POC.AURA.Api/
│       ├── Auth/               # JwtService — token generation & signing key
│       ├── Constants/          # AuraConstants — MessageTypes, JobStatuses, ClaimNames,
│       │                       #   ClientTypes, HubGroups, HubEvents
│       ├── Controllers/        # AuthController, PrintController, TransactionController
│       ├── Data/               # AppDbContext
│       ├── Entities/           # Message (EF entity)
│       ├── Extensions/         # ClaimsPrincipal helpers (GetTenantId, GetClientType, GetUserName)
│       ├── Hubs/               # AuraHub — unified SignalR hub
│       ├── Migrations/         # EF Core migrations
│       ├── Models/             # Request/response DTOs (PrintJob, TransactionRequest, …)
│       ├── Repositories/       # IJobRepository / JobRepository
│       └── Services/           # ConnectionTracker, TransactionQueueService,
│                               #   IConnectionTracker, ITransactionQueueService
├── frontend/
│   └── src/app/
│       ├── core/
│       │   ├── models/         # TypeScript interfaces (PrintJob, PrintJobResult, …)
│       │   └── services/
│       │       ├── auth.service.ts         # Fetches JWT tokens from /api/auth/token
│       │       ├── print-hub.service.ts    # Per-tenant SignalR connection for print jobs
│       │       ├── bank-hub.service.ts     # SignalR connection for bank transactions
│       │       ├── session.service.ts      # Current user session (tenantId, userName)
│       │       └── signalr-retry.ts        # Infinite exponential backoff retry policy
│       └── features/
│           ├── login/                      # Login / tenant selection form
│           ├── multi-tenant/               # Print job submission & result UI
│           └── transaction-queue/          # Bank transaction submission & history UI
└── smarthub/
    └── POC.AURA.SmartHub/
        ├── Services/
        │   ├── TenantHubServiceBase.cs     # Abstract base — connect, reconnect,
        │   │                               #   fetch pending jobs on startup
        │   ├── PrintProcessorService.cs    # Receives ExecutePrintJob; simulates processing;
        │   │                               #   POST /api/print/complete
        │   └── BankProcessorService.cs     # Receives ExecuteTransaction; simulates processing;
        │                                   #   POST /api/transaction/complete
        └── Pages/                          # Blazor UI showing processor status per tenant
```

---

## Docker Compose Services

| Service | Port | Notes |
|---|---|---|
| `api` | 5000 | ASP.NET Core 8; runs EF migrations on startup with 5-retry loop |
| `smarthub` | 5001 | Blazor Server; connects to API as `smarthub` + `bank` clients |
| `frontend` | 4200 | Angular served by nginx; `/api/` and `/hubs/` proxied to `api` |
| `db` | 1433 | SQL Server 2022 |

The frontend `.dockerignore` excludes `node_modules/` and `dist/` to prevent BuildKit from following symlinks in `node_modules/.bin/`.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| Single unified hub (`AuraHub`) | All real-time features share one WebSocket per client; simpler token and group management |
| Fail-fast bank lock | No server-side queuing; clients retry explicitly, keeping the API responsive |
| `RequestorUserId` stored in Messages | Stable identity across reconnects; raw `connectionId` becomes stale after any disconnect |
| `ConnectionTracker` (userId → connectionIds) | Routes notification to all active tabs of the submitter; survives reconnect |
| `SyncPrintJobs` hub method | Resolves race condition where SmartHub processes jobs before Angular joins its group |
| SmartHub fetches pending jobs on reconnect | Guarantees no work is lost if the processor container restarts |
| Repository pattern | Keeps EF Core queries out of hubs and controllers; single testable DB layer |
| First migration creates complete final schema | Avoids EF Core alphabetical sort issues with dependent migrations; schema is correct from the very first `Migrate()` call |
