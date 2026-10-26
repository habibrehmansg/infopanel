using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Mutable per-item render state (gauge animation, chart smoothing), keyed by item id.
    /// Keeps that state out of the model and out of statics.
    /// </summary>
    public sealed class ItemStateStore
    {
        private readonly ConcurrentDictionary<(Guid, Type), object> _states = new();

        public T Get<T>(Guid itemId) where T : class, new()
        {
            return (T)_states.GetOrAdd((itemId, typeof(T)), static _ => new T());
        }

        /// <summary>
        /// Drops the state of items that are no longer in the profile.
        /// </summary>
        public void Prune(IReadOnlySet<Guid> live)
        {
            foreach (var key in _states.Keys)
            {
                if (!live.Contains(key.Item1))
                {
                    _states.TryRemove(key, out _);
                }
            }
        }
    }
}
