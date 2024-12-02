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

        public async Task StartAsync()
        {
            await _startStopSemaphore.WaitAsync();
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

        public async Task StopAsync()
        {
            await _startStopSemaphore.WaitAsync();
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
        }

        protected abstract Task DoWorkAsync(CancellationToken token);

        private void DisposeResources()
        {
            Trace.WriteLine("Disposing resources");
            _cts?.Dispose();
            _task = null;
            _cts = null;
        }
    }
}
