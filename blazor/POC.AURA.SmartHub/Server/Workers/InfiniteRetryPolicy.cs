using Microsoft.AspNetCore.SignalR.Client;

namespace POC.AURA.SmartHub.Server.Workers;

/// <summary>Retries forever: 0s → 2s → 5s → 10s → 30s (capped).</summary>
public class InfiniteRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var idx = (int)Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
        return retryContext.PreviousRetryCount >= Delays.Length
            ? TimeSpan.FromSeconds(30)
            : Delays[idx];
    }
}
