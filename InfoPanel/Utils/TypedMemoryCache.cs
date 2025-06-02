using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace InfoPanel.Utils
{
    public class TypedMemoryCache<T> : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly MemoryCacheOptions _options;
        private bool _disposed;

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
            ArgumentNullException.ThrowIfNull(key);

            options ??= new MemoryCacheEntryOptions();

            options.RegisterPostEvictionCallback((k, v, reason, state) =>
            {
                Trace.WriteLine($"Cache entry '{k}' `{v}` evicted due to {reason}.");

                switch (v)
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
            });

            _cache.Set(key, value, options);
        }

        public T? Get(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _cache.Get<T>(key);
        }

        public bool TryGetValue(string key, out T? value)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _cache.TryGetValue(key, out value);
        }

        public void Remove(string key)
        {
            ArgumentNullException.ThrowIfNull(key);

            // Dispose if needed
            if (_cache.TryGetValue<T>(key, out var value) && value is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _cache.Remove(key);
        }

        // MemoryCache specific methods
        public void Clear()
        {
            // Compact with 1.0 removes all entries
            _cache.Compact(1.0);
        }

        public void Compact(double percentage)
        {
            _cache.Compact(percentage);
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
