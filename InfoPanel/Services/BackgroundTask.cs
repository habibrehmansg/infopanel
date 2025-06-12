using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public abstract class BackgroundTask
    {
        private static readonly SemaphoreSlim _startStopSemaphore = new(1, 1);

        private CancellationTokenSource? _cts;
        private Task? _task;

        protected BackgroundTask() { }

        protected CancellationToken? CancellationToken => _cts?.Token;

        public bool IsRunning => _task is not null && !_task.IsCompleted && _cts is not null && !_cts.IsCancellationRequested;

        protected bool _shutdown = false;

        public async Task StartAsync(CancellationToken? token = null)
        {
            await _startStopSemaphore.WaitAsync();
            _shutdown = false;
            try
            {
                if (IsRunning) return;

                if (token == null)
                {
                    _cts = new CancellationTokenSource();
                }
                else
                {
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(token.Value);
                }
                _task = Task.Run(() => DoWorkAsync(_cts.Token), _cts.Token);
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        public virtual async Task StopAsync(bool shutdown = false)
        {
            Trace.WriteLine($"{this.GetType().Name} Task stopping");

            await _startStopSemaphore.WaitAsync();
            _shutdown = shutdown;
            try
            {
                if (_cts is null || _task is null) return;

                _cts.Cancel();

                try
                {
                    await _task;
                }
                catch (OperationCanceledException)
                {
                    // Task was canceled
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception during task stop: {ex.Message}");
                }
                finally
                {
                    DisposeResources();
                }
            }
            finally
            {
                _startStopSemaphore.Release();
            }

            Trace.WriteLine($"{this.GetType().Name} Task stopped");
        }

        protected abstract Task DoWorkAsync(CancellationToken token);

        private void DisposeResources()
        {
            Trace.WriteLine("Disposing resources");
            _cts?.Dispose();
            _task?.Dispose();
            _cts = null;
            _task = null;
        }
    }
}
