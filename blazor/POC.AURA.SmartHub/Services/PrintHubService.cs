using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using POC.AURA.SmartHub.Models;

namespace POC.AURA.SmartHub.Services;

public class PrintHubService : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<PrintHubService> _logger;
    private HubConnection? _hub;
    private string _accessToken = string.Empty;
    private string _backendUrl = string.Empty;

    public string Status { get; private set; } = "disconnected";
    public string TenantId { get; private set; } = string.Empty;
    public string UserName { get; private set; } = string.Empty;

    public List<PrintJob> PendingJobs { get; } = [];
    public List<(PrintJobResult Result, DateTime Time)> CompletedJobs { get; } = [];
    public List<(string Level, string Message, DateTime Time)> Logs { get; } = [];

    public event Action? StateChanged;

    public PrintHubService(IConfiguration config, ILogger<PrintHubService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ConnectAsync(string tenantId, string userName)
    {
        if (_hub is not null)
            await DisposeAsync();

        TenantId = tenantId;
        UserName = userName;
        SetStatus("connecting");

        _backendUrl = _config["Backend:Url"] ?? "http://backend:8080";

        try
        {
            _accessToken = await GetTokenAsync(_backendUrl, tenantId, userName);
        }
        catch (Exception ex)
        {
            SetStatus("disconnected");
            AddLog("error", $"Auth failed: {ex.Message}");
            _logger.LogError(ex, "Failed to get smarthub token for {TenantId}", tenantId);
            return;
        }

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_backendUrl}/hubs/print", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                // Must match server's camelCase config
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        _hub.Reconnecting += _ => { SetStatus("reconnecting"); return Task.CompletedTask; };
        _hub.Reconnected += _ => { SetStatus("connected"); return Task.CompletedTask; };
        _hub.Closed += _ => { SetStatus("disconnected"); return Task.CompletedTask; };

        _hub.On<PrintJob>("ExecutePrintJob", job =>
        {
            PendingJobs.Add(job);
            AddLog("info", $"[IN] Job #{job.Id} — \"{job.DocumentName}\" x{job.Copies}");
            StateChanged?.Invoke();
        });

        _hub.On<PrintJob>("PrintJobQueued", job =>
        {
            AddLog("info", $"[Queued] Job #{job.Id} confirmed by server");
            StateChanged?.Invoke();
        });

        _hub.On<object>("ClientConnected", _ =>
        {
            AddLog("success", "[Hub] Client connected");
            StateChanged?.Invoke();
        });

        try
        {
            await _hub.StartAsync();
            SetStatus("connected");
            AddLog("success", $"Connected as SmartHub — tenant: {tenantId}, user: {userName}");
        }
        catch (Exception ex)
        {
            SetStatus("disconnected");
            AddLog("error", $"Connection failed: {ex.Message}");
            _logger.LogError(ex, "Failed to connect to PrintHub");
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

    public async Task ReportCompleteAsync(PrintJob job, bool success)
    {
        var message = success
            ? $"Printed {job.Copies}x \"{job.DocumentName}\""
            : "Print failed: Paper jam";

        try
        {
            // Report completion via HTTP POST /api/print/complete (requires Bearer token)
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var body = new { JobId = job.Id, Success = success, Message = message };
            var resp = await http.PostAsJsonAsync($"{_backendUrl}/api/print/complete", body);
            resp.EnsureSuccessStatusCode();

            var result = new PrintJobResult(
                JobId: job.Id,
                TenantId: job.TenantId,
                RequestorConnectionId: job.RequestorConnectionId,
                Success: success,
                Message: message,
                CompletedAt: DateTime.UtcNow.ToString("O")
            );

            PendingJobs.Remove(job);
            CompletedJobs.Add((result, DateTime.Now));
            if (CompletedJobs.Count > 50) CompletedJobs.RemoveAt(0);
            AddLog(success ? "success" : "error",
                $"[OUT] #{job.Id} {(success ? "Done" : "Failed")} → reported to server");
        }
        catch (Exception ex)
        {
            AddLog("error", $"Report failed: {ex.Message}");
            _logger.LogError(ex, "Failed to report job complete");
        }

        StateChanged?.Invoke();
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(string backendUrl, string tenantId, string userName)
    {
        using var http = new HttpClient();
        var body = new { TenantId = tenantId, ClientType = "smarthub", UserName = userName };
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
