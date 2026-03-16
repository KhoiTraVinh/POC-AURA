using System.Collections.Concurrent;

namespace POC.AURA.Api.Infrastructure;

/// <summary>
/// Singleton theo dõi tất cả các key đã được set vào IMemoryCache.
/// Tránh dùng reflection vào internals của MemoryCache (fragile, thay đổi theo .NET version).
/// Khi gọi cache.Set(key, ...) thì track(key) ngay sau đó.
/// </summary>
public sealed class CacheKeyRegistry
{
    // Dùng ConcurrentDictionary làm HashSet thread-safe
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    public void Track(string key) => _keys.TryAdd(key, 0);

    public void Untrack(string key) => _keys.TryRemove(key, out _);

    public IEnumerable<string> Keys => _keys.Keys;
}
