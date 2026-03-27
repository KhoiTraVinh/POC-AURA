using Microsoft.AspNetCore.Mvc;
using POC.AURA.Api.Common.Extensions;
using POC.AURA.Api.Service;

namespace POC.AURA.Api.Server.Controllers;

/// <summary>
/// Debug endpoint — shows all active SignalR WebSocket connections in real time.
/// Demonstrates that each tenant/client gets its own connection.
/// No auth required so it can be hit from a browser tab directly.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ConnectionsController : ControllerBase
{
    private readonly IConnectionTracker _tracker;

    public ConnectionsController(IConnectionTracker tracker) => _tracker = tracker;

    /// <summary>
    /// Returns a live snapshot of every active SignalR connection grouped by userId.
    /// Each entry = one persistent WebSocket to /hubs/aura.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var all = _tracker.GetAll();

        var result = new
        {
            totalConnections = all.Values.Sum(c => c.Count),
            totalUsers       = all.Count,
            timestamp        = DateTime.UtcNow,
            connections      = all
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    userId      = kv.Key,
                    socketCount = kv.Value.Count,
                    socketIds   = kv.Value
                })
        };

        return Ok(result);
    }
}
