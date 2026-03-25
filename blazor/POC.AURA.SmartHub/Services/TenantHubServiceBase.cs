using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace POC.AURA.SmartHub.Services;

/// <summary>
/// Abstract base for <see cref="PrintHubService"/> and <see cref="BankHubService"/>.
/// <para>
/// Manages a per-tenant dictionary of <see cref="HubConnection"/> instances and handles:
/// <list type="bullet">
///   <item>Token acquisition from the backend auth endpoint.</item>
///   <item>Hub connection lifecycle: connect, reconnect, restart-after-closed.</item>
///   <item>Thread-safe status updates and circular log buffer.</item>
///   <item>Recovery of pending jobs on reconnect (delegated to derived classes).</item>
/// </list>
/// </para>
/// </summary>
public abstract class TenantHubServiceBase : IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────
    protected readonly IConfiguration Config;
    protected readonly ILogger Logger;

    // ── Thread-safe state ─────────────────────────────────────────────────
    private readonly Dictionary<string, (HubConnection Hub, string Token)> _connections = new();

    /// <summary>Shared lock for all mutable state in this service and derived classes.</summary>
    protected readonly object SyncLock = new();

    protected string BackendUrl = string.Empty;

    // ── Public observable state ───────────────────────────────────────────

    /// <summary>Per-tenant connection status: connecting | connected | reconnecting | disconnected | error.</summary>
    public Dictionary<string, string> TenantStatuses { get; } = new();

    /// <summary>Circular log buffer capped at 100 entries.</summary>
    public List<(string Level, string Message, DateTime Time)> Logs { get; } = [];

    /// <summary>Raised on any state change that the Blazor UI should re-render for.</summary>
    public event Action? StateChanged;

    /// <summary>Raises <see cref="StateChanged"/> — can be called from derived classes.</summary>
    protected void NotifyStateChanged() => StateChanged?.Invoke();

    // ── Abstract members (Template Method pattern) ────────────────────────

    /// <summary>
    /// The <c>client_type</c> JWT claim value for this service
    /// (e.g. <c>"smarthub"</c> or <c>"bank"</c>).
    /// </summary>
    protected abstract string ClientType { get; }

    /// <summary>
    /// Registers SignalR event handlers on <paramref name="hub"/> for this service.
    /// Called once per tenant after a successful connection.
    /// </summary>
    protected abstract void RegisterHandlers(HubConnection hub, string tenantId, string token);

    /// <summary>
    /// Fetches any jobs left in <c>pending</c> state from a previous session and
    /// processes them. Called after <see cref="RegisterHandlers"/> succeeds.
    /// </summary>
    protected abstract Task FetchPendingAsync(string tenantId, string token);

    // ── Constructor ───────────────────────────────────────────────────────

    protected TenantHubServiceBase(IConfiguration config, ILogger logger)
    {
        Config = config;
        Logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the AuraHub backend for every <paramref name="tenantId"/> in parallel.
    /// </summary>
    public async Task ConnectAllAsync(IEnumerable<string> tenantIds)
    {
        BackendUrl = Config["Backend:Url"] ?? "http://backend:8080";
        await Task.WhenAll(tenantIds.Select(ConnectTenantAsync));
    }

    // ── Protected helpers ─────────────────────────────────────────────────

    /// <summary>Retrieves the stored token for a connected tenant.</summary>
    protected bool TryGetConnection(string tenantId, out (HubConnection Hub, string Token) conn)
    {
        lock (SyncLock)
            return _connections.TryGetValue(tenantId, out conn);
    }

    /// <summary>
    /// Appends a log entry to the circular buffer and fires <see cref="StateChanged"/>.
    /// </summary>
    protected void AddLog(string level, string message)
    {
        lock (SyncLock)
        {
            Logs.Add((level, message, DateTime.Now));
            if (Logs.Count > 100) Logs.RemoveAt(0);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Requests a fresh access token from <c>/api/auth/token</c>.</summary>
    protected async Task<string> GetTokenAsync(string tenantId, string? userName = null)
    {
        using var http = new HttpClient();
        var body = new
        {
            TenantId   = tenantId,
            ClientType,
            UserName   = userName ?? $"{ClientType}@{tenantId}"
        };
        var resp = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/token", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()
               ?? throw new InvalidOperationException("Missing accessToken in auth response.");
    }

    // ── Private: connection lifecycle ─────────────────────────────────────

    private async Task ConnectTenantAsync(string tenantId)
    {
        SetStatus(tenantId, "connecting");

        string token;
        try
        {
            token = await GetTokenAsync(tenantId);
        }
        catch (Exception ex)
        {
            SetStatus(tenantId, "error");
            AddLog("error", $"[{tenantId}] Auth failed: {ex.Message}");
            Logger.LogError(ex, "Failed to get {ClientType} token for {TenantId}", ClientType, tenantId);
            return;
        }

        var hub = new HubConnectionBuilder()
            .WithUrl($"{BackendUrl}/hubs/aura", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy        = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        // Default ServerTimeout = 30 s — tăng lên để tránh disconnect giả do idle
        hub.ServerTimeout     = TimeSpan.FromDays(1);
        hub.KeepAliveInterval = TimeSpan.FromSeconds(15);

        hub.Reconnecting += _ =>
        {
            SetStatus(tenantId, "reconnecting");
            return Task.CompletedTask;
        };
        hub.Reconnected += async _ =>
        {
            SetStatus(tenantId, "connected");
            // Re-fetch any jobs that arrived while we were offline
            await FetchPendingAsync(tenantId, token);
        };
        hub.Closed += async _ =>
        {
            // WithAutomaticReconnect gave up — restart the entire connect flow
            SetStatus(tenantId, "disconnected");
            await Task.Delay(5_000);
            await ConnectTenantAsync(tenantId);
        };

        RegisterHandlers(hub, tenantId, token);

        try
        {
            await hub.StartAsync();
            lock (SyncLock) _connections[tenantId] = (hub, token);
            SetStatus(tenantId, "connected");
            AddLog("success", $"[{tenantId}] Connected as {ClientType}");
            await FetchPendingAsync(tenantId, token);
        }
        catch (Exception ex)
        {
            SetStatus(tenantId, "error");
            AddLog("error", $"[{tenantId}] Connection failed: {ex.Message}");
            Logger.LogError(ex, "Failed to connect {ClientType} for {TenantId}", ClientType, tenantId);
        }
    }

    private void SetStatus(string tenantId, string status)
    {
        lock (SyncLock) TenantStatuses[tenantId] = status;
        StateChanged?.Invoke();
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        List<HubConnection> hubs;
        lock (SyncLock)
        {
            hubs = _connections.Values.Select(c => c.Hub).ToList();
            _connections.Clear();
        }
        await Task.WhenAll(hubs.Select(h => h.DisposeAsync().AsTask()));
    }
}
