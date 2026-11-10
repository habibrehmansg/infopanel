using AsyncKeyedLock;
using Serilog;
using System.Diagnostics;

namespace InfoPanel.Plugins.Loader
{
    public class PluginWrapper(PluginDescriptor pluginDescriptor, IPlugin plugin)
    {
        private static readonly ILogger Logger = Log.ForContext<PluginWrapper>();
        public PluginDescriptor PluginDescriptor { get; } = pluginDescriptor;
        public IPlugin Plugin { get; } = plugin;
        public List<IPluginContainer> PluginContainers { get; } = [];

        public string Id => Plugin.Id;
        public string Name => Plugin.Name;
        public string Description => Plugin.Description;
#pragma warning disable CS0618 // ConfigFilePath is obsolete but we need it for backward compatibility
        public string? ConfigFilePath => Plugin.ConfigFilePath;
#pragma warning restore CS0618

        public TimeSpan UpdateInterval => Plugin.UpdateInterval;

        private readonly Stopwatch _stopwatch = new();
        private long _updateTimeMilliseconds = 0;
        public long UpdateTimeMilliseconds => _updateTimeMilliseconds;

        private readonly AsyncNonKeyedLocker _startStopLock = new(1);

        private CancellationTokenSource? _cts;
        private Task? _task;
        // Set once the plugin's Initialize has returned, and once it has been closed: each plugin whose Initialize ran
        // is closed exactly once, whether or not it has a task of its own.
        private bool _initialized;
        private bool _closed;

        public bool IsLoaded { get; private set; } = false;

        public bool IsRunning => _task is not null && !_task.IsCompleted && _cts is not null && !_cts.IsCancellationRequested;

        // The first failure of the current streak of failed updates (Update, or UpdateAsync on the plugin's task), kept
        // until the host takes it; the streak's later failures don't replace it, and an update that succeeds ends it.
        private Exception? _failure;
        private int _failing;

        /// <summary>
        /// The failure that started the current streak of failed updates, once: null when no update has failed since the
        /// last one taken, or when the streak's first failure was already taken.
        /// </summary>
        public Exception? TakeUpdateFailure() => Interlocked.Exchange(ref _failure, null);

        private void UpdateFailed(Exception ex)
        {
            if (Interlocked.Exchange(ref _failing, 1) == 0)
                Interlocked.Exchange(ref _failure, ex);
        }

        private void UpdateSucceeded() => Interlocked.Exchange(ref _failing, 0);

        public void Update()
        {
            // If the plugin is running or the interval is not set to <0, we don't want to update it manually
            if (IsRunning || Plugin.UpdateInterval.TotalMilliseconds > 0) return;

            try
            {
                _stopwatch.Restart();
                Plugin.Update();
                _updateTimeMilliseconds = _stopwatch.ElapsedMilliseconds;
                UpdateSucceeded();
            }
            catch (Exception ex)
            {
                // Reported by the host, once per streak (TakeUpdateFailure).
                UpdateFailed(ex);
            }
        }

        public async Task Initialize()
        {
            using var _ = await _startStopLock.LockAsync();
            Plugin.Initialize();
            _initialized = true;
            Plugin.Load(PluginContainers);
            IsLoaded = true;

            // If the plugin is running or the interval is not set to >0, we don't want to start it
            if (IsRunning || Plugin.UpdateInterval.TotalMilliseconds <= 0) return;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => DoWorkAsync(_cts.Token), _cts.Token);
        }

        /// <summary>
        /// Stops the plugin: cancels its own task and waits for it, when it has one, then closes the plugin, once, when its
        /// Initialize ran (a plugin without an interval has no task, but what its Initialize or Load started is closed too).
        /// A second call does nothing.
        /// </summary>
        public async Task StopAsync()
        {
            using var _ = await _startStopLock.LockAsync();

            if (_cts is not null && _task is not null)
            {
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
                    Logger.Error(ex, "Exception during plugin task stop for {PluginName}", Name);
                }
            }

            if (_initialized && !_closed)
            {
                _closed = true;
                DisposeResources();
            }
        }

        private async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(300, cancellationToken);

            Logger.Debug("Plugin {PluginName} task started", Name);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        _stopwatch.Restart();
                        await Plugin.UpdateAsync(cancellationToken);
                        _updateTimeMilliseconds = _stopwatch.ElapsedMilliseconds;
                        UpdateSucceeded();
                    }
                    catch (Exception ex)
                    {
                        // Reported by the host, once per streak (TakeUpdateFailure); each failure only at debug here.
                        Logger.Debug(ex, "Exception during task execution for plugin {PluginName}", Name);
                        if (!cancellationToken.IsCancellationRequested)
                            UpdateFailed(ex);
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
            }
            catch { }

            PluginContainers.Clear();
        }
    }
}
