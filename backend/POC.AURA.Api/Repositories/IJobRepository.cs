using POC.AURA.Api.Entities;

namespace POC.AURA.Api.Repositories;

/// <summary>
/// Persistence contract for job messages stored in the unified <c>Messages</c> table.
/// <para>
/// Covers both <c>print_job</c> and <c>bank_txn</c> types.
/// All database access for jobs flows through this interface, keeping hubs and
/// controllers free of EF Core queries.
/// </para>
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Persists a new job with <c>Status = "pending"</c> and returns the saved entity.
    /// </summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="type">Message type discriminator (see <see cref="Constants.MessageTypes"/>).</param>
    /// <param name="id">Unique job reference ID (e.g. "A3F2B19C01").</param>
    /// <param name="payload">JSON-serialised job details.</param>
    /// <param name="connectionId">SignalR connection ID at submission time (may become stale after reconnect).</param>
    /// <param name="userId">Stable user identity from the JWT <c>name</c> claim. Used to route
    /// completion notifications even after the user reconnects with a new connection ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Message> SaveAsync(
        string tenantId, string type, string id,
        string payload, string connectionId,
        string? userId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a single job by its reference ID, tenant, and type.
    /// Returns <see langword="null"/> when not found.
    /// </summary>
    Task<Message?> FindByRefAsync(
        string id, string tenantId, string type,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all <c>pending</c> jobs for a tenant + type, ordered oldest-first.
    /// Used by the SmartHub on reconnect to recover unprocessed work.
    /// </summary>
    Task<IReadOnlyList<Message>> GetPendingAsync(
        string tenantId, string type,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a job as <c>completed</c> or <c>failed</c>, sets <c>CompletedAt</c>
    /// and <c>ResultMessage</c>.
    /// </summary>
    /// <returns><see langword="true"/> if the record was found and updated; <see langword="false"/> otherwise.</returns>
    Task<bool> CompleteAsync(
        string id, string tenantId, string type,
        bool success, string resultMessage,
        CancellationToken ct = default);
}
