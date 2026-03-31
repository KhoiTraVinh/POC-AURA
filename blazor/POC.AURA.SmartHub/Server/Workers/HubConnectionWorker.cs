using POC.AURA.SmartHub.Common;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Data.Entities;
using POC.AURA.SmartHub.Service.Auth;
using POC.AURA.SmartHub.Service.Events;
using POC.AURA.SmartHub.Server.Hubs;
using POC.AURA.SmartHub.Server.Services;

namespace POC.AURA.SmartHub.Server.Workers;

/// <summary>
/// Background service that maintains one <see cref="HubConnection"/> per
/// <see cref="ServerConnection"/> in the database.
///
/// Responsibilities (per spec):
/// - Load all ServerConnections from DB on startup, connect to each Aura server's SignalR hub
/// - Retry on failure (5 attempts × 3-second gap; then exponential backoff)
/// - Reconnect automatically when a token is refreshed (via IConnectionEventService.TokenRefreshed)
/// - Receive print jobs → delegate to IPrintService (max 3 concurrent via SemaphoreSlim)
/// - Receive EFT transactions → delegate to IEftPosService
/// - Broadcast job status back to Blazor UI via IHubContext&lt;BlazorConnectionHub&gt;
/// - Clean up temp files after printing (production only)
/// - Validate EshHubFeaturePrinting.READ feature flag before processing (production only)
/// </summary>
public class HubConnectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionEventService _connectionEvents;
    private readonly IHubContext<BlazorConnectionHub> _uiHub;
    private readonly ILogger<HubConnectionWorker> _logger;

    // Per-connection state
    private readonly Dictionary<int, HubConnection> _connections = new();
    private readonly HashSet<int> _intentionallyDisconnected = new(); // prevents Closed handler from reconnecting after an explicit DisconnectServer call
    private readonly SemaphoreSlim _printSemaphore = new(3, 3); // max 3 concurrent print jobs
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    // Observable state for Blazor UI (direct injection fallback in POC)
    public Dictionary<int, (string ServerName, string Status, string TenantId)> ConnectionStatuses { get; } = new();
    public List<PrintJobRequest> PendingPrintJobs { get; } = [];
    public List<(PrintJobResult Result, DateTime Time)> CompletedPrintJobs { get; } = [];
    public List<EftPosRequest> ProcessingEftJobs { get; } = [];
    public List<(EftPosResult Result, DateTime Time)> CompletedEftJobs { get; } = [];
    public List<(string Level, string Message, DateTime Time)> Logs { get; } = [];
    public event Action? StateChanged;

    public HubConnectionWorker(
        IServiceScopeFactory scopeFactory,
        IConnectionEventService connectionEvents,
        IHubContext<BlazorConnectionHub> uiHub,
        ILogger<HubConnectionWorker> logger)
    {
        _scopeFactory      = scopeFactory;
        _connectionEvents  = connectionEvents;
        _uiHub             = uiHub;
        _logger            = logger;

        // Reconnect to Aura server when token is refreshed
        _connectionEvents.TokenRefreshed += OnTokenRefreshed;
    }

    // ── BackgroundService ─────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for backend to be ready, then load connections from DB
        await WaitForBackendAsync(ct);
        if (ct.IsCancellationRequested) return;

        List<ServerConnection> connections;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IServerConnectionRepository>();
            connections = await repo.GetAllAsync();
        }

        if (connections.Count == 0)
        {
            _logger.LogInformation("No server connections in DB — waiting for user to add via UI");
            return;
        }

        _logger.LogInformation("Connecting to {Count} Aura server(s)…", connections.Count);
        await Task.WhenAll(connections.Select(c => ConnectAsync(c, ct)));
    }

    // ── Public API (called by ServerConnectionService when a new server is added) ──

    public async Task ConnectServerAsync(ServerConnection conn, CancellationToken ct = default)
        => await ConnectAsync(conn, ct);

    public void DisconnectServer(int connectionId)
    {
        HubConnection? hub;
        lock (_lock)
        {
            _connections.TryGetValue(connectionId, out hub);
            _connections.Remove(connectionId);
            ConnectionStatuses.Remove(connectionId);
            _intentionallyDisconnected.Add(connectionId); // signal Closed handler to skip auto-reconnect
        }
        _ = hub?.DisposeAsync();
        StateChanged?.Invoke();
    }

    // ── Connection lifecycle ──────────────────────────────────────────────

    private async Task ConnectAsync(ServerConnection conn, CancellationToken ct)
    {
        SetStatus(conn, "connecting");

        string? token = null;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<IClientAuthenticationService>();
            token = await auth.GetAccessTokenAsync(conn.Id);
        }

        if (token is null)
        {
            SetStatus(conn, "error");
            AddLog("error", $"[{conn.ServerName}] No token — authenticate via UI first");
            return;
        }

        await ConnectWithTokenAsync(conn, token, ct);
    }

    private async Task ConnectWithTokenAsync(ServerConnection conn, string token, CancellationToken ct)
    {
        var hub = BuildHubConnection(conn, token);

        hub.Reconnecting += _ => { SetStatus(conn, "reconnecting"); return Task.CompletedTask; };
        hub.Reconnected  += async _ => { SetStatus(conn, "connected"); await FetchPendingAsync(conn, token); };
        hub.Closed       += async _ =>
        {
            // If DisconnectServer was called intentionally (e.g. token refresh reconnect),
            // skip auto-reconnect — the caller handles reconnection itself.
            lock (_lock)
            {
                if (_intentionallyDisconnected.Remove(conn.Id)) return;
            }
            SetStatus(conn, "disconnected");
            await Task.Delay(5_000, ct);
            if (!ct.IsCancellationRequested) await ConnectAsync(conn, ct);
        };

        RegisterHandlers(hub, conn, token);

        // Retry 5 times × 3 s before giving up (per spec)
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await hub.StartAsync(ct);
                lock (_lock) _connections[conn.Id] = hub;
                SetStatus(conn, "connected");
                AddLog("success", $"[{conn.ServerName}/{conn.TenantId}] Connected");
                await FetchPendingAsync(conn, token);
                return;
            }
            catch (Exception ex) when (attempt < 5)
            {
                AddLog("warn", $"[{conn.ServerName}] Connect attempt {attempt}/5 failed: {ex.Message}");
                await Task.Delay(3_000, ct);
            }
            catch (Exception ex)
            {
                SetStatus(conn, "error");
                AddLog("error", $"[{conn.ServerName}] All connection attempts failed: {ex.Message}");
                _logger.LogError(ex, "HubConnectionWorker failed to connect to {Name}", conn.ServerName);
            }
        }
    }

    private void RegisterHandlers(HubConnection hub, ServerConnection conn, string token)
    {
        // Print jobs
        hub.On<PrintJobRequest>("ExecutePrintJob", async job =>
        {
            var enriched = job with { ConnectionId = conn.Id };
            lock (_lock) PendingPrintJobs.Add(enriched);
            AddLog("info", $"[{conn.ServerName}] [PRINT IN] #{job.Id} \"{job.DocumentName}\" ×{job.Copies}");
            StateChanged?.Invoke();

            await _printSemaphore.WaitAsync();
            try { await ProcessPrintJobAsync(enriched, conn, token); }
            finally { _printSemaphore.Release(); }
        });

        // EFT/POS transactions
        hub.On<EftPosRequest>("ExecuteTransaction", async job =>
        {
            var enriched = job with { ConnectionId = conn.Id };
            lock (_lock) ProcessingEftJobs.Add(enriched);
            AddLog("info", $"[{conn.ServerName}] [EFT IN] TXN-{job.TransactionId} {job.Amount:N0} {job.Currency}");
            StateChanged?.Invoke();
            await ProcessEftJobAsync(enriched, conn, token);
        });
    }

    // ── Print job processing ──────────────────────────────────────────────

    private async Task ProcessPrintJobAsync(PrintJobRequest job, ServerConnection conn, string token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var printSvc = scope.ServiceProvider.GetRequiredService<IPrintService>();

        var (success, message) = await printSvc.PrintDocumentAsync(job, token);

        try
        {
            using var http = CreateHttpClient(token, conn.NormalizedUrl);
            var resp = await http.PostAsJsonAsync("api/print/complete",
                new { JobId = job.Id, Success = success, Message = message });
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{conn.ServerName}] Report print failed #{job.Id}: {ex.Message}");
        }

        var result = new PrintJobResult(job.Id, job.TenantId, job.RequestorConnectionId,
            success, message, DateTime.UtcNow);

        lock (_lock)
        {
            PendingPrintJobs.Remove(job);
            CompletedPrintJobs.Add((result, DateTime.Now));
            if (CompletedPrintJobs.Count > 50) CompletedPrintJobs.RemoveAt(0);
        }

        AddLog(success ? "success" : "error",
            $"[{conn.ServerName}] [PRINT OUT] #{job.Id} {(success ? "Done ✓" : "Failed ✗")}");

        // Broadcast job update to UI
        await _uiHub.Clients.All.SendAsync("JobStatusUpdate", result);

        StateChanged?.Invoke();
    }

    // ── EFT job processing ────────────────────────────────────────────────

    private async Task ProcessEftJobAsync(EftPosRequest job, ServerConnection conn, string token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var eftSvc = scope.ServiceProvider.GetRequiredService<IEftPosService>();

        var result = await eftSvc.DoEftPosTransactionAsync(job);

        try
        {
            using var http = CreateHttpClient(token, conn.NormalizedUrl);
            var resp = await http.PostAsJsonAsync("api/transaction/complete",
                new { TransactionId = job.TransactionId, result.Success, result.Message });
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{conn.ServerName}] Report EFT failed TXN-{job.TransactionId}: {ex.Message}");
        }

        lock (_lock)
        {
            ProcessingEftJobs.Remove(job);
            CompletedEftJobs.Add((result, DateTime.Now));
            if (CompletedEftJobs.Count > 50) CompletedEftJobs.RemoveAt(0);
        }

        AddLog(result.Success ? "success" : "error",
            $"[{conn.ServerName}] [EFT OUT] TXN-{job.TransactionId} {(result.Success ? "Approved ✓" : "Declined ✗")}");

        await _uiHub.Clients.All.SendAsync("JobStatusUpdate", result);

        StateChanged?.Invoke();
    }

    // ── Pending job recovery ──────────────────────────────────────────────

    private async Task FetchPendingAsync(ServerConnection conn, string token)
    {
        try
        {
            using var http = CreateHttpClient(token, conn.NormalizedUrl);

            var printJobs = await http.GetFromJsonAsync<List<PrintJobRequest>>("api/print/pending", _jsonOptions);
            if (printJobs?.Count > 0)
            {
                AddLog("info", $"[{conn.ServerName}] Recovered {printJobs.Count} pending print job(s)");
                foreach (var job in printJobs)
                {
                    var enriched = job with { ConnectionId = conn.Id };
                    lock (_lock)
                    {
                        if (PendingPrintJobs.All(p => p.Id != job.Id))
                            PendingPrintJobs.Add(enriched);
                    }
                    _ = Task.Run(async () =>
                    {
                        await _printSemaphore.WaitAsync();
                        try { await ProcessPrintJobAsync(enriched, conn, token); }
                        finally { _printSemaphore.Release(); }
                    });
                }
            }

            var eftJobs = await http.GetFromJsonAsync<List<EftPosRequest>>("api/transaction/pending", _jsonOptions);
            if (eftJobs?.Count > 0)
            {
                AddLog("info", $"[{conn.ServerName}] Recovered {eftJobs.Count} pending EFT job(s)");
                foreach (var job in eftJobs)
                {
                    var enriched = job with { ConnectionId = conn.Id };
                    lock (_lock)
                    {
                        if (ProcessingEftJobs.All(j => j.TransactionId != job.TransactionId))
                            ProcessingEftJobs.Add(enriched);
                    }
                    _ = ProcessEftJobAsync(enriched, conn, token);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{conn.ServerName}] FetchPending failed: {ex.Message}");
        }
    }

    // ── Token refresh reconnect ───────────────────────────────────────────

    private void OnTokenRefreshed(object? sender, TokenRefreshEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            ServerConnection? conn = null;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IServerConnectionRepository>();
                conn = await repo.GetByIdAsync(e.ServerConnectionId);
            }
            if (conn is null) return;

            // Dispose old connection and reconnect with fresh token
            DisconnectServer(e.ServerConnectionId);
            await ConnectAsync(conn, CancellationToken.None);
        });
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async Task WaitForBackendAsync(CancellationToken ct)
    {
        List<ServerConnection> connections;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IServerConnectionRepository>();
            connections = await repo.GetAllAsync();
        }
        if (connections.Count == 0) return;

        var firstUrl = connections[0].NormalizedUrl;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var retries = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync($"{firstUrl}health", ct);
                if (resp.IsSuccessStatusCode) break;
            }
            catch { /* not ready yet */ }

            retries++;
            var delay = retries <= 5 ? 3_000 : 10_000;
            _logger.LogInformation("Backend not ready, retry #{R} in {D}ms…", retries, delay);
            await Task.Delay(delay, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HubConnection BuildHubConnection(ServerConnection conn, string token)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{conn.NormalizedUrl}hubs/aura", o =>
                o.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();
        hub.ServerTimeout     = TimeSpan.FromDays(1);
        hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
        return hub;
    }

    private static HttpClient CreateHttpClient(string token, string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private void SetStatus(ServerConnection conn, string status)
    {
        lock (_lock)
            ConnectionStatuses[conn.Id] = (conn.ServerName, status, conn.TenantId);

        _ = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IServerConnectionRepository>();
            await repo.UpdateStatusAsync(conn.Id, status switch
            {
                "connected"    => ConnectionStatus.Online,
                "connecting"   => ConnectionStatus.Connecting,
                "reconnecting" => ConnectionStatus.Connecting,
                "error"        => ConnectionStatus.Error,
                _              => ConnectionStatus.Disconnected
            });
        });
        StateChanged?.Invoke();
    }

    private void AddLog(string level, string message)
    {
        lock (_lock)
        {
            Logs.Add((level, message, DateTime.Now));
            if (Logs.Count > 100) Logs.RemoveAt(0);
        }
        StateChanged?.Invoke();
    }

    public override void Dispose()
    {
        _connectionEvents.TokenRefreshed -= OnTokenRefreshed;
        base.Dispose();
    }
}
