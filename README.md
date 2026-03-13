# POC AURA — SignalR Real-time Demo

> .NET 8 + Angular 19 + SignalR — 100% Docker

## Tech Stack

| Layer     | Technology           |
|-----------|----------------------|
| Backend   | .NET 8, ASP.NET Core SignalR |
| Frontend  | Angular 19, @microsoft/signalr |
| Container | Docker, Docker Compose |
| Proxy     | Nginx (prod)         |
| Dev Env   | WSL2 + VS Code Remote |

## Project Structure

```
POC-AURA/
├── backend/
│   ├── POC.AURA.Api/
│   │   ├── Hubs/ChatHub.cs        # SignalR Hub
│   │   ├── Models/ChatMessage.cs
│   │   └── Program.cs
│   ├── Dockerfile                  # Production
│   └── Dockerfile.dev              # Dev (dotnet watch)
├── frontend/
│   ├── src/app/
│   │   ├── core/services/signalr.service.ts
│   │   └── features/chat/         # Chat component
│   ├── nginx.conf                  # Nginx + reverse proxy
│   ├── Dockerfile                  # Production (nginx)
│   └── Dockerfile.dev              # Dev (ng serve)
├── .devcontainer/devcontainer.json
├── .vscode/
├── docker-compose.yml              # Production
└── docker-compose.dev.yml          # Development (hot reload)
```

## Quick Start

### Production (build & run)

```bash
docker compose up --build
```

- Frontend: http://localhost
- Backend API: http://localhost:5000
- Swagger: http://localhost:5000/swagger

### Development (hot reload)

```bash
docker compose -f docker-compose.dev.yml up --build
```

- Angular dev server: http://localhost:4200
- Backend: http://localhost:5000 (dotnet watch)

## VS Code Remote (WSL)

1. Mở WSL terminal, clone repo vào WSL filesystem:
   ```bash
   cd ~/projects
   git clone https://github.com/KhoiTraVinh/POC-AURA.git
   code POC-AURA
   ```
2. VS Code tự detect `.devcontainer/` → click **Reopen in Container**
3. Hoặc dùng **Remote - WSL** extension để mở thẳng WSL folder

## SignalR Hub — Events

| Event (Server → Client) | Mô tả                         |
|--------------------------|-------------------------------|
| `Connected`              | Trả về connectionId           |
| `MessageHistory`         | Lịch sử 100 tin nhắn gần nhất |
| `ReceiveMessage`         | Tin nhắn mới từ bất kỳ user   |
| `UserConnected`          | User mới kết nối              |
| `UserDisconnected`       | User ngắt kết nối             |
| `UserTyping`             | Trạng thái đang gõ            |

| Event (Client → Server) | Mô tả              |
|-------------------------|--------------------|
| `SendMessage`           | Gửi tin nhắn       |
| `SendTyping`            | Báo đang gõ        |
