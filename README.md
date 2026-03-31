# POC AURA — Resilient SignalR + REST API Demo

> .NET 8 + Angular 19 + SignalR + SQL Server — 100% Docker

## Tech Stack

| Layer       | Technology                              |
|-------------|-----------------------------------------|
| Backend     | .NET 8, ASP.NET Core SignalR, EF Core 8 |
| Frontend    | Angular 19, @microsoft/signalr          |
| Database    | SQL Server 2022                         |
| DB Manager  | CloudBeaver (web UI)                    |
| Container   | Docker, Docker Compose                  |
| Proxy       | Nginx (prod)                            |
| Dev Env     | VS Code Dev Containers (full-stack)     |

---

## Tổng quan 4 Use Cases

POC này demo 4 pattern SignalR thực tế, mỗi cái giải quyết một bài toán khác nhau:

| # | Route | Hub | Vấn đề cốt lõi |
|---|-------|-----|----------------|
| 0 | `/chat` | `ChatHub` | Resilient chat — signal-only, no data qua SignalR, reconnect đồng bộ pointer |
| 1 | `/multi-tenant` | `PrintHub` | Tenant isolation — JWT claim `tenant_id`, Groups tách biệt hoàn toàn |
| 2 | `/transaction` | `TransactionHub` | Fail-fast lock — `SemaphoreSlim(1,1) + Wait(0)`, reject ngay nếu bận |
| 3 | `/collab-doc` | `DocumentHub` | Pessimistic field lock — TTL 30s + heartbeat + auto-release on disconnect |

---

## Project Structure

```
POC-AURA/
├── .devcontainer/
│   ├── Dockerfile              # Workspace image: .NET 8 + Node 22 + vsdbg
│   └── devcontainer.json       # VS Code Dev Containers config
├── backend/
│   └── POC.AURA.Api/
│       ├── Auth/JwtService.cs              # JWT access+refresh token (10min/24h)
│       ├── Controllers/
│       │   ├── AuthController.cs           # POST /api/auth/token, /refresh
│       │   └── MessagesController.cs       # Chat REST API
│       ├── Hubs/
│       │   ├── ChatHub.cs                  # Signal-only hub, auto-leave on disconnect
│       │   ├── PrintHub.cs                 # [Authorize] multi-tenant hub
│       │   ├── TransactionHub.cs           # Fail-fast lock hub
│       │   └── DocumentHub.cs             # Field-level locking hub
│       ├── Models/                         # PrintModels, TransactionModels, DocumentModels
│       ├── Services/
│       │   ├── TransactionQueueService.cs  # SemaphoreSlim fail-fast
│       │   └── DocumentLockService.cs      # ConcurrentDict + TTL + background cleanup
│       └── Program.cs
├── frontend/src/app/
│   ├── core/services/
│   │   ├── auth.service.ts                 # Token cache + auto-refresh
│   │   ├── print-hub.service.ts            # Multi-connection manager per tenant
│   │   ├── transaction.service.ts          # Bank status stream
│   │   └── document.service.ts             # Lock state + heartbeat timer
│   └── features/
│       ├── chat/                           # Chat UI
│       ├── multi-tenant/                   # Tenant isolation demo
│       ├── transaction-queue/              # Fail-fast bank demo
│       └── collaborative-doc/              # Field-lock editor
```

---

## Quick Start — Xem sản phẩm (Production)

```bash
git clone <repo-url>
cd POC-AURA
docker compose up --build
```

| Service     | URL                           |
|-------------|-------------------------------|
| Frontend    | http://localhost:4200         |
| Backend API | http://localhost:5000         |
| Swagger     | http://localhost:5000/swagger |
| CloudBeaver | http://localhost:8978         |
| SQL Server  | localhost:1433                |

---

## Development — VS Code Dev Containers (khuyến nghị)

### Yêu cầu

- Docker Desktop đang chạy
- VS Code extension: **Dev Containers** (`ms-vscode-remote.remote-containers`)

### Các bước

```bash
git clone <repo-url>
code POC-AURA
# VS Code popup → "Reopen in Container"
# Hoặc F1 → Dev Containers: Reopen in Container
```

**Chạy full stack:**
```
Ctrl+Shift+B → "run: full stack"
```

| Port | Service |
|------|---------|
| 4200 | Angular dev server |
| 5000 | Backend API + Swagger |
| 8978 | CloudBeaver |
| 1433 | SQL Server |

**Debug:**
- `F5` → **"🔵 Backend: Launch & Debug"** — breakpoint trong `.cs`
- `F5` → **"🟠 Frontend: Chrome"** — breakpoint trong `.ts`
- `F5` → **"🚀 Full Stack Debug"** — cả hai cùng lúc

---

## Use Case 0: Chat Resilient (Signal-Only Pattern)

### Kiến trúc

```
[Client gửi message]
  POST /api/messages → lưu DB → Hub signal "NewMessageNotification" (không có data)

[Client nhận signal]
  GET /api/messages/{groupId}?afterMessageId={pointer}

[Reconnect sau mất mạng]
  GET /api/messages/receipt?groupId=&staffId=   ← pointer bền vững từ server
  GET /api/messages/{groupId}?afterMessageId=   ← fetch messages bị miss
```

**Tại sao không gửi data qua SignalR?**
- Token hết hạn (10 phút) → connection bị ngắt → mất data nếu gửi qua hub
- Signal-only: client luôn dùng API để đồng bộ lại sau reconnect
- `ReadReceipt.LastReadMessageId` lưu server — bền vững qua mọi reconnect

### REST API Endpoints (Chat)

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| `GET`  | `/api/messages/{groupId}?afterMessageId=` | Lấy messages mới hơn pointer |
| `GET`  | `/api/messages/receipt?groupId=&staffId=` | Lấy pointer hiện tại |
| `POST` | `/api/messages` | Tạo message → signal tới group |
| `POST` | `/api/messages/read` | Cập nhật pointer |

### SignalR Events (ChatHub `/hubs/chat`)

**Server → Client:**

| Event | Payload | Mô tả |
|-------|---------|-------|
| `NewMessageNotification` | _(không có data)_ | Có message mới — client tự gọi API |
| `UserReadReceipt` | `{ staffId, messageId }` | Client khác đã cập nhật pointer |

**Client → Server:**

| Method | Tham số | Mô tả |
|--------|---------|-------|
| `JoinGroup` | `groupId: int` | Tham gia group |
| `LeaveGroup` | `groupId: int` | Rời group |

### Test Cases

**Case 1 — Happy path**
1. Tab A: join groupId=1, staffId=1 — Tab B: join groupId=1, staffId=2
2. Tab B gửi 3 messages
3. ✓ Tab A nhận đủ 3 messages realtime

**Case 2 — Reconnect sau mất kết nối ⭐**
1. Cả 2 tab join groupId=1
2. Tab A nhấn **"Ngắt kết nối"**
3. Tab B gửi 4 messages
4. Tab A nhấn **"Kết nối lại"**
5. ✓ Banner "⟳ Đang đồng bộ 4 tin nhắn bị miss..." xuất hiện
6. ✓ Tab A hiển thị đủ 4 messages sau reconnect

---

## Use Case 1: Multi-Tenant Print Hub

### Kiến trúc

```
[Angular UI - TenantA]  →  POST /api/auth/token { tenantId: "A", clientType: "ui" }
                         →  JWT { tenant_id: "A", client_type: "ui" }
                         →  Connect /hubs/print (Bearer token)
                         →  Server: Groups.Add(connectionId, "ui-A")

[SmartHub - TenantA]    →  POST /api/auth/token { tenantId: "A", clientType: "smarthub" }
                         →  Connect /hubs/print
                         →  Server: Groups.Add(connectionId, "smarthub-A")

[Angular UI gửi job]
  Invoke "SubmitPrintJob" → Server route tới "smarthub-A" (KHÔNG đến smarthub-B)

[SmartHub hoàn thành]
  Invoke "ReportPrintJobComplete" → Server route về connectionId gốc + broadcast "ui-A"
```

**Tenant isolation đảm bảo bởi:**
- JWT `tenant_id` claim — server tự đọc, client không thể giả mạo
- Group names: `ui-{tenantId}` và `smarthub-{tenantId}` — tách biệt hoàn toàn
- `[Authorize]` attribute trên hub — unauthenticated request bị reject ở tầng middleware

### Auth Endpoints

| Method | Endpoint | Body |
|--------|----------|------|
| `POST` | `/api/auth/token` | `{ tenantId, clientType, userName }` |
| `POST` | `/api/auth/refresh` | `{ refreshToken }` |

Token: **access 10 phút**, **refresh 24 giờ**. Frontend tự refresh khi còn < 60s.

### SignalR Events (PrintHub `/hubs/print`)

**Client → Server:**

| Method | Tham số | Mô tả |
|--------|---------|-------|
| `SubmitPrintJob` | `PrintJobRequest` | UI gửi job → route tới `smarthub-{tenant}` |
| `ReportPrintJobComplete` | `PrintJobResult` | SmartHub báo cáo kết quả |

**Server → Client:**

| Event | Nhận bởi | Mô tả |
|-------|----------|-------|
| `ExecutePrintJob` | `smarthub-{tenantId}` | Job cần thực thi |
| `PrintJobQueued` | Caller (UI) | Xác nhận job đã gửi tới SmartHub |
| `PrintJobComplete` | requestorConnectionId | Kết quả từ SmartHub |
| `PrintJobStatusUpdate` | `ui-{tenantId}` | Broadcast status cho tất cả UI cùng tenant |
| `ClientConnected` | `ui-{tenantId}` | Thông báo có client kết nối mới |
| `ClientDisconnected` | `ui-{tenantId}` | Thông báo client rời |

### Test Cases

**Case 1 — Tenant isolation**
1. Mở `/multi-tenant` — connect cả TenantA và TenantB
2. TenantA gửi print job
3. ✓ SmartHub TenantA nhận job, SmartHub TenantB **không nhận**
4. SmartHub TenantA click "Done"
5. ✓ Kết quả chỉ về UI TenantA, TenantB không bị ảnh hưởng

**Case 2 — Job flow hoàn chỉnh**
1. Connect TenantA (UI + SmartHub)
2. Nhập document name + content → "Send Print Job"
3. ✓ Activity log: `[UI] Queued → SmartHub` rồi `[SmartHub] Received job`
4. SmartHub click "✓ Done" hoặc "✗ Fail"
5. ✓ `[UI] Job #xxx done/FAILED` xuất hiện trong log

---

## Use Case 2: Sequential Bank Transaction (Fail-Fast)

### Kiến trúc

```
[Client submit transaction]
  Invoke "SubmitTransaction" → TransactionQueueService.TrySubmit()

[TrySubmit logic]
  _bankLock.Wait(0)  ← non-blocking try-acquire (SemaphoreSlim(1,1))

  ├─ true  (bank free):  spawn RunTransactionAsync (fire-and-forget), return "accepted"
  └─ false (bank busy):  return "rejected" ngay lập tức — NO queuing, NO waiting

[RunTransactionAsync]
  Signal "processing" → sleep 3-7s → Signal "completed/failed" → release lock
  Sau mỗi state change: broadcast "BankStatus" tới ALL clients
```

**Tại sao Fail-Fast thay vì Queue?**

| Approach | Cơ chế | Rủi ro |
|----------|--------|--------|
| `WaitAsync(ct)` | Task chờ trong semaphore's internal waiter list | Memory leak — tasks pile up |
| `Wait(0)` ← dùng | Trả về false ngay nếu bận | **Không có task nào bị treo** |

Tương đương DB: `SELECT FOR UPDATE NOWAIT` (Oracle) / `FOR UPDATE SKIP LOCKED` (PostgreSQL)

### SignalR Events (TransactionHub `/hubs/transaction`)

**Client → Server:**

| Method | Tham số | Mô tả |
|--------|---------|-------|
| `SubmitTransaction` | `{ description, amount, currency }` | Try-submit, return kết quả ngay |

**Server → Client (broadcast ALL):**

| Event | Payload | Mô tả |
|-------|---------|-------|
| `BankStatus` | `TransactionHistoryStatus` | Trạng thái bank + history |
| `TransactionStatusChanged` | `{ id, state, message }` | processing / completed / failed |

### Test Cases

**Case 1 — Sequential processing**
1. Mở `/transaction` — status "FREE — Ready"
2. Submit 1 transaction → ✓ "Accepted", bank chuyển sang "BUSY — Processing"
3. Submit thêm transaction trong khi bận
4. ✓ "Rejected" ngay lập tức, message hiện ai đang block
5. Sau 3-7s: ✓ Bank "FREE" lại, history cập nhật

**Case 2 — Multi-client isolation**
1. Mở 2 tab `/transaction`
2. Tab A submit → bank BUSY
3. ✓ Tab B cũng thấy bank BUSY ngay (realtime broadcast)
4. Sau khi xong: ✓ Cả 2 tab thấy bank FREE cùng lúc

---

## Use Case 3: Collaborative Document (Field-Level Lock)

### Kiến trúc

```
[User focus vào field]
  Invoke "AcquireFieldLock"(docId, fieldId)

  Server: ConcurrentDictionary.AddOrUpdate
  ├─ Acquired:  broadcast "FieldLocked" tới tất cả OTHERS
  └─ Rejected:  return currentHolder, không thay đổi gì

[User đang edit — heartbeat mỗi 8s]
  Invoke "HeartbeatFieldLock" → gia hạn TTL thêm 30s

[User blur (rời field)]
  Invoke "ReleaseFieldLock" → broadcast "FieldUnlocked"

[User disconnect]
  OnDisconnectedAsync → auto-release ALL locks của connection đó

[Background cleanup timer (5s interval)]
  Quét locks hết TTL → broadcast "FieldsExpiredUnlocked" tới ALL
```

**Lock entry:** `ConcurrentDictionary<"docId:fieldId", FieldLockEntry>`
- TTL: **30 giây** (gia hạn bởi heartbeat)
- Cleanup: background timer 5s
- Auto-release: OnDisconnectedAsync

### SignalR Events (DocumentHub `/hubs/document`)

**Client → Server (invoked):**

| Method | Tham số | Returns |
|--------|---------|---------|
| `AcquireFieldLock` | `(docId, fieldId)` | `LockAcquireResult { acquired, expiresAt, currentHolder }` |
| `ReleaseFieldLock` | `(docId, fieldId)` | void |
| `HeartbeatFieldLock` | `(docId, fieldId)` | void |
| `UpdateFieldValue` | `(docId, fieldId, value)` | void (throws nếu không phải lock holder) |

**Server → Client:**

| Event | Nhận bởi | Payload |
|-------|----------|---------|
| `LockSnapshot` | Caller (on connect) | `FieldLockInfo[]` — toàn bộ locks hiện tại |
| `FieldLocked` | Others | `{ docId, fieldId, userId, userName, expiresAt }` |
| `FieldUnlocked` | Others | `{ docId, fieldId }` |
| `FieldValueChanged` | Others | `{ docId, fieldId, value, userId, userName }` |
| `FieldsExpiredUnlocked` | All | `FieldLockInfo[]` — locks vừa hết TTL |

### Test Cases

**Case 1 — Lock + edit realtime**
1. Mở 2 tab `/collab-doc` với tên khác nhau (Alice, Bob)
2. Alice click vào field "Tên bên mua"
3. ✓ Bob thấy field đó hiển thị "🔒 Alice đang chỉnh sửa..." và bị disabled
4. Alice gõ text
5. ✓ Bob thấy text cập nhật realtime
6. Alice blur (click ra ngoài)
7. ✓ Field mở khóa, Bob có thể edit

**Case 2 — Lock TTL tự expire**
1. Alice click vào field để lock
2. Alice **không gõ gì** (không có heartbeat)
3. Sau 30s: ✓ lock tự expire, broadcast "FieldsExpiredUnlocked", Bob có thể edit

**Case 3 — Disconnect auto-release**
1. Alice lock nhiều fields
2. Alice nhấn "Rời khỏi" (hoặc đóng tab)
3. ✓ Tất cả locks của Alice tự giải phóng ngay lập tức
4. ✓ Bob thấy tất cả fields mở khóa

**Case 4 — Concurrent lock attempt**
1. Alice đang lock field "Địa chỉ"
2. Bob click vào field "Địa chỉ"
3. ✓ Server trả về `acquired: false`, Bob không chiếm được lock
4. Alice blur → Bob click lại → ✓ Bob acquire thành công

---

## Useful Docker Commands

```bash
# Xem logs backend
docker compose logs -f backend

# Xem logs frontend
docker compose logs -f frontend

# Dừng tất cả
docker compose down

# Reset database (xóa volume)
docker compose down -v
```
