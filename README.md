# POC AURA — SignalR Real-time Demo

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

## Project Structure

```
POC-AURA/
├── .devcontainer/
│   ├── Dockerfile              # Workspace image: .NET 8 + Node 22 + vsdbg
│   └── devcontainer.json       # VS Code Dev Containers config
├── .vscode/
│   ├── launch.json             # Debug: attach BE, Chrome FE, full-stack
│   ├── tasks.json              # Tasks: run BE, run FE, run full stack
│   ├── settings.json
│   └── extensions.json
├── backend/
│   ├── POC.AURA.Api/
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs
│   │   │   └── Migrations/
│   │   ├── Hubs/ChatHub.cs            # SignalR Hub
│   │   ├── Models/
│   │   │   ├── ChatMessage.cs         # In-memory model
│   │   │   └── ChatMessageEntity.cs   # EF Core entity
│   │   ├── Program.cs
│   │   └── appsettings.json
│   ├── Dockerfile                     # Production (multi-stage)
│   └── Dockerfile.dev                 # Standalone dev (dotnet watch + vsdbg)
├── frontend/
│   ├── src/app/
│   │   ├── core/services/signalr.service.ts
│   │   └── features/chat/
│   ├── nginx.conf                     # Nginx + reverse proxy + WebSocket
│   ├── Dockerfile                     # Production (nginx)
│   └── Dockerfile.dev                 # Standalone dev (ng serve)
├── docker-compose.yml                 # Production
└── docker-compose.dev.yml             # Development
```

## Quick Start

### Production

```bash
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

Cách này VS Code connect thẳng vào container `workspace` có sẵn cả **.NET 8 + Node 22**, code cả BE lẫn FE trong cùng 1 môi trường.

### Yêu cầu

- Docker Desktop đang chạy
- VS Code extension: **Dev Containers** (`ms-vscode-remote.remote-containers`)

### Bước 1: Mở project

Mở folder `POC-AURA` trong VS Code.

### Bước 2: Reopen in Container

VS Code tự detect `.devcontainer/` → popup **"Reopen in Container"** → click.

Hoặc: `F1` → `Dev Containers: Reopen in Container`

> Lần đầu build image mất vài phút. Các lần sau dùng cache.

### Bước 3: Chạy full stack

Sau khi vào container, mở Terminal (`Ctrl+```) — đang ở trong container, có sẵn `dotnet`, `node`, `ng`.

**Cách A — VS Code Task** (`Ctrl+Shift+B`):
```
run: full stack   ← start cả BE + FE cùng lúc
```

**Cách B — Terminal thủ công:**
```bash
# Terminal 1 — Backend (port 5000)
cd /workspace/backend
dotnet watch run --project POC.AURA.Api/POC.AURA.Api.csproj --urls http://0.0.0.0:5000

# Terminal 2 — Frontend (port 4200)
cd /workspace/frontend
npm start
```

### Ports được forward tự động

| Port | Service                        |
|------|--------------------------------|
| 4200 | Angular dev server (tự mở browser) |
| 5000 | Backend API + Swagger          |
| 8978 | CloudBeaver DB Manager         |
| 1433 | SQL Server                     |

### Services khởi động cùng devcontainer

| Container              | Vai trò                            |
|------------------------|------------------------------------|
| `poc-aura-workspace`   | VS Code attach vào đây (BE + FE)   |
| `poc-aura-sqlserver-dev` | SQL Server 2022                  |
| `poc-aura-cloudbeaver-dev` | Web DB manager                 |

---

## Development — Standalone (không dùng devcontainer)

Dùng khi không cần Dev Containers, chạy BE + FE trong container riêng:

```bash
docker compose -f docker-compose.dev.yml up --build --profile standalone
```

| Service     | URL                           |
|-------------|-------------------------------|
| Angular     | http://localhost:4200         |
| Backend API | http://localhost:5000         |
| CloudBeaver | http://localhost:8978         |
| SQL Server  | localhost:1433                |

---

## Kiến trúc Chat (REST + SignalR)

Dự án hiện sử dụng kiến trúc **API-First kết hợp SignalR Notification** (Tương tự Zalo/Messenger):
1. **Gửi tin nhắn**: Gọi REST API (`POST /api/messages`).
2. **Real-time**: SignalR Server chỉ đóng vai trò broadcast **Notification** (chỉ chứa `MessageId`) đến các Client trong Group.
3. **Hiển thị**: Client nhận notification -> Gọi `GET` API lấy riêng tin nhắn đó về để hiển thị.
4. **Offline/Disconnect**: Khi mạng rớt và có lại, Client gọi API lấy list tin nhắn bị *miss* trong khoảng thời gian mất mạng (dựa vào `afterMessageId`).

### SignalR Hub — Events (Hubs/chat)

| Event (Server → Client)  | Mô tả                                                           |
|--------------------------|-----------------------------------------------------------------|
| `NewMessageNotification` | Có tin nhắn mới trong Group (Payload: `MessageId`)              |
| `UserReadReceipt`        | Thông báo một user đã xem tin nhắn (Payload: `staffId`, `msgId`)|

| Event (Client → Server) | Mô tả                                         |
|-------------------------|-----------------------------------------------|
| `JoinGroup(groupId)`    | Tham gia vào một kênh chat cụ thể             |
| `LeaveGroup(groupId)`   | Rời khỏi kênh chat                            |

### REST API Endpoints

| HTTP Method | Endpoint               | Chức năng                               |
|-------------|------------------------|-----------------------------------------|
| `GET`       | `/api/messages/{id}`   | Lấy lịch sử tin nhắn (hỗ trợ `afterId`) |
| `POST`      | `/api/messages`        | Gửi tin nhắn mới                        |
| `POST`      | `/api/messages/read`   | Đánh dấu tin nhắn đã đọc (Read Receipt) |
