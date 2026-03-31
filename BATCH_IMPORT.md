# Batch Import — Technical Documentation

## Overview

The Batch Import feature allows users to upload large CSV files (up to 500 MB) and insert millions of rows into SQL Server with real-time progress reporting via SignalR.

**Stack:** Angular → ASP.NET Core → Hangfire → SqlBulkCopy → SQL Server → SignalR → Angular

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Angular (Browser)                           │
│                                                                     │
│  BatchImportComponent                                               │
│       │  upload()          ┌─────────────────────────────────┐     │
│       │──POST /api/batch/upload──▶│                           │     │
│       │                   │   BatchImportService (Angular)   │     │
│       │  SignalR events   │   AuthService (JWT token)        │     │
│       │◀──BatchProgress───│   batch-import.service.ts        │     │
│       │◀──BatchCompleted──└─────────────────────────────────┘     │
│       │◀──BatchFailed─────           ↕ WebSocket /hubs/aura        │
└───────┼─────────────────────────────────────────────────────────────┘
        │ HTTP + JWT
        ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     ASP.NET Core Backend                            │
│                                                                     │
│  BatchController (thin — route + map only)                          │
│       │                                                             │
│       ├─[POST /upload]──▶ IBatchImportService                       │
│       │                       │ 1. Save CSV to temp disk            │
│       │                       │ 2. Count rows (stream)              │
│       │                       │ 3. Persist BatchJob (EF Core)       │
│       │                       │ 4. Hangfire.Enqueue<BatchImportJob> │
│       │                       └─────────────────────────────────▶  │
│       │                                                 Hangfire    │
│       ├─[DELETE /{id}]──▶ repo.GetById + importJob.CancelAsync      │
│       ├─[GET /{id}]─────▶ IBatchJobRepository.GetByIdForTenant      │
│       ├─[GET /]─────────▶ IBatchJobRepository.ListForTenant         │
│       └─[GET /{id}/records]─▶ AppDbContext (paginated query)        │
│                                                                     │
│  IBatchJobRepository / BatchJobRepository (EF Core)                 │
│       Owns all BatchJob DB operations                               │
│                                                                     │
│  AuraHub (SignalR)                                                  │
│       Pushes BatchProgress / BatchCompleted / BatchFailed           │
│       to group "ui-{tenantId}"                                      │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ Hangfire dequeues
                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    BatchImportJob (Hangfire Worker)                 │
│                                                                     │
│  1. CleanupPartialRows  — DELETE WHERE BatchId (idempotent retry)   │
│  2. UpdateStatus        — "running"                                 │
│  3. Open FileStream (64 KB OS buffer)                               │
│  4. Open SqlConnection                                              │
│  5. SqlBulkCopy (TableLock, BatchSize=50k, NotifyAfter=10k)         │
│       │                                                             │
│       │  CsvDataReader.Read()     ← pulls 1 line from StreamReader  │
│       │  CsvDataReader.GetValue() ← parses column on demand        │
│       │       (decimal/DateTime as stack values — no heap alloc)    │
│       │                                                             │
│       │  Every 10 000 rows ACK'd by SQL Server:                     │
│       │  SqlRowsCopied → fire-and-forget PushProgressAsync          │
│       │       └──▶ SignalR → Angular progress bar                   │
│       │                                                             │
│       └──▶ SQL Server ImportedRecords table                         │
│                                                                     │
│  6. FinalizeAsync       — "completed" / "failed"                    │
│  7. BatchCompleted / BatchFailed → SignalR                          │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Sequence Diagram — Happy Path

```
Angular        BatchController   BatchImportService   Hangfire   BatchImportJob   SQL Server   SignalR
   │                  │                  │                │              │              │           │
   │──POST /upload───▶│                  │                │              │              │           │
   │                  │──UploadAsync()──▶│                │              │              │           │
   │                  │                  │─SaveFile()     │              │              │           │
   │                  │                  │─CountRows()    │              │              │           │
   │                  │                  │─AddAsync()─────────────────────────────────▶│           │
   │                  │                  │─Enqueue()─────▶│              │              │           │
   │◀──200 OK─────────│◀─BatchUploadResp─│                │              │              │           │
   │                  │                  │                │              │              │           │
   │  (status: queued, progress bar appears)              │              │              │           │
   │                  │                  │                │─dequeue─────▶│              │           │
   │                  │                  │                │              │─CleanupRows()▶           │
   │                  │                  │                │              │─UpdateStatus("running")  │
   │                  │                  │                │              │─BulkCopy loop:           │
   │                  │                  │                │              │   Read() → GetValue()    │
   │                  │                  │                │              │──────── INSERT ─────────▶│
   │                  │                  │                │              │  every 10k rows:         │
   │◀─────────────────────────── BatchProgress (%) ───────────────────────────────────────────────│
   │                  │                  │                │              │──────── INSERT ─────────▶│
   │◀─────────────────────────── BatchProgress (%) ───────────────────────────────────────────────│
   │                  │                  │                │              │─FinalizeAsync("completed")│
   │◀─────────────────────────── BatchCompleted ──────────────────────────────────────────────────│
```

---

## File Structure

```
backend/POC.AURA.Api/
│
├── Server/Controllers/
│   └── BatchController.cs          # Thin REST layer — route, validate, delegate
│
├── Service/Batch/
│   ├── IBatchImportService.cs      # Upload orchestration interface
│   ├── BatchImportService.cs       # Save file → count rows → persist → enqueue
│   └── BatchImportJob.cs           # Hangfire job — execute + cancel
│
├── Data/
│   ├── Entities/
│   │   └── BatchJob.cs             # EF entity (BatchJob, BatchCheckpoint, ImportedRecord)
│   ├── Configurations/
│   │   └── BatchJobConfiguration.cs # EF Fluent API table mappings
│   └── Repositories/
│       ├── IBatchJobRepository.cs  # All BatchJob persistence operations
│       └── BatchJobRepository.cs
│
├── Infrastructure/
│   └── CsvDataReader.cs            # IDataReader streaming CSV → SqlBulkCopy
│
└── Common/Dtos/
    └── BatchDtos.cs                # BatchUploadResponse, BatchJobDto, RecordsPageDto

frontend/src/app/
│
├── core/services/
│   └── batch-import.service.ts     # HTTP + SignalR client
│
└── features/batch-import/
    └── batch-import.component.ts   # Import tab + Data viewer tab
```

---

## Design Patterns Used

| Pattern | Where | Why |
|---|---|---|
| **Repository** | `IBatchJobRepository` | Isolates DB queries; controller/job never touch EF directly |
| **Service Layer** | `IBatchImportService` | Separates upload orchestration from HTTP routing and job execution |
| **Facade** | `BatchImportService` | Single entry point hides: file I/O + row counting + DB + Hangfire enqueue |
| **Strategy (IDataReader)** | `CsvDataReader` | Pluggable data source for SqlBulkCopy — swap CSV for JSON/XML without changing job |
| **Template Method** | `BatchImportJob.ExecuteAsync` | Fixed skeleton (cleanup → open → bulkcopy → finalize); CsvDataReader provides the varying step |

---

## Why Single-Stream SqlBulkCopy with TableLock

### The parallelism trap

```
❌ Parallel connections (what seems faster but isn't):

   conn1 ──▶ log buffer latch ──▶ write ──▶ release
   conn2 ──▶ WAIT ──────────────────────▶ write ──▶ release
   conn3 ──▶ WAIT ──────────────────────────────▶ write

   SQL Server transaction log writer = SERIAL mutex.
   N connections → N threads queued on the same latch.
   Overhead of N connections > any concurrency gain.
```

```
✅ Single connection + TableLock (minimal logging):

   Full logging   : every INSERT writes a per-row log record  → 50 MB log for 500k rows
   Minimal logging: only extent allocations logged            → 5 MB log for 500k rows

   Minimal logging requires:
     1. Recovery model = SIMPLE or BULK_LOGGED (SQL Server default in dev)
     2. SqlBulkCopyOptions.TableLock
     3. No concurrent transactions on the table (single connection guarantees this)

   Result: ~10× less log I/O → 5–8× faster throughput
```

### Memory profile

```
  Disk ──▶ FileStream (64 KB buffer) ──▶ StreamReader
                                              │ Read() pulls 1 line
                                         CsvDataReader
                                              │ GetValue() parses column on demand
                                         SqlBulkCopy (BatchSize = 50 000)
                                              │ TDS packet (50k rows at a time)
                                         SQL Server

  RAM used:
    FileStream buffer : 64 KB (constant)
    _parts[]          : 1 string[] of 4 refs per row, GC'd after 6 GetValue calls
    decimal/DateTime  : stack values, gone after GetValue returns
    Total             : ~64 KB constant, independent of file size
```

---

## SQL Tables

```sql
BatchJobs
  Id            NVARCHAR(50)   PK
  TenantId      NVARCHAR(100)
  FileName      NVARCHAR(260)
  FilePath      NVARCHAR(500)
  FileSizeBytes BIGINT
  TotalRows     INT
  ProcessedRows INT
  Status        NVARCHAR(20)   -- queued | running | completed | failed | cancelled
  HangfireJobId NVARCHAR(100)
  ErrorMessage  NVARCHAR(MAX)
  CreatedAt     DATETIME2
  CompletedAt   DATETIME2

ImportedRecords
  Id         BIGINT IDENTITY  PK
  BatchId    NVARCHAR(50)     FK → BatchJobs(Id)
  Name       NVARCHAR(200)
  Category   NVARCHAR(100)
  Value      DECIMAL(18,2)
  Timestamp  DATETIME2
  ImportedAt DATETIME2

INDEX IX_ImportedRecords_BatchId ON ImportedRecords(BatchId)
  -- DISABLED before bulk insert, REBUILT after (avoids per-row B-tree updates)
```

---

## SignalR Events (server → Angular)

| Event | Payload | When |
|---|---|---|
| `BatchProgress` | `{ batchId, processedRows, totalRows, percent, rowsPerSecond }` | Every 10 000 rows ACK'd |
| `BatchCompleted` | `{ batchId, processedRows, durationMs, rowsPerSecond }` | Job finished successfully |
| `BatchFailed` | `{ batchId, error }` | Job threw exception (Hangfire will retry) |
| `BatchCancelled` | `{ batchId }` | User cancelled |

All events are scoped to the SignalR group `ui-{tenantId}` — only the uploader's tenant receives them.

---

## API Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/batch/upload` | Upload CSV, enqueue Hangfire job. Returns `BatchUploadResponse`. |
| `DELETE` | `/api/batch/{id}` | Cancel running/queued job, rollback inserted rows. |
| `GET` | `/api/batch/{id}` | Get job status. Returns `BatchJobDto`. |
| `GET` | `/api/batch` | List 20 most recent jobs for the current tenant. |
| `GET` | `/api/batch/{id}/records` | Paginated viewer of imported rows. Query: `page`, `pageSize`, `search`, `category`. |

All endpoints require JWT Bearer token (`Authorization: Bearer <token>`).

---

## Idempotent Retry

Hangfire retries failed jobs automatically (`Attempts = 3, delays = 30s / 60s / 120s`).

Before re-inserting, `BatchImportJob` always calls:
```sql
DELETE FROM ImportedRecords WHERE BatchId = @batchId
```
This ensures a retry always starts from a clean slate — no duplicate rows.

---

## Cancel & Rollback

```
User clicks Cancel
      │
      ▼
BatchController.Cancel()
      │─ BackgroundJob.Delete(hangfireJobId)   ← stops Hangfire if still queued
      │─ importJob.CancelAsync(batchId, tenantId)
                │─ repo.FinalizeAsync("cancelled")
                │─ DELETE FROM ImportedRecords WHERE BatchId = @id
                │─ File.Delete(tempFilePath)
                └─ SignalR → BatchCancelled → Angular
```
