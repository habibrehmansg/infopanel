using System;
using System.Threading;

namespace InfoPanel.Utils
{
    /// <summary>
    /// Provides a thread-safe mechanism to debounce rapid successive calls to an action.
    /// The action will only be executed after the specified delay has elapsed without new calls.
    /// </summary>
    public class Debouncer: IDisposable
    {
        private Timer? _timer;
        private readonly SynchronizationContext? _syncContext;

        public Debouncer()
        {
            _syncContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Debounces the execution of the specified action.
        /// If called multiple times in rapid succession, only the last call will execute after the delay.
        /// </summary>
        /// <param name="action">The action to execute after the delay</param>
        /// <param name="delayMs">The delay in milliseconds before executing the action (default: 100ms)</param>
        public void Debounce(Action action, int delayMs = 100)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => {
                try
                {
                    if (_syncContext != null)
                        _syncContext.Post(_ => action(), null);
                    else
                        action();
                }
                catch (Exception ex)
                {
                    // Log exception but don't crash
                    System.Diagnostics.Debug.WriteLine($"Debouncer action failed: {ex}");
                }
            }, null, delayMs, Timeout.Infinite);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
