namespace POC.AURA.SmartHub.Service.Scheduling;

public interface ITokenSchedulerService
{
    /// <summary>Load all stored tokens from DB and schedule refresh jobs for each.</summary>
    Task InitializeTokenSchedulingAsync();

    /// <summary>
    /// Schedule a refresh job for the given connection.
    /// Fires at 80% of <paramref name="period"/> (minimum 3 minutes).
    /// </summary>
    Task ScheduleTokenRefreshAsync(int connectionId, TimeSpan period);

    /// <summary>Execute a token refresh immediately for the given connection.</summary>
    Task RefreshTokenAsync(int connectionId);

    /// <summary>Cancel and remove the refresh job for the given connection.</summary>
    Task DeleteTokenRefreshAsync(int connectionId);
}
