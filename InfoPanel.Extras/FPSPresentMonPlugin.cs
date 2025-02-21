using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using PresentMonFps;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

/*
 * Plugin: FPS Info - PresentMonFPS
 * Version: 1.0
 * Description: A simple InfoPanel plugin that monitors and displays Frames Per Second (FPS), current frame time, and 1% low FPS for fullscreen applications using PresentMonFps. Updates every 1 second to match InfoPanel’s default refresh rate, with retry logic to handle monitoring stalls or file conflicts.
 * Changelog:
 *   - v1.0 (Feb 20, 2025): Initial stable release.
 *     - Features: Fullscreen detection, real-time FPS monitoring, 1% low FPS calculation over 1000 frames, 1-second updates.
 *     - Stability: Added 3 retry attempts with 1s delay for FpsInspector errors (e.g., 0x800700B7), 15s stall detection with automatic restarts.
 *     - Simplified from earlier iterations by removing smoothing, throttling, and rounding attempts to address UI jitter (deemed an InfoPanel limitation).
 * Note: "Array is variable sized and does not follow prefix convention" error persists in logs but is benign and does not affect functionality.
 */

namespace InfoPanel.Extras
{
    // Defines the FPS plugin inheriting from InfoPanel’s BasePlugin
    public class FpsPlugin : BasePlugin
    {
        // Sensor definitions for FPS, 1% low FPS, and current frame time
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS"); // Displays current FPS
        private readonly PluginSensor _onePercentLowFpsSensor = new(
            "1% low fps",
            "1% Low Frames Per Second",
            0,
            "FPS"
        ); // Displays 1% low FPS (99th percentile)
        private readonly PluginSensor _currentFrameTimeSensor = new(
            "current frame time",
            "Current Frame Time",
            0,
            "ms"
        ); // Displays current frame time in milliseconds

        // Thread-safe queue to store up to 1000 frame times for 1% low calculation
        private readonly ConcurrentQueue<float> _frameTimes = new ConcurrentQueue<float>();

        // Cancellation tokens for managing background tasks
        private CancellationTokenSource? _cancellationTokenSource; // Controls the main monitoring loop
        private CancellationTokenSource? _monitoringCts; // Controls the FpsInspector monitoring task

        // Tracks the current process ID being monitored
        private uint _currentPid;

        // Constants for configuration
        private const int MaxFrameTimes = 1000; // Max number of frame times to store for 1% low calculation
        private const int RetryAttempts = 3; // Number of retry attempts for FpsInspector failures
        private const int RetryDelayMs = 1000; // Delay between retries in milliseconds

        // Tracks the last time FpsInspector updated data to detect stalls
        private DateTime _lastUpdate = DateTime.MinValue;

        // Constructor: Initializes the plugin with a unique ID, name, and description
        public FpsPlugin()
            : base("fps-plugin", "FPS Info - PresentMonFPS", "Retrieves FPS using PresentMonFPS.")
        { }

        // No configuration file used for this plugin
        public override string? ConfigFilePath => null;

        // Sets the update interval to 1 second to match InfoPanel’s default refresh rate
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initializes the plugin by starting the background monitoring task
        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _ = StartFPSMonitoringAsync(_cancellationTokenSource.Token); // Fire-and-forget async task
        }

        // Cleans up resources when the plugin is closed
        public override void Close()
        {
            _cancellationTokenSource?.Cancel(); // Stops the main monitoring loop
            _monitoringCts?.Cancel(); // Stops the FpsInspector task
            _cancellationTokenSource?.Dispose();
            _monitoringCts?.Dispose();
        }

        // Registers the plugin’s sensors with InfoPanel’s UI
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS"); // Creates a container labeled "FPS"
            container.Entries.Add(_fpsSensor); // Adds FPS sensor
            container.Entries.Add(_onePercentLowFpsSensor); // Adds 1% low FPS sensor
            container.Entries.Add(_currentFrameTimeSensor); // Adds current frame time sensor
            containers.Add(container); // Registers the container with InfoPanel
        }

        // Not implemented: Synchronous update method (InfoPanel uses UpdateAsync instead)
        public override void Update() => throw new NotImplementedException();

        // Updates sensor values every 1 second by polling FpsInspector
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await GetFpsAsync().ConfigureAwait(false); // Polls FPS data
            Console.WriteLine(
                $"UpdateAsync - FPS: {_fpsSensor.Value}, Frame Time: {_currentFrameTimeSensor.Value}, 1% Low: {_onePercentLowFpsSensor.Value}, Count: {_frameTimes.Count}"
            );
        }

        // Background task that monitors fullscreen apps and manages FpsInspector
        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Detects the active fullscreen app’s process ID
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                    Console.WriteLine($"Detected PID: {pid}");

                    if (pid != 0 && pid != _currentPid) // New fullscreen app detected
                    {
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Stops the previous monitoring task
                            _monitoringCts.Dispose();
                            Console.WriteLine("Canceled previous monitoring task");
                        }
                        _currentPid = pid;
                        await StartMonitoringWithRetryAsync(pid, cancellationToken)
                            .ConfigureAwait(false); // Starts monitoring the new process
                    }
                    else if (pid == 0) // No fullscreen app detected
                    {
                        _fpsSensor.Value = 0;
                        _currentFrameTimeSensor.Value = 0;
                        _onePercentLowFpsSensor.Value = 0;
                        Console.WriteLine("No fullscreen app detected, resetting values");
                    }

                    // Checks for stalls (no updates for 15 seconds) and restarts monitoring if needed
                    if (
                        _currentPid != 0
                        && _lastUpdate != DateTime.MinValue
                        && (DateTime.Now - _lastUpdate).TotalSeconds > 15
                    )
                    {
                        Console.WriteLine("Monitoring stalled, attempting restart...");
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel();
                            _monitoringCts.Dispose();
                            Console.WriteLine("Canceled stalled monitoring task");
                        }
                        await StartMonitoringWithRetryAsync(_currentPid, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false); // Waits 1 second before next check
                }
            }
            catch (TaskCanceledException) { } // Expected when plugin closes
            catch (Exception ex)
            {
                Console.WriteLine($"Monitoring exception: {ex.Message}");
            }
            finally
            {
                _monitoringCts?.Cancel();
                _monitoringCts?.Dispose();
                _fpsSensor.Value = 0; // Resets sensors on shutdown
                _currentFrameTimeSensor.Value = 0;
                _onePercentLowFpsSensor.Value = 0;
            }
        }

        // Starts FpsInspector monitoring with retry logic for stability
        private async Task StartMonitoringWithRetryAsync(
            uint pid,
            CancellationToken cancellationToken
        )
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                try
                {
                    _monitoringCts = new CancellationTokenSource();
                    var fpsRequest = new FpsRequest { TargetPid = pid };
                    Console.WriteLine(
                        $"Starting monitoring for PID: {pid} (Attempt {attempt}/{RetryAttempts})"
                    );

                    // Runs FpsInspector in a background task, updating sensors in real-time
                    await Task.Run(
                            () =>
                                FpsInspector.StartForeverAsync(
                                    fpsRequest,
                                    (result) =>
                                    {
                                        float fps = (float)result.Fps;
                                        float frameTime = 1000.0f / fps;
                                        _fpsSensor.Value = fps; // Updates FPS sensor
                                        _currentFrameTimeSensor.Value = frameTime; // Updates frame time sensor
                                        _lastUpdate = DateTime.Now; // Marks last update time for stall detection
                                        Console.WriteLine(
                                            $"FPS: {fps}, Current Frame Time: {frameTime} ms"
                                        );

                                        // Manages frame time queue for 1% low calculation
                                        if (_frameTimes.Count >= MaxFrameTimes)
                                            _frameTimes.TryDequeue(out _); // Removes oldest frame time if queue is full
                                        _frameTimes.Enqueue(frameTime); // Adds new frame time
                                        Console.WriteLine(
                                            $"Added frame time: {frameTime} ms, Count: {_frameTimes.Count}"
                                        );

                                        // Calculates 1% low FPS from the 99th percentile of frame times
                                        int count = _frameTimes.Count;
                                        int index = (int)(0.99 * (count - 1));
                                        if (index >= 0 && index < count)
                                        {
                                            float ninetyNinthPercentileFrameTime = _frameTimes
                                                .OrderBy(ft => ft)
                                                .Skip(index)
                                                .FirstOrDefault();
                                            _onePercentLowFpsSensor.Value =
                                                ninetyNinthPercentileFrameTime > 0
                                                    ? 1000.0f / ninetyNinthPercentileFrameTime
                                                    : 0;
                                            Console.WriteLine(
                                                $"1% Low FPS: {_onePercentLowFpsSensor.Value}"
                                            );
                                        }
                                        else
                                        {
                                            _onePercentLowFpsSensor.Value = 0;
                                            Console.WriteLine("1% Low FPS index out of range");
                                        }
                                    },
                                    _monitoringCts.Token
                                ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    Console.WriteLine(
                        $"Monitoring started successfully for PID: {pid} on attempt {attempt}"
                    );
                    break; // Exits retry loop on success
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"FpsInspector failed on attempt {attempt}/{RetryAttempts}: {ex.Message}"
                    );
                    if (attempt < RetryAttempts)
                    {
                        Console.WriteLine($"Retrying in {RetryDelayMs}ms...");
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false); // Waits before retrying
                    }
                    else
                    {
                        Console.WriteLine($"Max retries reached for PID: {pid}, restarting...");
                        _monitoringCts?.Cancel();
                        _monitoringCts?.Dispose();
                        _monitoringCts = new CancellationTokenSource();

                        // Restarts monitoring after max retries
                        await Task.Run(
                                () =>
                                    FpsInspector.StartForeverAsync(
                                        new FpsRequest { TargetPid = pid },
                                        (result) =>
                                        {
                                            float fps = (float)result.Fps;
                                            float frameTime = 1000.0f / fps;
                                            _fpsSensor.Value = fps;
                                            _currentFrameTimeSensor.Value = frameTime;
                                            _lastUpdate = DateTime.Now;
                                            Console.WriteLine(
                                                $"FPS: {fps}, Current Frame Time: {frameTime} ms"
                                            );

                                            if (_frameTimes.Count >= MaxFrameTimes)
                                                _frameTimes.TryDequeue(out _);
                                            _frameTimes.Enqueue(frameTime);
                                            Console.WriteLine(
                                                $"Added frame time: {frameTime} ms, Count: {_frameTimes.Count}"
                                            );

                                            int count = _frameTimes.Count;
                                            int index = (int)(0.99 * (count - 1));
                                            if (index >= 0 && index < count)
                                            {
                                                float ninetyNinthPercentileFrameTime = _frameTimes
                                                    .OrderBy(ft => ft)
                                                    .Skip(index)
                                                    .FirstOrDefault();
                                                _onePercentLowFpsSensor.Value =
                                                    ninetyNinthPercentileFrameTime > 0
                                                        ? 1000.0f / ninetyNinthPercentileFrameTime
                                                        : 0;
                                                Console.WriteLine(
                                                    $"1% Low FPS: {_onePercentLowFpsSensor.Value}"
                                                );
                                            }
                                            else
                                            {
                                                _onePercentLowFpsSensor.Value = 0;
                                                Console.WriteLine("1% Low FPS index out of range");
                                            }
                                        },
                                        _monitoringCts.Token
                                    ),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        Console.WriteLine($"Monitoring restarted for PID: {pid} after max retries");
                    }
                }
            }
        }

        // Polls FPS data once for UpdateAsync (runs every 1 second)
        private async Task GetFpsAsync()
        {
            try
            {
                uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                if (pid == 0) // No fullscreen app
                {
                    _fpsSensor.Value = 0;
                    _currentFrameTimeSensor.Value = 0;
                    _onePercentLowFpsSensor.Value = 0;
                    return;
                }

                var fpsRequest = new FpsRequest { TargetPid = pid };
                var fpsResult = await FpsInspector.StartOnceAsync(fpsRequest).ConfigureAwait(false); // One-shot FPS poll
                _fpsSensor.Value = (float)fpsResult.Fps; // Updates FPS sensor (frame time and 1% low updated via callback)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetFpsAsync exception: {ex.Message}");
            }
        }

        // Detects the process ID of the active fullscreen application
        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
                {
                    var hWnd = GetForegroundWindow(); // Gets the currently focused window
                    if (hWnd == IntPtr.Zero)
                    {
                        Console.WriteLine("No foreground window found");
                        return 0u;
                    }

                    if (!GetWindowRect(hWnd, out RECT windowRect)) // Gets window dimensions
                    {
                        Console.WriteLine("Failed to get window rect");
                        return 0u;
                    }

                    var hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST); // Finds the nearest monitor
                    if (hMonitor == IntPtr.Zero)
                    {
                        Console.WriteLine("No monitor found");
                        return 0u;
                    }

                    var monitorInfo = new MONITORINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
                    }; // Prepares monitor info structure
                    if (!GetMonitorInfo(hMonitor, ref monitorInfo)) // Gets monitor dimensions
                    {
                        Console.WriteLine("Failed to get monitor info");
                        return 0u;
                    }

                    var monitorRect = monitorInfo.rcMonitor;
                    Console.WriteLine(
                        $"Window Rect: {windowRect.left},{windowRect.top},{windowRect.right},{windowRect.bottom}"
                    );
                    Console.WriteLine(
                        $"Monitor Rect: {monitorRect.left},{monitorRect.top},{monitorRect.right},{monitorRect.bottom}"
                    );

                    // Checks if the window matches the monitor’s dimensions (fullscreen)
                    if (
                        windowRect.left == monitorRect.left
                        && windowRect.top == monitorRect.top
                        && windowRect.right == monitorRect.right
                        && windowRect.bottom == monitorRect.bottom
                    )
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid); // Gets the process ID of the fullscreen window
                        Console.WriteLine($"Fullscreen app detected, PID: {pid}");
                        return pid;
                    }

                    Console.WriteLine("Window not fullscreen");
                    return 0u;
                })
                .ConfigureAwait(false);
        }
    }
}
