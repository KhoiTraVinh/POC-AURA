# POC AURA — SignalR Real-time Demo

> .NET 8 + Angular 19 + SignalR + SQL Server — 100% Docker

## Tech Stack

| Layer       | Technology                          |
|-------------|-------------------------------------|
| Backend     | .NET 8, ASP.NET Core SignalR, EF Core 8 |
| Frontend    | Angular 19, @microsoft/signalr      |
| Database    | SQL Server 2022                     |
| DB Manager  | CloudBeaver (web UI)                |
| Container   | Docker, Docker Compose              |
| Proxy       | Nginx (prod)                        |
| Dev Env     | WSL2 + VS Code Remote               |

## Project Structure

```
POC-AURA/
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
│   │   ├── appsettings.json
│   │   └── appsettings.Development.json
│   ├── Dockerfile                     # Production (multi-stage)
│   └── Dockerfile.dev                 # Dev (dotnet watch + vsdbg)
├── frontend/
│   ├── src/app/
│   │   ├── core/services/signalr.service.ts
│   │   └── features/chat/
│   ├── nginx.conf                     # Nginx + reverse proxy + WebSocket
│   ├── Dockerfile                     # Production (nginx)
│   └── Dockerfile.dev                 # Dev (ng serve)
├── .devcontainer/devcontainer.json    # VS Code Remote Containers
├── .vscode/
│   ├── launch.json                    # Debug configs
│   ├── tasks.json                     # Docker tasks
│   ├── settings.json
│   └── extensions.json
├── docker-compose.yml                 # Production
└── docker-compose.dev.yml             # Development (hot reload)
```

## Quick Start

### Production

```bash
docker compose up --build
```

| Service      | URL                          |
|--------------|------------------------------|
| Frontend     | http://localhost             |
| Backend API  | http://localhost:5000        |
| Swagger      | http://localhost:5000/swagger |
| CloudBeaver  | http://localhost:8978        |
| SQL Server   | localhost:1433               |

### Development (hot reload + debug)

```bash
docker compose -f docker-compose.dev.yml up --build
```

| Service      | URL                          |
|--------------|------------------------------|
| Angular      | http://localhost:4200        |
| Backend API  | http://localhost:5000        |
| Swagger      | http://localhost:5000/swagger |
| CloudBeaver  | http://localhost:8978        |
| SQL Server   | localhost:1433               |

## Database — SQL Server 2022

| Thông tin    | Giá trị                      |
|--------------|------------------------------|
| Host         | `localhost` (hoặc `sqlserver` trong Docker network) |
| Port         | `1433`                       |
| Database     | `PocAuraDb`                  |
| Username     | `sa`                         |
| Password     | `Aura@Poc2024!`              |

> Migration tự động chạy khi backend khởi động (`db.Database.Migrate()`).

## CloudBeaver — Web DB Manager

1. Truy cập http://localhost:8978
2. Lần đầu: tạo admin account theo wizard
3. Tạo connection mới → **SQL Server**:
   - Host: `sqlserver` (tên service trong Docker)
   - Port: `1433`
   - Database: `PocAuraDb`
   - Username: `sa`
   - Password: `Aura@Poc2024!`

## VS Code Debug

### Backend (.NET)

1. Start containers: `Ctrl+Shift+P` → `Tasks: Run Task` → `docker: dev up (background)`
2. **Run & Debug** (`Ctrl+Shift+D`) → chọn `🔵 Backend: Attach (Docker)` → F5
3. Chọn process `POC.AURA.Api` trong popup
4. Đặt breakpoint trong `.cs` files → trigger từ browser

### Frontend (Angular)

- Chọn `🟠 Frontend: Chrome (localhost:4200)` → F5
- Đặt breakpoint trong `.ts` files

### Full Stack

- Chọn `🚀 Full Stack Debug` → F5

## VS Code Remote (WSL)

```bash
# Trong WSL terminal:
cd ~/projects
git clone https://github.com/KhoiTraVinh/POC-AURA.git
code POC-AURA
# VS Code tự detect .devcontainer/ → "Reopen in Container"
```

## EF Core — Tạo Migration mới

Khi thêm entity mới, chạy lệnh này (trong WSL hoặc dev container):

```bash
cd backend
dotnet ef migrations add <TênMigration> --project POC.AURA.Api
dotnet ef database update --project POC.AURA.Api
```

## SignalR Hub — Events

| Event (Server → Client) | Mô tả                          |
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
