using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Data;
using POC.AURA.Api.Models;
using POC.AURA.Api.Services;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TransactionController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITransactionQueueService _bank;
    private readonly ILogger<TransactionController> _logger;

    private string TenantId => User.FindFirst("tenant_id")!.Value;
    private string ClientType => User.FindFirst("client_type")!.Value;

    public TransactionController(AppDbContext db, ITransactionQueueService bank, ILogger<TransactionController> logger)
    {
        _db = db;
        _bank = bank;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/transaction/pending
    /// Called by SmartHub on startup/reconnect to retrieve pending transactions.
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        if (ClientType != "bank")
            return Forbid();

        var txns = await _db.BankTransactions
            .Where(t => t.TenantId == TenantId && t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                TransactionId = t.Id,
                t.Description,
                t.Amount,
                t.Currency,
                SubmittedAt = t.CreatedAt
            })
            .ToListAsync();

        return Ok(txns);
    }

    /// <summary>
    /// POST /api/transaction/complete
    /// Called by SmartHub to report transaction completion.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteTransactionRequest req)
    {
        if (ClientType != "bank")
            return Forbid();

        await _bank.CompleteTransactionAsync(TenantId, req);

        _logger.LogInformation("[TxnAPI] TXN-{Id} {Status} for {TenantId}", req.TransactionId, req.Success ? "completed" : "failed", TenantId);

        return Ok(new { message = "Transaction completion recorded" });
    }
}
