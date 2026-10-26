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

        /// <summary>The last render failure logged per item and action ("draw", "measure"): exception type and message.</summary>
        private readonly ConcurrentDictionary<(Guid, string), string> _failures = new();

        /// <summary>
        /// Notes that <paramref name="action"/> ("draw", "measure") failed on the item with <paramref name="failure"/>;
        /// true the first time, and again only after the action succeeded (<see cref="ClearFailure"/>) or fails
        /// differently, so a broken item is logged once, not every frame.
        /// </summary>
        internal bool ShouldLogFailure(Guid itemId, string action, string failure)
        {
            var previous = _failures.GetValueOrDefault((itemId, action));
            _failures[(itemId, action)] = failure;
            return previous != failure;
        }

        /// <summary>Forgets an item's logged failure once the action succeeds again. Cheap when nothing has failed.</summary>
        internal void ClearFailure(Guid itemId, string action)
        {
            if (!_failures.IsEmpty)
            {
                _failures.TryRemove((itemId, action), out _);
            }
        }

        public T Get<T>(Guid itemId) where T : class, new()
        {
            return (T)_states.GetOrAdd((itemId, typeof(T)), static _ => new T());
        }

        /// <summary>
        /// Drops the state of items that are no longer in the profile.
        /// </summary>
        public void Prune(IReadOnlySet<Guid> live)
        {
            foreach (var key in _failures.Keys)
            {
                if (!live.Contains(key.Item1))
                {
                    _failures.TryRemove(key, out _);
                }
            }

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
