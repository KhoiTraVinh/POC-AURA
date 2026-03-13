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

## Kiến trúc & Chiến lược Resilience

Dự án dùng mô hình **API-First + SignalR Signal-Only** để đảm bảo không mất dữ liệu khi mất kết nối:

```
[Client gửi message]
  POST /api/messages → lưu DB → Hub bắn tín hiệu "NewMessageNotification" (chỉ signal, không có data)

[Client nhận tín hiệu]
  GET /api/messages/{groupId}?afterMessageId={pointer}  ← lấy data từ API

[Token hết hạn / mất mạng → reconnect]
  GET /api/messages/receipt?groupId=&staffId=           ← lấy pointer bền vững từ server
  GET /api/messages/{groupId}?afterMessageId={pointer}  ← fetch messages bị miss

[Disconnect]
  Hub.OnDisconnectedAsync → tự động rời group → không nhận signal thừa
```

**Tại sao không gửi data qua SignalR?**
- Token hết hạn (10 phút) → connection bị ngắt → mất data nếu gửi qua SignalR
- Bằng cách chỉ dùng SignalR làm signal, client luôn có thể dùng API để đồng bộ lại sau reconnect

**Pointer (ReadReceipt)**
- `ReadReceipt.LastReadMessageId` lưu trên server — bền vững qua mọi reconnect
- Mỗi client (staffId) có pointer riêng → độc lập, không ảnh hưởng nhau

---

## Project Structure

```
POC-AURA/
├── .devcontainer/
│   ├── Dockerfile              # Workspace image: .NET 8 + Node 22 + vsdbg
│   └── devcontainer.json       # VS Code Dev Containers config
├── .vscode/
│   ├── launch.json             # Debug: Backend Launch, Attach, Chrome, Full Stack
│   ├── tasks.json              # Tasks: run BE, run FE, run full stack, docker
│   ├── settings.json
│   └── extensions.json
├── backend/
│   └── POC.AURA.Api/
│       ├── Controllers/
│       │   └── MessagesController.cs   # REST API (GET receipt, GET messages, POST, read)
│       ├── Data/AppDbContext.cs
│       ├── DTOs/MessageModels.cs
│       ├── Entities/                   # Message, Group, Member, ReadReceipt
│       ├── Hubs/ChatHub.cs             # SignalR Hub (auto-leave on disconnect)
│       └── Program.cs
├── frontend/
│   └── src/app/
│       ├── core/services/chat.service.ts   # SignalR + API + pointer logic
│       └── features/chat/                  # Chat UI với disconnect/reconnect simulation
├── docker-compose.yml          # Production
└── docker-compose.dev.yml      # Development (devcontainer + standalone)
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
| Frontend    | http://localhost              |
| Backend API | http://localhost:5000         |
| Swagger     | http://localhost:5000/swagger |
| CloudBeaver | http://localhost:8978         |
| SQL Server  | localhost:1433                |

---

## Development — VS Code Dev Containers (khuyến nghị)

Cách này VS Code connect thẳng vào container `workspace` có sẵn **.NET 8 + Node 22 + vsdbg**, code cả BE lẫn FE trong cùng 1 môi trường với đầy đủ IntelliSense và debug.

### Yêu cầu

- Docker Desktop đang chạy
- VS Code extension: **Dev Containers** (`ms-vscode-remote.remote-containers`)

### Bước 1: Clone và mở project

```bash
git clone <repo-url>
code POC-AURA
```

### Bước 2: Reopen in Container

VS Code tự detect `.devcontainer/` → popup **"Reopen in Container"** → click.

Hoặc: `F1` → `Dev Containers: Reopen in Container`

> Lần đầu build image mất ~5 phút. Các lần sau dùng cache.

### Bước 3: Chạy full stack

Sau khi vào container, terminal đang ở trong container — có sẵn `dotnet`, `node`, `ng`.

**Cách A — VS Code Task (khuyến nghị):**
```
Ctrl+Shift+B → chọn "run: full stack"
```
Sẽ mở 2 terminal song song: Backend (port 5000) + Frontend (port 4200).

**Cách B — Terminal thủ công:**
```bash
# Terminal 1 — Backend
cd /workspace/backend
dotnet watch run --project POC.AURA.Api/POC.AURA.Api.csproj --urls http://0.0.0.0:5000

# Terminal 2 — Frontend
cd /workspace/frontend
npm start
```

### Ports được forward tự động

| Port | Service                              |
|------|--------------------------------------|
| 4200 | Angular dev server (tự mở browser)   |
| 5000 | Backend API + Swagger                |
| 8978 | CloudBeaver DB Manager               |
| 1433 | SQL Server                           |

---

## Debug trong Dev Container

### Backend Debug

**Cách A — F5 Launch (khuyến nghị):**
1. Đảm bảo backend **chưa** chạy (hoặc dùng Attach thay thế)
2. `F5` → chọn **"🔵 Backend: Launch & Debug"**
3. VS Code tự build rồi launch với debugger attached
4. Đặt breakpoint bình thường trong file `.cs`

**Cách B — Attach vào `dotnet watch` (hot reload + debug cùng lúc):**
1. `Ctrl+Shift+B` → **"run: backend (dotnet watch)"** — để terminal chạy nền
2. `F5` → chọn **"🔵 Backend: Attach (process)"**
3. Chọn process `POC.AURA.Api` từ danh sách
4. Sửa code → lưu → `dotnet watch` tự reload, breakpoint vẫn giữ nguyên

### Frontend Debug

```
F5 → "🟠 Frontend: Chrome (localhost:4200)"
```
Breakpoint đặt thẳng trong file `.ts` — source map tự động map sang JS.

Hoặc dùng Edge: `F5` → **"🟠 Frontend: Edge (localhost:4200)"**

### Full Stack Debug (BE + FE cùng lúc)

```
F5 → "🚀 Full Stack Debug"
```
Launches cả Backend (với debugger) lẫn Chrome (với source map). Breakpoint trong cả `.cs` và `.ts` đều hoạt động.

---

## Development — Standalone (không dùng Dev Container)

Chạy BE + FE trong container riêng, không cần VS Code Dev Containers:

```bash
docker compose -f docker-compose.dev.yml up --build --profile standalone
```

| Service     | URL                           |
|-------------|-------------------------------|
| Angular     | http://localhost:4200         |
| Backend API | http://localhost:5000         |
| CloudBeaver | http://localhost:8978         |
| SQL Server  | localhost:1433                |

> **Lưu ý:** Chế độ standalone không hỗ trợ debug — chỉ dùng để xem sản phẩm trong môi trường dev mà không cần VS Code Dev Containers.

---

## SignalR Hub Events (`/hubs/chat`)

### Server → Client

| Event | Payload | Mô tả |
|---|---|---|
| `NewMessageNotification` | _(không có data)_ | Có message mới trong group — client tự gọi API lấy |
| `UserReadReceipt` | `{ staffId, messageId }` | Client khác đã cập nhật pointer |

### Client → Server

| Method | Tham số | Mô tả |
|---|---|---|
| `JoinGroup` | `groupId: int` | Tham gia group để nhận signal |
| `LeaveGroup` | `groupId: int` | Rời group (gọi khi stopConnection) |

> **Auto-leave:** Khi connection bị đóng bất kỳ lý do gì (token hết hạn, mất mạng, đóng tab, crash), Hub tự động remove connection khỏi group qua `OnDisconnectedAsync`.

---

## REST API Endpoints

| Method | Endpoint | Mô tả |
|---|---|---|
| `GET` | `/api/messages/{groupId}?afterMessageId=` | Lấy messages mới hơn pointer |
| `GET` | `/api/messages/receipt?groupId=&staffId=` | Lấy pointer hiện tại (LastReadMessageId) |
| `POST` | `/api/messages` | Tạo message mới → signal tới group |
| `POST` | `/api/messages/read` | Cập nhật pointer (LastReadMessageId) |
| `GET` | `/health` | Health check |

---

## Hướng dẫn sử dụng

1. Mở `http://localhost:4200` (dev) hoặc `http://localhost` (prod)
2. Nhập **Username**, **Staff ID** (mỗi tab chọn số khác nhau), **Group ID**
3. Click **Join Chat**
4. Gửi tin nhắn bằng cách nhập và nhấn **Enter** (hoặc click nút gửi)
5. Để mô phỏng nhiều clients: **mở nhiều tab** với cùng Group ID

**Nút mô phỏng (chỉ hiện sau khi join):**
- **"Ngắt kết nối"** — mô phỏng token hết hạn hoặc mất mạng
- **"Kết nối lại"** — reconnect và tự động đồng bộ messages bị miss

---

## Test Cases — Mô phỏng Reconnect

### Case 1 — Happy path
1. **Tab A**: join groupId=1, staffId=1
2. **Tab B**: join groupId=1, staffId=2
3. Tab B gửi 3 messages

**Verify:** Tab A nhận đủ 3 messages realtime ✓

---

### Case 2 — Reconnect sau mất kết nối ⭐ (case chính)
1. Tab A và Tab B cùng join groupId=1
2. **Tab A nhấn "Ngắt kết nối"** (status → 🔴 Disconnected)
3. Tab B gửi 4 messages trong khi Tab A offline
4. **Tab A nhấn "Kết nối lại"**

**Verify:**
- Banner **"⟳ Đang đồng bộ 4 tin nhắn bị miss..."** xuất hiện ✓
- Tab A hiển thị đủ 4 messages sau khi reconnect ✓
- DB: `ReadReceipts` → `LastReadMessageId` của staffId=1 cập nhật đúng ✓

---

### Case 3 — Nhiều client độc lập
1. Mở 3 tab, cùng groupId=1, staffId lần lượt 1, 2, 3
2. Tab 2 nhấn "Ngắt kết nối"
3. Tab 1 gửi 2 messages
4. Tab 2 nhấn "Kết nối lại"

**Verify:**
- Tab 2 đồng bộ đúng 2 messages bị miss ✓
- Tab 3 không bị ảnh hưởng, pointer của staffId=3 độc lập ✓

---

### Case 4 — Auto leave group khi đóng tab
1. Tab A join groupId=1
2. **Đóng hoàn toàn Tab A** (Ctrl+W hoặc đóng cửa sổ)
3. Tab B gửi 1 message

**Verify:** Không có lỗi "connection not in group" trong server logs ✓

---

## Useful Docker Commands

```bash
# Xem logs backend
docker compose logs -f backend

# Xem logs frontend
docker compose logs -f frontend

# Dừng tất cả
docker compose down
docker compose -f docker-compose.dev.yml down

# Reset database (xóa volume)
docker compose down -v
```
