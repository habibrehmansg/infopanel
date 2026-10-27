using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.DataApi
{
    /// <summary>
    /// Values that external processes add through POST /api/push, keyed by
    /// sensor key. Each value remembers its source label, which drives
    /// replace pushes and per-source freshness.
    /// </summary>
    internal sealed class PushStore
    {
        private readonly ConcurrentDictionary<string, DataApiValue> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores a push. With <paramref name="replace"/>, the push is the
        /// source's complete key set: its stored keys that are missing from
        /// it are removed. Returns the number of removed keys.
        /// </summary>
        public int Apply(string source, IReadOnlyDictionary<string, DataApiValue> values, bool replace)
        {
            // Upsert first, so a reader never sees a live key missing mid-push.
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }
            _lastSeen[source] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!replace) return 0;

            int removed = 0;
            foreach (var entry in _values)
            {
                if (string.Equals(entry.Value.Source, source, StringComparison.OrdinalIgnoreCase)
                    && !values.ContainsKey(entry.Key)
                    // Removes only if unchanged, so a key another source just took over stays.
                    && _values.TryRemove(entry))
                {
                    removed++;
                }
            }
            return removed;
        }

        public void Clear()
        {
            _values.Clear();
            _lastSeen.Clear();
        }

        public void AddTo(Dictionary<string, DataApiValue> sensors)
        {
            foreach (var (key, value) in _values)
            {
                sensors[key] = value;
            }
        }

        public Dictionary<string, PushSourceStatus> GetSources()
        {
            var keyCounts = _values.Values
                .GroupBy(v => v.Source ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            return _lastSeen.ToDictionary(
                s => s.Key,
                s => new PushSourceStatus { LastSeen = s.Value, Keys = keyCounts.GetValueOrDefault(s.Key) });
        }
    }
}
