using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace POC.AURA.SmartHub.Services;

/// <summary>Immutable snapshot of a bank job routed from AuraHub.</summary>
public record BankJob(
    string  TransactionId,
    string  Description,
    decimal Amount,
    string  Currency,
    DateTime SubmittedAt
);

/// <summary>Result sent back to the backend after processing.</summary>
public record BankJobResult(
    string TransactionId,
    bool   Success,
    string Message
);

/// <summary>
/// Manages bank transaction processing for all configured tenants.
/// <para>
/// Extends <see cref="TenantHubServiceBase"/> and connects to AuraHub as <c>bank</c>.
/// On receiving <c>ExecuteTransaction</c>, simulates bank verification (3–7 s) with
/// a configurable 15% failure rate, then reports to <c>POST /api/transaction/complete</c>.
/// </para>
/// </summary>
public sealed class BankHubService : TenantHubServiceBase
{
    protected override string ClientType => "bank";

    /// <summary>Transactions currently being processed.</summary>
    public List<BankJob> ProcessingQueue { get; } = [];

    /// <summary>Completed results, capped at the 50 most recent.</summary>
    public List<(BankJobResult Result, DateTime Time)> CompletedJobs { get; } = [];

    public BankHubService(IConfiguration config, ILogger<BankHubService> logger)
        : base(config, logger) { }

    // ── TenantHubServiceBase ──────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void RegisterHandlers(HubConnection hub, string tenantId, string token)
    {
        hub.On<BankJob>("ExecuteTransaction", job =>
        {
            lock (SyncLock) ProcessingQueue.Add(job);
            AddLog("info",
                $"[{tenantId}] [IN] TXN-{job.TransactionId} \"{job.Description}\" {job.Amount:N0} {job.Currency}");
            _ = ProcessJobAsync(job, token);
        });
    }

    /// <inheritdoc/>
    protected override async Task FetchPendingAsync(string tenantId, string token)
    {
        try
        {
            using var http = CreateHttpClient(token);
            var txns = await http.GetFromJsonAsync<List<BankJob>>($"{BackendUrl}/api/transaction/pending");
            if (txns is null || txns.Count == 0) return;

            AddLog("info", $"[{tenantId}] Recovered {txns.Count} pending transaction(s) — processing...");

            foreach (var job in txns)
            {
                lock (SyncLock)
                {
                    if (!ProcessingQueue.Any(j => j.TransactionId == job.TransactionId))
                        ProcessingQueue.Add(job);
                }
                _ = ProcessJobAsync(job, token);
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"[{tenantId}] Failed to fetch pending transactions: {ex.Message}");
        }
    }

    // ── Processing ────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates bank verification then reports the result to the backend.
    /// Failure rate: ~15% (for demo purposes).
    /// </summary>
    private async Task ProcessJobAsync(BankJob job, string token)
    {
        AddLog("info", $"Processing TXN-{job.TransactionId} — verifying...");

        await Task.Delay(Random.Shared.Next(3_000, 7_000));

        var success   = Random.Shared.NextDouble() > 0.15;
        var reference = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var message   = success
            ? $"Approved. Bank Reference: TXN-{reference}"
            : "Declined: insufficient balance or fraud check failed";

        var result = new BankJobResult(job.TransactionId, success, message);

        try
        {
            using var http = CreateHttpClient(token);
            var resp = await http.PostAsJsonAsync(
                $"{BackendUrl}/api/transaction/complete",
                new { TransactionId = job.TransactionId, Success = success, Message = message });
            resp.EnsureSuccessStatusCode();

            AddLog(success ? "success" : "error",
                $"[OUT] TXN-{job.TransactionId} {(success ? "Approved ✓" : "Declined ✗")}: {message}");
        }
        catch (Exception ex)
        {
            AddLog("error", $"[OUT] Report failed for TXN-{job.TransactionId}: {ex.Message}");
            Logger.LogError(ex, "Failed to report transaction complete for {Id}", job.TransactionId);
        }
        finally
        {
            lock (SyncLock)
            {
                ProcessingQueue.Remove(job);
                CompletedJobs.Add((result, DateTime.Now));
                if (CompletedJobs.Count > 50) CompletedJobs.RemoveAt(0);
            }
            NotifyStateChanged();
        }
    }

    // ── Private ───────────────────────────────────────────────────────────

    private HttpClient CreateHttpClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
