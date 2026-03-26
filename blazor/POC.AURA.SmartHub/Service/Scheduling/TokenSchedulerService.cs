using POC.AURA.SmartHub.Data;
using POC.AURA.SmartHub.Service.Auth;

namespace POC.AURA.SmartHub.Service.Scheduling;

/// <summary>
/// Timer-based token scheduler (production equivalent: Quartz.NET).
/// Schedules refresh at 80% of token lifetime, minimum 3 minutes.
/// <para>
/// IClientAuthenticationService is resolved via IServiceScopeFactory (not constructor-injected)
/// to break the circular dependency: ClientAuthenticationService → ITokenSchedulerService
/// → IClientAuthenticationService.
/// </para>
/// </summary>
public class TokenSchedulerService(
    IServerConnectionRepository repo,
    IServiceScopeFactory scopeFactory,
    ILogger<TokenSchedulerService> logger) : ITokenSchedulerService, IAsyncDisposable
{
    private readonly Dictionary<int, Timer> _timers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);

    public async Task InitializeTokenSchedulingAsync()
    {
        var connections = await repo.GetAllAsync();
        foreach (var conn in connections.Where(c => c.AuthToken is not null))
        {
            var remaining = conn.AuthToken!.ExpiredAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMinutes(5);
            await ScheduleTokenRefreshAsync(conn.Id, remaining);
        }
        logger.LogInformation("TokenScheduler initialized {Count} job(s)", connections.Count(c => c.AuthToken is not null));
    }

    public async Task ScheduleTokenRefreshAsync(int connectionId, TimeSpan period)
    {
        // 80% of lifetime, minimum 3 minutes
        var delay = TimeSpan.FromSeconds(period.TotalSeconds * 0.8);
        if (delay < MinInterval) delay = MinInterval;

        await _lock.WaitAsync();
        try
        {
            if (_timers.TryGetValue(connectionId, out var existing))
            {
                await existing.DisposeAsync();
                _timers.Remove(connectionId);
            }

            var timer = new Timer(
                callback: _ => _ = RefreshTokenAsync(connectionId),
                state: null,
                dueTime: delay,
                period: Timeout.InfiniteTimeSpan);

            _timers[connectionId] = timer;
            logger.LogDebug("Scheduled token refresh for conn {Id} in {Delay:mm\\:ss}", connectionId, delay);
        }
        finally { _lock.Release(); }
    }

    public async Task RefreshTokenAsync(int connectionId)
    {
        logger.LogInformation("Refreshing token for connection {Id}", connectionId);

        // Resolve IClientAuthenticationService from a fresh scope to avoid circular dependency
        // (TokenSchedulerService ← IClientAuthenticationService ← ITokenSchedulerService).
        string? newToken;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<IClientAuthenticationService>();
            newToken = await auth.RefreshTokenAsync(connectionId);
        }

        if (newToken is null)
        {
            logger.LogWarning("Token refresh returned null for connection {Id}", connectionId);
            return;
        }

        // Reschedule for next cycle (55-minute default)
        await ScheduleTokenRefreshAsync(connectionId, TimeSpan.FromMinutes(55));
    }

    public async Task DeleteTokenRefreshAsync(int connectionId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_timers.TryGetValue(connectionId, out var timer))
            {
                await timer.DisposeAsync();
                _timers.Remove(connectionId);
            }
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var t in _timers.Values) await t.DisposeAsync();
            _timers.Clear();
        }
        finally { _lock.Release(); }
    }
}
