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

## Debug với VS Code

### Backend (.NET)

1. Đảm bảo đang trong Dev Container (container `workspace` đang chạy)
2. **Run & Debug** (`Ctrl+Shift+D`) → chọn `🔵 Backend: Attach (Docker)` → F5
3. Chọn process `POC.AURA.Api` trong popup
4. Đặt breakpoint trong file `.cs` → trigger từ browser

### Frontend (Angular)

- Chọn `🟠 Frontend: Chrome (localhost:4200)` → F5
- Đặt breakpoint trong file `.ts`

### Full Stack

- Chọn `🚀 Full Stack Debug` → F5

---

## Database — SQL Server 2022

| Thông tin | Giá trị                                              |
|-----------|------------------------------------------------------|
| Host      | `localhost` (từ máy) / `sqlserver` (trong Docker network) |
| Port      | `1433`                                               |
| Database  | `PocAuraDb`                                          |
| Username  | `sa`                                                 |
| Password  | `Aura@Poc2024!`                                      |

> Migration tự động chạy khi backend khởi động (`db.Database.Migrate()`).

### Tạo migration mới

```bash
# Chạy trong terminal của Dev Container
cd /workspace/backend
dotnet ef migrations add <TênMigration> --project POC.AURA.Api
```

---

## CloudBeaver — Web DB Manager

1. Truy cập http://localhost:8978
2. Lần đầu: tạo admin account theo wizard
3. Tạo connection mới → **SQL Server**:
   - Host: `sqlserver`
   - Port: `1433`
   - Database: `PocAuraDb`
   - Username: `sa`
   - Password: `Aura@Poc2024!`

---

## SignalR Hub — Events

| Event (Server → Client)  | Mô tả                          |
|--------------------------|--------------------------------|
| `Connected`              | Trả về connectionId            |
| `MessageHistory`         | Lịch sử 100 tin nhắn gần nhất  |
| `ReceiveMessage`         | Tin nhắn mới từ bất kỳ user    |
| `UserConnected`          | User mới kết nối               |
| `UserDisconnected`       | User ngắt kết nối              |
| `UserTyping`             | Trạng thái đang gõ             |

| Event (Client → Server) | Mô tả               |
|-------------------------|---------------------|
| `SendMessage`           | Gửi tin nhắn        |
| `SendTyping`            | Báo đang gõ         |
