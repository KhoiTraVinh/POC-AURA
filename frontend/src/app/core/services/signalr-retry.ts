import * as signalR from '@microsoft/signalr';

/**
 * SignalR retry policy that retries indefinitely.
 * Delays: 0 ms → 2 s → 5 s → 10 s → 10 s (held constant from attempt 3 onward).
 */
export const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2_000, 5_000, 10_000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  },
};
