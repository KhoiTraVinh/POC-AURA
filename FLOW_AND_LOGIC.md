# POC AURA — Tài liệu Flow & Logic

## Mục lục

1. [Tổng quan hệ thống](#1-tổng-quan-hệ-thống)
2. [Authentication & JWT](#2-authentication--jwt)
3. [Kết nối SignalR & Groups](#3-kết-nối-signalr--groups)
4. [Print Job Pipeline](#4-print-job-pipeline)
5. [Bank Transaction Pipeline](#5-bank-transaction-pipeline)
6. [Collaborative Document Pipeline](#6-collaborative-document-pipeline)
7. [SmartHub Worker — Logic nội bộ](#7-smarthub-worker--logic-nội-bộ)
8. [Recovery khi mất kết nối](#8-recovery-khi-mất-kết-nối)
9. [Persistence & Database](#9-persistence--database)

---

## 1. Tổng quan hệ thống

Hệ thống gồm 3 process chạy độc lập, giao tiếp qua SignalR và HTTP:

```
┌─────────────────────────────────────────────────────────────────┐
│  ANGULAR (Browser)                                               │
│                                                                  │
│  AuthService        — quản lý JWT token (cache + refresh)        │
│  PrintHubService    — WebSocket kết nối đến /hubs/aura           │
│  TransactionService — WebSocket kết nối đến /hubs/aura           │
│  DocumentService    — WebSocket kết nối đến /hubs/aura           │
└────────────────────┬─────────────────────────────────────────────┘
                     │ WebSocket (JWT qua query string)
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│  ASP.NET CORE API (backend)                                      │
│                                                                  │
│  AuraHub            — SignalR hub duy nhất, xử lý mọi feature    │
│  JwtService         — phát + validate JWT                        │
│  ConnectionTracker  — map userId ↔ connectionId                  │
│  TransactionQueueService — global bank lock (SemaphoreSlim)      │
│  DocumentLockService     — field-level pessimistic lock + TTL    │
│  JobRepository      — EF Core, thao tác DB                       │
└───────┬──────────────────────────────────┬───────────────────────┘
        │ SignalR (WebSocket)               │ HTTP REST
        │                                  │
        ▼                                  ▼
┌──────────────────────┐        ┌────────────────────┐
│  BLAZOR SMARTHUB     │        │  POST /api/print/  │
│                      │◄───────│       complete     │
│  HubConnectionWorker │        │  POST /api/txn/    │
│  PrintService (mock) │        │       complete     │
│  EftPosService (mock)│        └────────────────────┘
│  BlazorConnectionHub │
└──────────────────────┘
```

---

## 2. Authentication & JWT

### 2.1 Cấu trúc token

Mỗi JWT chứa các claim:

| Claim | Ví dụ | Mục đích |
|---|---|---|
| `tenant_id` | `TenantA` | Xác định tenant |
| `client_type` | `ui` hoặc `smarthub` | Xác định loại client |
| `name` (JWT standard) | `alice@TenantA` | Username, dùng làm lock owner |
| `sub` | `TenantA_ui_alice` | User ID |
| `token_type` | `access` hoặc `refresh` | Phân biệt loại token |

### 2.2 Flow lấy token (Angular)

```
Angular gọi AuthService.getToken(tenantId, 'ui', userName)
    │
    ├─ Có token cache VÀ còn hơn 60 giây?
    │   └─ YES → trả về token cache ngay
    │
    ├─ Có refresh token?
    │   └─ YES → POST /api/auth/refresh → cập nhật cache → trả về
    │            (nếu refresh fail → tiếp tục xuống)
    │
    └─ POST /api/auth/token { tenantId, clientType, userName }
           → lưu vào Map<"tenantA_ui", TokenPair>
           → trả về
```

Cache key: `${tenantId}_${clientType}` — mỗi tenant × clientType có token riêng.

### 2.3 Cơ chế token trong SignalR

WebSocket không hỗ trợ custom header. Angular gửi token qua query string:

```
wss://backend/hubs/aura?access_token=eyJhbGc...
```

Backend có event handler để đọc token từ query string (chỉ áp dụng cho path `/hubs/`):

```csharp
OnMessageReceived = context =>
{
    var token = context.Request.Query["access_token"];
    if (!string.IsNullOrEmpty(token) &&
        context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
        context.Token = token;
    return Task.CompletedTask;
}
```

`accessTokenFactory` trong Angular service tự động được gọi lại khi token cần refresh trước mỗi reconnect.

---

## 3. Kết nối SignalR & Groups

### 3.1 Groups — bảng định tuyến

```
Group name           Thành viên                  Nhận event
─────────────────────────────────────────────────────────────────
ui-{tenantId}        Angular (tenant X)          PrintJobComplete, ClientConnected/Disconnected
smarthub-{tenantId}  Blazor SmartHub             ExecutePrintJob
bank-{tenantId}      Blazor SmartHub (cùng conn) ExecuteTransaction
ui-broadcast         Tất cả Angular              TransactionStatusChanged, BankStatus
doc-all              Tất cả Angular              FieldLocked, FieldUnlocked, FieldValueChanged
```

### 3.2 OnConnectedAsync — logic phân nhóm

```csharp
AuraHub.OnConnectedAsync()
    │
    ├─ ConnectionTracker.Register(UserName, ConnectionId)
    │
    ├─ Groups.AddToGroup(connId, "{clientType}-{tenantId}")
    │   → tất cả client đều vào group này
    │
    ├─ IF clientType == "smarthub"
    │   └─ Groups.AddToGroup(connId, "bank-{tenantId}")
    │      [một connection xử lý cả print VÀ bank]
    │
    └─ IF clientType == "ui"
        ├─ Groups.AddToGroup(connId, "ui-broadcast")
        ├─ Groups.AddToGroup(connId, "doc-all")
        ├─ Clients.Caller ← LockSnapshot (toàn bộ lock đang active)
        └─ Clients.Caller ← BankStatus (trạng thái ngân hàng hiện tại)
```

Khi `clientType = "smarthub"`, SmartHub dùng một connection duy nhất nhưng được thêm vào hai group:
- `smarthub-{tenantId}` → nhận `ExecutePrintJob`
- `bank-{tenantId}` → nhận `ExecuteTransaction`

### 3.3 OnDisconnectedAsync

```csharp
AuraHub.OnDisconnectedAsync()
    │
    ├─ ConnectionTracker.Unregister(ConnectionId)
    ├─ Groups.RemoveFromGroup cho group chính
    │
    ├─ IF clientType == "smarthub"
    │   └─ Groups.RemoveFromGroup "bank-{tenantId}"
    │
    └─ IF clientType == "ui"
        ├─ Groups.RemoveFromGroup "ui-broadcast" và "doc-all"
        ├─ DocumentLockService.ReleaseAllByConnection(ConnectionId)
        │   → trả về danh sách lock đã release
        └─ FOR EACH lock released:
            └─ Clients.Group("doc-all") ← FieldUnlocked
```

---

## 4. Print Job Pipeline

### 4.1 Happy path

```
Angular                    AuraHub                    SmartHub              PrintController
   │                          │                           │                       │
   │─ SubmitPrintJob ─────────►│                           │                       │
   │  { documentName,          │                           │                       │
   │    content, copies }      │ 1. GenerateId()           │                       │
   │                          │    (10-char từ GUID)       │                       │
   │                          │ 2. SaveAsync(DB)           │                       │
   │                          │    Status = "pending"      │                       │
   │                          │ 3. ExecutePrintJob ────────►│                      │
   │◄─ PrintJobQueued ─────────│    { id, tenantId,        │                       │
   │   (job thêm vào pending)  │      docName, content,    │                       │
   │                          │      copies, connId,       │                       │
   │                          │      createdAt }           │                       │
   │                          │                           │ 4. PrintService        │
   │                          │                           │    .PrintDocumentAsync │
   │                          │                           │    (delay 1-3s)        │
   │                          │                           │                       │
   │                          │                           │─ POST /api/print/complete ►│
   │                          │                           │  { JobId, Success,    │   │
   │                          │                           │    Message }          │   │
   │                          │                           │                       │   │
   │                          │                           │                       │ 5. CompleteAsync(DB)
   │                          │                           │                       │    Status = "completed"
   │                          │                           │                       │
   │                          │                           │                       │ 6. ConnectionTracker
   │                          │                           │                       │    .GetConnectionIds(userId)
   │                          │                           │                       │
   │◄─ PrintJobComplete ───────────────────────────────────────────────────────────│
   │   (submitter's connections)                                                   │
   │                          │                           │                       │
   │   PrintJobStatusUpdate ──────────────────────────────────────────────────────►│
   │   (các client khác cùng tenant)                                               │
```

**Bước 6 — routing thông minh:**

```csharp
var submitterConnections = _tracker.GetConnectionIds(message.RequestorUserId);

if (submitterConnections.Count > 0)
{
    // User đang online: gửi đến TẤT CẢ tab của họ (có thể mở nhiều tab)
    Clients.Clients(submitterConnections) ← PrintJobComplete
    // Báo cho các user khác trong tenant
    Clients.GroupExcept("ui-TenantA", submitterConnections) ← PrintJobStatusUpdate
}
else
{
    // User offline: broadcast toàn tenant để họ thấy khi reconnect
    Clients.Group("ui-TenantA") ← PrintJobComplete
}
```

### 4.2 Concurrent submissions (n user cùng lúc)

Mỗi `SubmitPrintJob` là một hub invocation độc lập. Không có lock nào chặn.

```
User A → SubmitPrintJob → GenerateId=ABC → SaveAsync → ExecutePrintJob(ABC) → PrintJobQueued(ABC)
User B → SubmitPrintJob → GenerateId=XYZ → SaveAsync → ExecutePrintJob(XYZ) → PrintJobQueued(XYZ)
                                           ↕ song song
SmartHub nhận ABC và XYZ như 2 message riêng biệt
SmartHub không query DB — xử lý job từ payload SignalR trực tiếp
SemaphoreSlim(3,3): tối đa 3 job xử lý song song, phần còn lại xếp hàng
```

**SmartHub không query DB để lấy job.** Toàn bộ thông tin job được đóng gói trong `ExecutePrintJob` payload. DB chỉ được đọc khi SmartHub reconnect (xem mục 8).

---

## 5. Bank Transaction Pipeline

### 5.1 Cơ chế global lock

Bank là tài nguyên dùng chung toàn hệ thống — **chỉ một transaction tại một thời điểm**, không phân biệt tenant.

```csharp
// TransactionQueueService (Singleton)
private readonly SemaphoreSlim _globalLock = new(1, 1);  // chỉ 1 slot
private TransactionStatus? _current;                      // job đang xử lý
private readonly ConcurrentQueue<TransactionStatus> _history; // tối đa 50 entries
```

### 5.2 Happy path

```
Angular                    AuraHub              TransactionQueueService        SmartHub
   │                          │                           │                       │
   │─ SubmitTransaction ──────►│                           │                       │
   │  { description,           │─ TrySubmitAsync ──────────►│                      │
   │    amount, currency }      │                           │ 1. _globalLock.Wait(0)│
   │                          │                           │    → pass (0ms)       │
   │                          │                           │ 2. Tạo status "processing"
   │◄─ TransactionSubmitResult─│◄─ { id, "accepted", ...} │                       │
   │   { status: "accepted" }  │                           │ 3. SaveAsync(DB) [background]
   │                          │                           │ 4. BroadcastEvent "processing"
   │◄─ TransactionStatusChanged│◄─ ui-broadcast ───────────│                       │
   │◄─ BankStatus ─────────────│◄─ ui-broadcast ───────────│                       │
   │                          │                           │ 5. ExecuteTransaction ►│
   │                          │                           │                       │ 6. EftPosService
   │                          │                           │                       │    (delay 3-7s)
   │                          │                           │◄─ POST /api/txn/complete
   │                          │                           │   { transactionId,    │
   │                          │                           │     success, message }│
   │                          │                           │ 7. CompleteAsync(DB)  │
   │                          │                           │ 8. _current = null    │
   │                          │                           │ 9. _globalLock.Release│
   │◄─ TransactionStatusChanged│◄─ ui-broadcast ───────────│ 10. BroadcastEvent    │
   │◄─ BankStatus ─────────────│◄─ ui-broadcast ───────────│     "completed/failed"│
```

### 5.3 Khi bank bận (rejected)

```csharp
if (!_globalLock.Wait(0))  // 0ms = fail-fast, không chờ
{
    var reason = current != null
        ? $"Bank đang xử lý [{current.Id}] \"{current.Description}\""
        : "Bank đang bận.";

    return new TransactionSubmitResult(null, "rejected", reason, current);
}
```

Client nhận `status: "rejected"` ngay lập tức kèm thông tin job đang xử lý. Không có queue, không có retry tự động — client phải tự retry.

### 5.4 Fire-and-forget pattern

`SaveAndForwardAsync` được gọi với `_ =` (fire-and-forget) để response trả về ngay:

```csharp
// TrySubmitAsync — trả về "accepted" ngay sau khi acquire lock
_ = SaveAndForwardAsync(tenantId, id, request, connectionId);
return new TransactionSubmitResult(id, "accepted", ...);
```

Nếu `SaveAndForwardAsync` fail:
- `_current = null`
- `_globalLock.Release()` — lock được trả
- Broadcast `status: "failed"` đến ui-broadcast

### 5.5 Thứ tự release lock trước broadcast

```csharp
// CompleteTransactionAsync
lock (_currentSync) _current = null;
_globalLock.Release();              // ← release TRƯỚC
// ↑ Sau bước này, submission tiếp theo có thể acquire lock ngay
await BroadcastEventAsync(...);     // ← broadcast SAU
await BroadcastStatusAsync();
```

Lý do: để transaction tiếp theo không bị chặn khi broadcast đang gửi đến hàng trăm client.

---

## 6. Collaborative Document Pipeline

### 6.1 Cấu trúc lock

```csharp
// DocumentLockService — in-memory, Singleton
ConcurrentDictionary<string, FieldLockEntry> _locks
// Key: "docId:fieldId"  →  vd: "contract-001:buyer_name"

record FieldLockEntry(
    string DocId, string FieldId,
    string UserId, string UserName,
    string ConnectionId,   // dùng để release khi disconnect
    DateTime ExpiresAt     // TTL = 30 giây
);
```

### 6.2 Acquire lock

```
Angular (User A)           AuraHub              DocumentLockService    Angular (User B)
   │                          │                           │                  │
   │─ focus vào field ────────►│ (onFocus handler)         │                  │
   │─ AcquireFieldLock ────────►│                           │                  │
   │  (docId, fieldId)         │─ TryAcquire ──────────────►│                  │
   │                          │              AddOrUpdate:  │                  │
   │                          │              key không tồn tại? → thêm mới   │
   │                          │              key tồn tại:                     │
   │                          │                same userId? → overwrite (re-acquire)
   │                          │                đã expired?  → overwrite       │
   │                          │                người khác?  → từ chối        │
   │◄─ { acquired: true,  ────│◄─ LockAcquireResult ──────│                  │
   │     expiresAt }           │                           │                  │
   │ startHeartbeat(8s)        │─ FieldLocked ─────────────────────────────►│
   │                          │  (gửi đến doc-all, TRỪ caller)               │
```

### 6.3 Heartbeat — giữ lock sống

```
Client (mỗi 8 giây)        AuraHub              DocumentLockService
   │                          │                           │
   │─ HeartbeatFieldLock ─────►│                           │
   │  (docId, fieldId)         │─ Heartbeat ───────────────►│
   │                          │              Chỉ extend nếu:
   │                          │              1. Lock tồn tại
   │                          │              2. UserId khớp
   │                          │              → ExpiresAt = UtcNow + 30s
   │                          │              KHÔNG tạo lock mới nếu không tồn tại
```

**Tại sao không tạo mới?** Khi reconnect, connection cũ bị disconnect → `ReleaseAllByConnection(oldId)`. Nếu heartbeat tạo lock mới với `ConnectionId = newId` thì lock này không bao giờ bị release khi disconnect vì nó được tạo từ context của connection mới nhưng với data cũ.

### 6.4 Cleanup timer — xóa lock hết hạn

```csharp
// Chạy mỗi 5 giây (IHostedService)
private void CleanupExpired(object? state)
{
    foreach (var (key, entry) in _locks)
    {
        if (entry.ExpiresAt < now && _locks.TryRemove(key, out var removed))
        {
            expired.Add(removed.ToInfo());
        }
    }
    if (expired.Count > 0)
    {
        // Broadcast đến tất cả client trong doc-all
        _hub.Clients.Group("doc-all") ← FieldsExpiredUnlocked(expired)
    }
}
```

### 6.5 Release khi blur

```
Angular (User A)           AuraHub              DocumentLockService    Angular (User B)
   │                          │                           │                  │
   │─ blur khỏi field ────────►│ (onBlur handler)          │                  │
   │─ ReleaseFieldLock ────────►│                           │                  │
   │  (docId, fieldId)         │─ Release ─────────────────►│                  │
   │ stopHeartbeat             │              Kiểm tra userId khớp?            │
   │ claimedFields.delete      │              → TryRemove(key)                 │
   │                          │─ FieldUnlocked ───────────────────────────►│
   │                          │  (gửi đến TOÀN BỘ doc-all, kể cả caller)     │
```

### 6.6 Re-acquire sau reconnect (fix spam lock/unlock)

```
Network drops               DocumentService             AuraHub
   │                              │                         │
   │─ onreconnecting ─────────────►│                         │
   │                              │ stopAllHeartbeats()     │
   │                              │ emit 'reconnecting'     │
   │─ reconnected ────────────────►│                         │
   │                              │ NEW connectionId!       │
   │                              │─ onreconnected ─────────►│
   │                              │                         │ OnConnectedAsync:
   │                              │◄─ LockSnapshot ──────────│ (lock vẫn có vì
   │                              │   (có lock của user này) │ chưa timeout)
   │                              │                         │
   │                              │ reacquireClaimedFields()│
   │                              │─ AcquireFieldLock ───────►│
   │                              │   (cập nhật ConnectionId │ TryAcquire:
   │                              │    sang ID mới)          │ same userId → overwrite
   │                              │◄─ { acquired: true } ───│ lock.ConnectionId = newId
   │                              │ startHeartbeat() ✓      │
   │                              │                         │
   │ [sau đó] OLD conn disconnect                           │
   │                              │                         │ OnDisconnectedAsync(oldId):
   │                              │                         │ ReleaseAllByConnection(oldId)
   │                              │                         │ → lock có newId → KHÔNG match
   │                              │                         │ → KHÔNG release ✓
   │                              │                         │ → KHÔNG spam FieldUnlocked ✓
```

---

## 7. SmartHub Worker — Logic nội bộ

### 7.1 Startup sequence

```
HubConnectionWorker.ExecuteAsync(ct)
    │
    ├─ WaitForBackendAsync()
    │   └─ Poll GET {backendUrl}/health mỗi 3s
    │      (backoff 10s sau 5 lần đầu)
    │
    ├─ Load ServerConnections từ SQLite
    │
    └─ Task.WhenAll: ConnectAsync(conn) cho mỗi server
```

### 7.2 ConnectAsync — cơ chế retry

```
ConnectAsync(conn, ct)
    │
    ├─ Lấy access token từ IClientAuthenticationService
    │   (lưu trong SQLite, refresh tự động qua OAuth)
    │
    └─ ConnectWithTokenAsync(conn, token, ct)
        │
        ├─ BuildHubConnection (URL, token, retry policy)
        │
        ├─ Đăng ký handlers: hub.Closed, hub.Reconnecting, hub.Reconnected
        │
        ├─ FOR attempt = 1..5:
        │   ├─ hub.StartAsync(ct) → thành công? break
        │   └─ thất bại? log, delay 3s, thử lại
        │
        ├─ Nếu tất cả 5 lần thất bại → SetStatus("error")
        │
        └─ FetchPendingAsync() — lấy job chưa xử lý từ DB
```

### 7.3 Xử lý print job

```csharp
hub.On<PrintJobRequest>("ExecutePrintJob", async job =>
{
    // 1. Thêm vào UI list (Blazor page hiển thị)
    PendingPrintJobs.Add(job);
    StateChanged?.Invoke();  // trigger Blazor re-render

    // 2. Chờ semaphore (max 3 concurrent)
    await _printSemaphore.WaitAsync();
    try
    {
        // 3. PrintService.PrintDocumentAsync (mock: delay 1-3s)
        var (success, message) = await printSvc.PrintDocumentAsync(job, token);

        // 4. Báo kết quả cho backend qua HTTP (KHÔNG qua SignalR)
        await http.PostAsJsonAsync("api/print/complete",
            new { JobId = job.Id, Success = success, Message = message });
    }
    finally { _printSemaphore.Release(); }
});
```

**Tại sao báo kết quả qua HTTP thay vì SignalR?**
PrintController cần xử lý routing phức tạp (tìm user qua ConnectionTracker, phân biệt online/offline). SignalR hub method không có context của HTTP request và không thể dễ dàng làm điều này.

### 7.4 Token refresh reconnect

```csharp
_connectionEvents.TokenRefreshed += OnTokenRefreshed;

private void OnTokenRefreshed(object? sender, TokenRefreshEventArgs e)
{
    _ = Task.Run(async () =>
    {
        // 1. Đánh dấu disconnect là intentional (tránh Closed handler tự reconnect)
        DisconnectServer(e.ServerConnectionId);

        // 2. Kết nối lại với token mới
        await ConnectAsync(conn, CancellationToken.None);
    });
}
```

`_intentionallyDisconnected` là `HashSet<int>` để phân biệt: disconnect do token refresh (có chủ đích) vs disconnect do lỗi mạng (cần tự động reconnect).

---

## 8. Recovery khi mất kết nối

### 8.1 Angular — Print job recovery

```
Hub reconnected
    │
    └─ PrintHubService.onReconnected$.next(tenantId)
        │
        └─ MultiTenantComponent subscribe:
            │
            └─ syncJobs(tenantId, pendingJobIds[])
                │
                └─ hub.invoke("SyncPrintJobs", jobIds)
                    │
                    └─ AuraHub.SyncPrintJobs(jobIds[]):
                        FOR EACH jobId:
                            ├─ FindByRefAsync(jobId) từ DB
                            ├─ Status vẫn "pending"? → bỏ qua
                            └─ Status "completed/failed"?
                               → Clients.Caller ← PrintJobComplete
```

**Tại sao cần SyncPrintJobs?**
SmartHub có thể xử lý xong job trong khi Angular đang disconnected và chưa join lại `ui-{tenantId}` group. Nếu không có cơ chế này, `PrintJobComplete` đã bị gửi đến group lúc Angular không có mặt → mất event → job mắc kẹt "pending" mãi mãi.

### 8.2 SmartHub — Pending job recovery

```
FetchPendingAsync(conn, token)
    │
    ├─ GET /api/print/pending → danh sách job "pending" trong DB
    │   ├─ Filter: chỉ lấy job chưa có trong PendingPrintJobs (tránh duplicate)
    │   └─ Xử lý từng job: await processJobAsync (qua semaphore)
    │
    └─ GET /api/transaction/pending → danh sách txn "pending" trong DB
        ├─ Filter: chưa có trong ProcessingEftJobs
        └─ Xử lý từng txn: await processEftAsync
```

Được gọi tại 2 thời điểm:
1. Sau khi connect thành công lần đầu
2. Sau mỗi lần `hub.Reconnected`

### 8.3 Angular — Document lock recovery

Xem mục 6.6. Logic nằm trong `DocumentService.reacquireClaimedFields()`.

---

## 9. Persistence & Database

### 9.1 Messages table — unified storage

Cả print job và bank transaction đều lưu trong một bảng duy nhất, phân biệt bằng cột `Type`:

```
Id | Type        | Ref       | TenantId | Payload (JSON)              | Status    | ...
───┼─────────────┼───────────┼──────────┼─────────────────────────────┼───────────┤
1  | print_job   | AB3F7C1D2E| TenantA  | {"documentName":"Inv","...} | completed |
2  | bank_txn    | X9K2M4P7Q3| TenantB  | {"description":"Pay","...}  | pending   |
3  | print_job   | R1T5Y8U2W6| TenantA  | {"documentName":"Rpt","...} | failed    |
```

### 9.2 Lifecycle của một Message

```
Angular SubmitPrintJob
    → SaveAsync → Status = "pending"
                              ↓
SmartHub nhận ExecutePrintJob (từ SignalR payload, KHÔNG từ DB)
SmartHub xử lý
SmartHub POST /api/print/complete
    → CompleteAsync → Status = "completed" hoặc "failed"
                              ↓
Backend gửi PrintJobComplete đến Angular
```

DB là **nguồn sự thật duy nhất** cho trạng thái job. ConnectionId trong DB (`RequestorConnectionId`) là ID tại thời điểm submit — có thể stale sau reconnect. Vì vậy routing dùng `RequestorUserId` + `ConnectionTracker.GetConnectionIds()` thay vì connectionId trong DB.

### 9.3 Scoped vs Singleton — vấn đề lifetime

`IJobRepository` là Scoped (một instance per request), nhưng `TransactionQueueService` là Singleton. Singleton không thể inject Scoped trực tiếp:

```csharp
// TransactionQueueService (Singleton) KHÔNG inject IJobRepository trực tiếp
// Thay vào đó dùng IServiceScopeFactory:
using var scope = _scopeFactory.CreateScope();
var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
await repo.SaveAsync(...);
// scope bị dispose → DbContext bị dispose → connection trả về pool
```

### 9.4 Migration strategy

Backend chạy `db.Database.Migrate()` ngay khi startup với retry loop 5 lần (delay 3s):

```
Startup
   │
   ├─ Migrate() → thành công? → tiếp tục
   ├─ Fail + còn retry? → delay 3s → thử lại
   │  (SQL Server trong Docker cần ~20-30s để sẵn sàng)
   └─ Fail + hết retry + IsDevelopment?
       → EnsureDeleted() → Migrate()
       (reset DB trong dev để tránh migration conflict)
```

---

## Tóm tắt các điểm thiết kế quan trọng

| Quyết định | Lý do |
|---|---|
| Một hub duy nhất (`AuraHub`) cho 3 feature | Mỗi client chỉ cần 1 WebSocket connection, chia sẻ auth và group |
| SmartHub dùng 1 connection cho cả print + bank | Token `smarthub` được thêm vào cả 2 group tự động trong `OnConnectedAsync` |
| Bank dùng global lock thay vì per-tenant | Bank terminal là thiết bị vật lý dùng chung, không thể xử lý song song |
| `ExecutePrintJob` gửi trước `PrintJobQueued` | SmartHub cần bắt đầu xử lý sớm nhất có thể; Angular recover qua `SyncPrintJobs` khi cần |
| Báo kết quả qua HTTP thay vì SignalR | HTTP cho phép routing phức tạp (online/offline detection, multi-tab) trong controller context |
| `RequestorUserId` lưu trong DB | ConnectionId stale sau reconnect; UserId ổn định, dùng kết hợp với `ConnectionTracker` |
| Heartbeat 8s / TTL 30s | Lock tự expire khi tab crash hoặc mạng mất; 8s << 30s để có buffer cho latency |
| `stopAllHeartbeats` trong `onreconnecting` | Tránh zombie lock: heartbeat trên connection mới có thể re-tạo lock với connectionId rỗng |
| Re-acquire lock trong `onreconnected` | Cập nhật `connectionId` trong lock sang ID mới trước khi conn cũ disconnect và release |
