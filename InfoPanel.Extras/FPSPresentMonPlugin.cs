using InfoPanel.Plugins;
using PresentMonFps;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

/*
 * Summary of Changes:
 * 1. Optimized Fullscreen Detection: Enhanced GetActiveFullscreenProcessIdAsync to verify fullscreen status by comparing window and monitor rectangles.
 * 2. Efficient Monitoring Task Management: Modified StartFPSMonitoringAsync to manage a single monitoring task, canceling previous tasks on process change, running in background.
 * 3. Fixed Type Mismatch: Cast Marshal.SizeOf<MONITORINFO>() from int to uint for MONITORINFO.cbSize to fix compilation error.
 * 4. Added Static Import: Included 'using static Vanara.PInvoke.User32' to simplify User32 method calls.
 * 5. Reset FPS on App Close: Added _fpsSensor.Value = 0 when no fullscreen app is detected in StartFPSMonitoringAsync and GetFpsAsync.
 */

namespace InfoPanel.Extras
{
    public class FpsPlugin : BasePlugin
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationTokenSource? _monitoringCts; // For managing the monitoring task
        private uint _currentPid; // Tracks the current fullscreen process ID

        public FpsPlugin() : base("fps-plugin", "FPS Info - PresentMonFPS", "Retrieves FPS information periodically using PresentMonFPS.")
        {
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _ = StartFPSMonitoringAsync(_cancellationTokenSource.Token);
        }

        public override void Close()
        {
            _cancellationTokenSource?.Cancel();
            _monitoringCts?.Cancel();
            _cancellationTokenSource?.Dispose();
            _monitoringCts?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            containers.Add(container);
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            // Note: Could be made a no-op if StartFPSMonitoringAsync is sufficient
            await GetFpsAsync().ConfigureAwait(false);
        }

        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);

                    if (pid == 0 || pid != _currentPid)
                    {
                        _monitoringCts?.Cancel();
                        _monitoringCts?.Dispose();
                        _currentPid = 0;

                        if (pid == 0)
                        {
                            // Change 5: Reset FPS to 0 when no fullscreen app is detected
                            _fpsSensor.Value = 0;
                        }
                        else // New fullscreen process detected
                        {
                            _monitoringCts = new CancellationTokenSource();
                            var fpsRequest = new FpsRequest { TargetPid = pid };
                            _currentPid = pid;

                            // Change 2: Run monitoring task in background, single instance
                            _ = Task.Run(() => FpsInspector.StartForeverAsync(
                                fpsRequest,
                                (result) => _fpsSensor.Value = (float)result.Fps,
                                _monitoringCts.Token), cancellationToken);
                        }
                    }

                    await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception)
            {
                // Consider logging exceptions in a production environment
            }
            finally
            {
                _monitoringCts?.Cancel();
                _monitoringCts?.Dispose();
                // Optional: Reset FPS on shutdown
                _fpsSensor.Value = 0;
            }
        }

        private async Task GetFpsAsync()
        {
            try
            {
                uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                if (pid == 0)
                {
                    // Change 5: Reset FPS to 0 for consistency when no fullscreen app
                    _fpsSensor.Value = 0;
                    return;
                }

                var fpsRequest = new FpsRequest { TargetPid = pid };
                var fpsResult = await FpsInspector.StartOnceAsync(fpsRequest).ConfigureAwait(false);
                _fpsSensor.Value = (float)fpsResult.Fps;
            }
            catch (Exception)
            {
                // Consider logging exceptions in a production environment
            }
        }

        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                {
                    return 0u;
                }

                if (!GetWindowRect(hWnd, out RECT windowRect))
                {
                    return 0u;
                }

                var hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero)
                {
                    return 0u;
                }

                // Change 3: Cast int to uint for cbSize to fix type mismatch
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                {
                    return 0u;
                }

                // Change 1: Compare window and monitor rectangles for fullscreen check
                var monitorRect = monitorInfo.rcMonitor;
                if (windowRect.left == monitorRect.left &&
                    windowRect.top == monitorRect.top &&
                    windowRect.right == monitorRect.right &&
                    windowRect.bottom == monitorRect.bottom)
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    return pid;
                }

                return 0u;
            }).ConfigureAwait(false);
        }
    }
}