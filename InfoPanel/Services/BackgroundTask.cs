using Serilog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public abstract class BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<BackgroundTask>();
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
                
                Logger.Debug("{TaskName} starting initialization", this.GetType().Name);

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
            Logger.Debug("{TaskName} stopping", this.GetType().Name);

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
                    Logger.Error(ex, "Exception during task stop for {TaskName}", this.GetType().Name);
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

            Logger.Debug("{TaskName} stopped", this.GetType().Name);
        }

        protected abstract Task DoWorkAsync(CancellationToken token);

        private void DisposeResources()
        {
            Logger.Debug("Disposing resources for {TaskName}", this.GetType().Name);
            _cts?.Dispose();
            _task?.Dispose();
            _cts = null;
            _task = null;
        }
    }
}
