using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace POC.AURA.SmartHub.Services;

public record BankJob(
    string TransactionId,
    string Description,
    decimal Amount,
    string Currency,
    DateTime SubmittedAt
);

public record BankJobResult(
    string TransactionId,
    bool Success,
    string Message
);

public class BankHubService : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<BankHubService> _logger;
    private HubConnection? _hub;
    private string _accessToken = string.Empty;
    private string _backendUrl = string.Empty;

    public string Status { get; private set; } = "disconnected";
    public string TenantId { get; private set; } = string.Empty;

    public List<BankJob> ProcessingQueue { get; } = [];
    public List<(BankJobResult Result, DateTime Time)> CompletedJobs { get; } = [];
    public List<(string Level, string Message, DateTime Time)> Logs { get; } = [];

    public event Action? StateChanged;

    public BankHubService(IConfiguration config, ILogger<BankHubService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ConnectAsync(string tenantId)
    {
        if (_hub is not null)
            await DisposeAsync();

        TenantId = tenantId;
        SetStatus("connecting");

        _backendUrl = _config["Backend:Url"] ?? "http://backend:8080";

        try
        {
            _accessToken = await GetTokenAsync(_backendUrl, tenantId);
        }
        catch (Exception ex)
        {
            SetStatus("disconnected");
            AddLog("error", $"Auth failed: {ex.Message}");
            _logger.LogError(ex, "Failed to get bank token for {TenantId}", tenantId);
            return;
        }

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_backendUrl}/hubs/transaction", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        _hub.Reconnecting += _ => { SetStatus("reconnecting"); return Task.CompletedTask; };
        _hub.Reconnected += _ => { SetStatus("connected"); return Task.CompletedTask; };
        _hub.Closed += _ => { SetStatus("disconnected"); return Task.CompletedTask; };

        // Server routes to bank-{tenantId} group when a transaction is submitted
        _hub.On<BankJob>("ExecuteTransaction", job =>
        {
            ProcessingQueue.Add(job);
            AddLog("info", $"[IN] TXN-{job.TransactionId} \"{job.Description}\" {job.Amount:N0} {job.Currency}");
            StateChanged?.Invoke();

            // Fire-and-forget processing — hub callback must return immediately
            _ = ProcessJobAsync(job);
        });

        try
        {
            await _hub.StartAsync();
            SetStatus("connected");
            AddLog("success", $"Connected as Bank Processor — tenant: {tenantId}, group: bank-{tenantId}");
        }
        catch (Exception ex)
        {
            SetStatus("disconnected");
            AddLog("error", $"Connection failed: {ex.Message}");
            _logger.LogError(ex, "Failed to connect to TransactionHub for {TenantId}", tenantId);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hub is not null)
        {
            await _hub.StopAsync();
            await _hub.DisposeAsync();
            _hub = null;
        }
        SetStatus("disconnected");
        AddLog("warn", "Disconnected");
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task ProcessJobAsync(BankJob job)
    {
        AddLog("info", $"[Processing] TXN-{job.TransactionId} — verifying...");
        StateChanged?.Invoke();

        // Simulate bank processing time (3–7 seconds)
        var delayMs = Random.Shared.Next(3000, 7000);
        await Task.Delay(delayMs);

        // 85% success rate
        var success = Random.Shared.NextDouble() > 0.15;
        var reference = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var message = success
            ? $"Approved. Bank Reference: TXN-{reference}"
            : "Declined: insufficient balance or fraud check failed";

        var result = new BankJobResult(job.TransactionId, success, message);

        try
        {
            // Report completion via HTTP POST /api/transaction/complete (requires Bearer token)
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var body = new { TransactionId = job.TransactionId, Success = success, Message = message };
            var resp = await http.PostAsJsonAsync($"{_backendUrl}/api/transaction/complete", body);
            resp.EnsureSuccessStatusCode();

            AddLog(success ? "success" : "error",
                $"[OUT] TXN-{job.TransactionId} {(success ? "Approved ✓" : "Declined ✗")}: {message}");
        }
        catch (Exception ex)
        {
            AddLog("error", $"[OUT] Report failed: {ex.Message}");
            _logger.LogError(ex, "Failed to report transaction complete for {Id}", job.TransactionId);
        }
        finally
        {
            ProcessingQueue.Remove(job);
            CompletedJobs.Add((result, DateTime.Now));
            if (CompletedJobs.Count > 50) CompletedJobs.RemoveAt(0);
            StateChanged?.Invoke();
        }
    }

    private async Task<string> GetTokenAsync(string backendUrl, string tenantId)
    {
        using var http = new HttpClient();
        var body = new { TenantId = tenantId, ClientType = "bank", UserName = $"BankProcessor-{tenantId}" };
        var resp = await http.PostAsJsonAsync($"{backendUrl}/api/auth/token", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()
               ?? throw new InvalidOperationException("No accessToken in response");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    private void AddLog(string level, string message)
    {
        Logs.Add((level, message, DateTime.Now));
        if (Logs.Count > 100) Logs.RemoveAt(0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }
}
