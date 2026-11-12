using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Xtream
{
    /// <summary>
    /// In-memory TTL cache for per-stream EPG tables with a limit on concurrent provider requests.
    /// Concurrent callers asking for the same key share one load.
    /// </summary>
    public sealed class EpgTableCache
    {
        private sealed class Item
        {
            public DateTime LoadedUtc;
            public List<XtreamEpgEntry> Entries;
        }

        private readonly ConcurrentDictionary<string, Item> _items = new ConcurrentDictionary<string, Item>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _concurrency;
        private readonly Func<DateTime> _utcNow;

        public EpgTableCache(int maxConcurrent = 4, Func<DateTime> utcNow = null)
        {
            _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<List<XtreamEpgEntry>> GetAsync(string key, TimeSpan ttl, Func<Task<List<XtreamEpgEntry>>> loader, CancellationToken ct)
        {
            Item item;
            if (_items.TryGetValue(key, out item) && _utcNow() - item.LoadedUtc < ttl) return item.Entries;

            var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await keyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_items.TryGetValue(key, out item) && _utcNow() - item.LoadedUtc < ttl) return item.Entries;

                await _concurrency.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entries = await loader().ConfigureAwait(false) ?? new List<XtreamEpgEntry>();
                    _items[key] = new Item { LoadedUtc = _utcNow(), Entries = entries };
                    return entries;
                }
                finally
                {
                    _concurrency.Release();
                }
            }
            finally
            {
                keyLock.Release();
            }
        }

        public void Clear() => _items.Clear();
    }
}
