using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using POC.AURA.SmartHub.Models;

namespace POC.AURA.SmartHub.Services;

/// <summary>
/// Manages print job processing for all configured tenants.
/// <para>
/// Extends <see cref="TenantHubServiceBase"/> and connects to AuraHub as <c>smarthub</c>.
/// On receiving <c>ExecutePrintJob</c>, automatically simulates printing (1–3 s delay)
/// then reports the result to <c>POST /api/print/complete</c>.
/// </para>
/// </summary>
public sealed class PrintHubService : TenantHubServiceBase
{
    protected override string ClientType => "smarthub";

    /// <summary>Jobs currently in-flight (added when received, removed after reporting).</summary>
    public List<PrintJob> PendingJobs { get; } = [];

    /// <summary>Completed job results, capped at the 50 most recent.</summary>
    public List<(PrintJobResult Result, DateTime Time)> CompletedJobs { get; } = [];

    public PrintHubService(IConfiguration config, ILogger<PrintHubService> logger)
        : base(config, logger) { }

    // ── TenantHubServiceBase ──────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void RegisterHandlers(HubConnection hub, string tenantId, string token)
    {
        hub.On<PrintJob>("ExecutePrintJob", job =>
        {
            lock (SyncLock) PendingJobs.Add(job);
            AddLog("info", $"[{tenantId}] [IN] #{job.Id} \"{job.DocumentName}\" ×{job.Copies}");
            _ = AutoProcessAsync(job);
        });

        hub.On<PrintJob>("PrintJobQueued", job =>
            AddLog("info", $"[{tenantId}] [Queued] #{job.Id} confirmed"));
    }

    /// <inheritdoc/>
    protected override async Task FetchPendingAsync(string tenantId, string token)
    {
        var url = $"{BackendUrl}/api/print/pending";
        AddLog("info", $"[{tenantId}] Fetching pending jobs from {url}...");

        try
        {
            using var http = CreateHttpClient(token);
            var resp = await http.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                AddLog("error", $"[{tenantId}] GET pending failed {(int)resp.StatusCode}: {body}");
                return;
            }

            var jobs = await resp.Content.ReadFromJsonAsync<List<PrintJob>>(
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy          = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive   = true
                });

            AddLog("info", $"[{tenantId}] Pending jobs in DB: {jobs?.Count ?? 0}");
            if (jobs is null || jobs.Count == 0) return;

            var newJobs = new List<PrintJob>();
            lock (SyncLock)
            {
                foreach (var job in jobs.Where(j => !PendingJobs.Any(p => p.Id == j.Id)))
                {
                    PendingJobs.Add(job);
                    newJobs.Add(job);
                }
            }

            if (newJobs.Count == 0) return;

            AddLog("info", $"[{tenantId}] Recovered {newJobs.Count} job(s) — auto-processing...");
            foreach (var job in newJobs)
                _ = AutoProcessAsync(job);
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{tenantId}] FetchPending exception: {ex.GetType().Name}: {ex.Message}");
            Logger.LogError(ex, "FetchPendingAsync failed for {TenantId}", tenantId);
        }
    }

    // ── Job processing ────────────────────────────────────────────────────

    /// <summary>
    /// Reports a job as completed or failed to the backend HTTP API.
    /// Updates <see cref="PendingJobs"/> and <see cref="CompletedJobs"/>.
    /// </summary>
    public async Task ReportCompleteAsync(PrintJob job, bool success)
    {
        var message = success
            ? $"Printed {job.Copies}× \"{job.DocumentName}\""
            : "Print failed: Paper jam";

        if (!TryGetConnection(job.TenantId, out var conn))
        {
            AddLog("error", $"No connection for tenant {job.TenantId}");
            return;
        }

        try
        {
            using var http = CreateHttpClient(conn.Token);
            var resp = await http.PostAsJsonAsync(
                $"{BackendUrl}/api/print/complete",
                new { JobId = job.Id, Success = success, Message = message });
            resp.EnsureSuccessStatusCode();

            var result = new PrintJobResult(
                job.Id, job.TenantId, job.RequestorConnectionId,
                success, message, DateTime.UtcNow.ToString("O"));

            lock (SyncLock)
            {
                PendingJobs.Remove(job);
                CompletedJobs.Add((result, DateTime.Now));
                if (CompletedJobs.Count > 50) CompletedJobs.RemoveAt(0);
            }

            AddLog(success ? "success" : "error",
                $"[{job.TenantId}] [OUT] #{job.Id} {(success ? "Done ✓" : "Failed ✗")} → reported");
        }
        catch (Exception ex)
        {
            AddLog("error", $"Report failed for #{job.Id}: {ex.Message}");
            Logger.LogError(ex, "Failed to report job complete for {Id}", job.Id);
        }
    }

    // ── Private ───────────────────────────────────────────────────────────

    /// <summary>Simulates print processing then auto-reports completion.</summary>
    private async Task AutoProcessAsync(PrintJob job)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(1_000, 3_000));
            await ReportCompleteAsync(job, success: true);
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{job.TenantId}] Auto-process failed #{job.Id}: {ex.Message}");
        }
    }

    private HttpClient CreateHttpClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
