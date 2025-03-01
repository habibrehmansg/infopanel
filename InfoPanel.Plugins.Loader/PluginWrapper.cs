using System.Diagnostics;
using System.Reflection;

namespace InfoPanel.Plugins.Loader
{
    public class PluginWrapper(string fileName, IPlugin plugin)
    {
        public string FileName => fileName;
        public IPlugin Plugin { get; } = plugin;
       public List<IPluginContainer> PluginContainers { get; } = [];

        public string Id => Plugin.Id;
        public string Name => Plugin.Name;
        public string Description => Plugin.Description;
        public string? ConfigFilePath => Plugin.ConfigFilePath;

        public TimeSpan UpdateInterval => Plugin.UpdateInterval;

        private readonly Stopwatch _stopwatch = new();
        private long _updateTimeMilliseconds = 0;
        public long UpdateTimeMilliseconds => _updateTimeMilliseconds;

        private static readonly SemaphoreSlim _startStopSemaphore = new(1, 1);

        private CancellationTokenSource? _cts;
        private Task? _task;

        public bool IsRunning => _task is not null && !_task.IsCompleted && _cts is not null && !_cts.IsCancellationRequested;

        public void Update()
        {
            // If the plugin is running or the interval is not set to <0, we don't want to update it manually
            if (IsRunning || Plugin.UpdateInterval.TotalMilliseconds > 0) return;

            try
            {
                _stopwatch.Restart();
                Plugin.Update();
                _updateTimeMilliseconds = _stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                
            }
        }

        public async Task Initialize()
        {
            await _startStopSemaphore.WaitAsync();
            try
            {
                Plugin.Initialize();
                Plugin.Load(PluginContainers);

                // If the plugin is running or the interval is not set to >0, we don't want to start it
                if (IsRunning || Plugin.UpdateInterval.TotalMilliseconds <= 0) return;
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

        private async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(300, cancellationToken);

            Trace.WriteLine($"Plugin {Name} task started");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        _stopwatch.Restart();
                        await Plugin.UpdateAsync(cancellationToken);
                        _updateTimeMilliseconds = _stopwatch.ElapsedMilliseconds;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Exception during task execution: {ex.Message}");
                    }

                    await Task.Delay(Plugin.UpdateInterval, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                // Task was canceled
            }
        }

        private void DisposeResources()
        {
            _cts?.Dispose();
            _task?.Dispose();
            _cts = null;
            _task = null;

            try
            {
                Plugin.Close();
            }catch { }

            PluginContainers.Clear();
        }
    }
}
