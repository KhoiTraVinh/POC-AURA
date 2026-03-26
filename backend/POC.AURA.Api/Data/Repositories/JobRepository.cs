using Microsoft.EntityFrameworkCore;
using POC.AURA.Api.Common.Constants;
using POC.AURA.Api.Data.Entities;

namespace POC.AURA.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IJobRepository"/>.
/// Registered as <c>Scoped</c> (one instance per HTTP/hub request).
/// </summary>
public sealed class JobRepository : IJobRepository
{
    private readonly AppDbContext _db;

    public JobRepository(AppDbContext db) => _db = db;

    /// <inheritdoc/>
    public async Task<Message> SaveAsync(
        string tenantId, string type, string id,
        string payload, string connectionId,
        string? userId = null,
        CancellationToken ct = default)
    {
        var message = new Message
        {
            TenantId              = tenantId,
            Type                  = type,
            Ref                   = id,
            Payload               = payload,
            Status                = JobStatuses.Pending,
            RequestorUserId       = userId,
            RequestorConnectionId = connectionId,
            CreatedAt             = DateTime.UtcNow
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(ct);
        return message;
    }

    /// <inheritdoc/>
    public Task<Message?> FindByRefAsync(
        string id, string tenantId, string type,
        CancellationToken ct = default) =>
        _db.Messages.FirstOrDefaultAsync(
            m => m.Ref == id && m.TenantId == tenantId && m.Type == type, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> GetPendingAsync(
        string tenantId, string type,
        CancellationToken ct = default) =>
        await _db.Messages
            .Where(m => m.TenantId == tenantId
                     && m.Type     == type
                     && m.Status   == JobStatuses.Pending)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<bool> CompleteAsync(
        string id, string tenantId, string type,
        bool success, string resultMessage,
        CancellationToken ct = default)
    {
        var message = await FindByRefAsync(id, tenantId, type, ct);
        if (message is null) return false;

        message.Status        = success ? JobStatuses.Completed : JobStatuses.Failed;
        message.CompletedAt   = DateTime.UtcNow;
        message.ResultMessage = resultMessage;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
