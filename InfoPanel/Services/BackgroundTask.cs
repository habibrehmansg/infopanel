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

        public bool IsRunning => _task is not null && !_task.IsCompleted && _cts is not null && !_cts.IsCancellationRequested;

        protected bool _shutdown = false;

        public async Task StartAsync()
        {
            await _startStopSemaphore.WaitAsync();
            _shutdown = false;
            try
            {
                if (IsRunning) return;

                _cts = new CancellationTokenSource();
                _task = Task.Run(() => DoWorkAsync(_cts.Token), _cts.Token);
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        public async Task StopAsync(bool shutdown = false)
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
