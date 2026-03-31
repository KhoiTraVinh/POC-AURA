using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Common.Extensions;
using POC.AURA.Api.Common.Models;
using POC.AURA.Api.Data.Repositories;
using POC.AURA.Api.Service;

namespace POC.AURA.Api.Server.Controllers;

/// <summary>
/// HTTP API for the Blazor SmartHub bank processor.
/// <para>All endpoints require <c>client_type = "smarthub"</c> (or <c>"bank"</c>) in the JWT.</para>
/// <list type="bullet">
///   <item><c>GET  /api/transaction/pending</c>  — Fetch unprocessed transactions on reconnect.</item>
///   <item><c>POST /api/transaction/complete</c> — Report result; releases the global bank lock.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TransactionController : ControllerBase
{
    private readonly IJobRepository          _jobs;
    private readonly ITransactionQueueService _bank;
    private readonly ILogger<TransactionController> _logger;

    private string TenantId  => User.GetTenantId();
    private string ClientType => User.GetClientType();

    public TransactionController(
        IJobRepository           jobs,
        ITransactionQueueService bank,
        ILogger<TransactionController> logger)
    {
        _jobs   = jobs;
        _bank   = bank;
        _logger = logger;
    }

    /// <summary>
    /// Returns all <c>pending</c> bank transactions for the caller's tenant.
    /// Called by SmartHub on startup/reconnect to recover unprocessed work.
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        // SmartHub connects with client_type="smarthub" and handles bank jobs too
        if (ClientType != ClientTypes.Bank && ClientType != ClientTypes.SmartHub) return Forbid();

        var messages = await _jobs.GetPendingAsync(TenantId, MessageTypes.BankTransaction);

        var txns = messages.Select(m =>
        {
            var p = JsonSerializer.Deserialize<JsonElement>(m.Payload ?? "{}");
            return new
            {
                TransactionId = m.Ref,
                Description   = p.TryGetProperty("description", out var d) ? d.GetString() : "",
                Amount        = p.TryGetProperty("amount",      out var a) ? a.GetDecimal() : 0m,
                Currency      = p.TryGetProperty("currency",    out var c) ? c.GetString()  : "VND",
                SubmittedAt   = m.CreatedAt
            };
        });

        return Ok(txns);
    }

    /// <summary>
    /// Marks a transaction as <c>completed</c> or <c>failed</c>, releases the global
    /// bank lock, and broadcasts the updated status to all UI clients.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteTransactionRequest req)
    {
        // SmartHub connects with client_type="smarthub" and handles bank jobs too
        if (ClientType != ClientTypes.Bank && ClientType != ClientTypes.SmartHub) return Forbid();

        await _bank.CompleteTransactionAsync(TenantId, req);

        _logger.LogInformation("[TxnAPI] TXN-{Id} {Status} for {TenantId}",
            req.TransactionId, req.Success ? "completed" : "failed", TenantId);

        return Ok(new { message = "Transaction completion recorded" });
    }
}
