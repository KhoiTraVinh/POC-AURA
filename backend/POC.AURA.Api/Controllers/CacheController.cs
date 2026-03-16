using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using POC.AURA.Api.Infrastructure;

namespace POC.AURA.Api.Controllers;

[ApiController]
[Route("api/cache")]
public class CacheController(IMemoryCache cache, CacheKeyRegistry cacheKeys) : ControllerBase
{
    /// <summary>
    /// Trả về tất cả entries đang có trong IMemoryCache.
    /// Dùng CacheKeyRegistry (track khi Set) thay vì reflection vào internals của MemoryCache
    /// → reliable, không bị break khi .NET upgrade.
    /// </summary>
    [HttpGet]
    public IActionResult GetEntries()
    {
        var entries = cacheKeys.Keys
            .Select(key =>
            {
                cache.TryGetValue(key, out var value);
                return new { key, value };
            })
            .ToList();

        return Ok(new { count = entries.Count, entries });
    }
}
