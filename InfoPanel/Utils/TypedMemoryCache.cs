using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Utils
{
    public class TypedMemoryCache<T> : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _keys = [];
        private readonly MemoryCache _cache;
        private readonly MemoryCacheOptions _options;
        private bool _disposed;
        public IEnumerable<string> Keys => _keys.Keys;

        public TypedMemoryCache(MemoryCacheOptions? options = null)
        {
            _options = options ?? new MemoryCacheOptions
            {
                ExpirationScanFrequency = TimeSpan.FromMinutes(1),
                SizeLimit = null // Set if you want to limit cache size
            };

            _cache = new MemoryCache(_options);
        }

        public void Set(string key, T value, MemoryCacheEntryOptions? options = null)
        {
            _cache.Set(key, value, options);
            _keys.TryAdd(key, 0);
        }

        public T? Get(string key)
        {
            return _cache.Get<T>(key);
        }

        public bool TryGetValue(string key, out T? value)
        {
            return _cache.TryGetValue(key, out value);
        }

        public void Remove(string key)
        {
            if (_cache.TryGetValue<T>(key, out var value))
            {
                switch (value)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case IDisposable[] disposables:
                        foreach (var item in disposables)
                            item?.Dispose();
                        break;
                    case IEnumerable<IDisposable> enumerable:
                        foreach (var item in enumerable)
                            item?.Dispose();
                        break;
                }
            }

            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }

        public void Clear()
        {
            foreach (var key in _keys.Keys)
            {
                Remove(key);
            }
        }

        public MemoryCacheStatistics? GetCurrentStatistics()
        {
            return _cache.GetCurrentStatistics();
        }

        public int Count => _cache.Count;

        public void Dispose()
        {
            if (_disposed) return;

            Clear(); // This will trigger disposal callbacks
            _cache?.Dispose();
            _disposed = true;

            // Suppress finalization to adhere to CA1816
            GC.SuppressFinalize(this);
        }
    }
}
