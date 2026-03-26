using System.IdentityModel.Tokens.Jwt;

namespace POC.AURA.Api.Common.Constants;

/// <summary>Discriminator values stored in <c>Messages.Type</c>.</summary>
public static class MessageTypes
{
    public const string PrintJob        = "print_job";
    public const string BankTransaction = "bank_txn";
}

/// <summary>Job lifecycle states stored in <c>Messages.Status</c>.</summary>
public static class JobStatuses
{
    public const string Pending   = "pending";
    public const string Completed = "completed";
    public const string Failed    = "failed";
}

/// <summary>JWT claim names used across auth, hubs, and controllers.</summary>
public static class ClaimNames
{
    public const string TenantId   = "tenant_id";
    public const string ClientType = "client_type";
    public const string TokenType  = "token_type";
    public const string UserName   = JwtRegisteredClaimNames.Name;
}

/// <summary>Allowed values for the <c>client_type</c> JWT claim.</summary>
public static class ClientTypes
{
    /// <summary>Angular browser client.</summary>
    public const string Ui       = "ui";

    /// <summary>Blazor SmartHub print processor.</summary>
    public const string SmartHub = "smarthub";

    /// <summary>Blazor SmartHub bank processor.</summary>
    public const string Bank     = "bank";
}

/// <summary>
/// Factory helpers for SignalR group names.
/// Convention: <c>{clientType}-{tenantId}</c> for per-tenant groups.
/// </summary>
public static class HubGroups
{
    /// <summary>Angular UI clients for a specific tenant: <c>ui-{tenantId}</c>.</summary>
    public static string Ui(string tenantId) => $"ui-{tenantId}";

    /// <summary>Blazor SmartHub print processors: <c>smarthub-{tenantId}</c>.</summary>
    public static string SmartHub(string tenantId) => $"smarthub-{tenantId}";

    /// <summary>Blazor SmartHub bank processors: <c>bank-{tenantId}</c>.</summary>
    public static string Bank(string tenantId) => $"bank-{tenantId}";

    /// <summary>
    /// Builds the group name for a client type + tenant combination.
    /// Used in <see cref="POC.AURA.Api.Server.Hubs.AuraHub"/> to add/remove connections generically.
    /// </summary>
    public static string For(string clientType, string tenantId) => $"{clientType}-{tenantId}";

    /// <summary>
    /// All UI clients across every tenant.
    /// Receives global bank status broadcasts so every user sees the same bank state.
    /// </summary>
    public const string UiBroadcast = "ui-broadcast";

    /// <summary>
    /// All UI clients participating in collaborative document editing.
    /// Receives <c>FieldLocked</c>, <c>FieldUnlocked</c>, and <c>FieldValueChanged</c> events.
    /// </summary>
    public const string DocAll = "doc-all";
}

/// <summary>SignalR event names pushed from the backend to clients.</summary>
public static class HubEvents
{
    // ── Print ──────────────────────────────────────────────────────────────
    /// <summary>Sent to <c>smarthub-{tenantId}</c> to trigger print processing.</summary>
    public const string ExecutePrintJob      = "ExecutePrintJob";

    /// <summary>Confirmation sent back to the submitting Angular client.</summary>
    public const string PrintJobQueued       = "PrintJobQueued";

    /// <summary>Sent to the original requestor when the job is done/failed.</summary>
    public const string PrintJobComplete     = "PrintJobComplete";

    /// <summary>Broadcast to other UI clients in the same tenant.</summary>
    public const string PrintJobStatusUpdate = "PrintJobStatusUpdate";

    // ── Bank ───────────────────────────────────────────────────────────────
    /// <summary>Sent to <c>bank-{tenantId}</c> to trigger bank processing.</summary>
    public const string ExecuteTransaction      = "ExecuteTransaction";

    /// <summary>Global lifecycle event (processing → completed | failed).</summary>
    public const string TransactionStatusChanged = "TransactionStatusChanged";

    /// <summary>Full bank snapshot (isBankBusy, currentTransaction, history).</summary>
    public const string BankStatus              = "BankStatus";

    // ── Collaborative Document ─────────────────────────────────────────────
    /// <summary>Full snapshot of all active locks sent to a newly connected client.</summary>
    public const string LockSnapshot        = "LockSnapshot";

    /// <summary>Broadcast when a user acquires exclusive edit rights on a field.</summary>
    public const string FieldLocked         = "FieldLocked";

    /// <summary>Broadcast when a field lock is released (manual or TTL expiry).</summary>
    public const string FieldUnlocked       = "FieldUnlocked";

    /// <summary>Broadcast when the lock holder updates the field value in real-time.</summary>
    public const string FieldValueChanged   = "FieldValueChanged";

    /// <summary>Broadcast when one or more locks expire due to missed heartbeats.</summary>
    public const string FieldsExpiredUnlocked = "FieldsExpiredUnlocked";

    // ── Connection ─────────────────────────────────────────────────────────
    public const string ClientConnected    = "ClientConnected";
    public const string ClientDisconnected = "ClientDisconnected";
}
