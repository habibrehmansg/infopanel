using InfoPanel.Plugins;
using PresentMonFps;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace InfoPanel.Extras
{
    // FpsPlugin class extends BasePlugin to monitor FPS using PresentMonFPS
    public class FpsPlugin : BasePlugin
    {
        // Sensor to store FPS values
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        // CancellationTokenSource to manage async task cancellation
        private CancellationTokenSource? _cancellationTokenSource;

        // Constructor initializing the base plugin with specific details
        public FpsPlugin() : base("fps-plugin", "FPS Info - PresentMonFPS", "Retrieves FPS information periodically using PresentMonFPS.")
        {
        }

        // ConfigFilePath property returns null as no config file is used
        public override string? ConfigFilePath => null;
        // UpdateInterval property specifies the update interval as 1 second
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initialize method starts the FPS monitoring
        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _ = StartFPSMonitoringAsync(_cancellationTokenSource.Token);
        }

        // Close method cancels and disposes the CancellationTokenSource
        public override void Close()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }

        // Load method adds the FPS sensor to the provided containers
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            containers.Add(container);
        }

        // Update method throws NotImplementedException as it's not implemented
        public override void Update()
        {
            throw new NotImplementedException();
        }

        // UpdateAsync method retrieves FPS asynchronously
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await GetFpsAsync().ConfigureAwait(false);
        }

        // StartFPSMonitoringAsync method continuously monitors FPS
        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Get the process ID of the active fullscreen application
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                    if (pid == 0)
                    {
                        // No fullscreen application found, wait for the next interval
                        await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Create an FPS request for the target process ID
                    var fpsRequest = new FpsRequest
                    {
                        TargetPid = pid // Ensure TargetPid is set correctly
                    };

                    // Start continuous FPS monitoring
                    await FpsInspector.StartForeverAsync(fpsRequest, (result) =>
                    {
                        // Update the FPS sensor value with the retrieved FPS
                        _fpsSensor.Value = (float)result.Fps;
                    }, cancellationToken).ConfigureAwait(false);

                    // Wait for the next update interval
                    await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, no need to handle this exception
            }
            catch (Exception)
            {
                // Handle other exceptions if necessary
            }
        }

        // GetFpsAsync method retrieves FPS once
        private async Task GetFpsAsync()
        {
            try
            {
                // Get the process ID of the active fullscreen application
                uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                if (pid == 0)
                {
                    // No fullscreen application found, return
                    return;
                }

                // Create an FPS request for the target process ID
                var fpsRequest = new FpsRequest
                {
                    TargetPid = pid // Ensure TargetPid is set correctly
                };

                // Retrieve FPS information once
                var fpsResult = await FpsInspector.StartOnceAsync(fpsRequest).ConfigureAwait(false);
                // Update the FPS sensor value with the retrieved FPS
                _fpsSensor.Value = (float)fpsResult.Fps;
            }
            catch (Exception)
            {
                // Handle exceptions if necessary
            }
        }

        // GetActiveFullscreenProcessIdAsync method retrieves the process ID of the active fullscreen window
        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
            {
                // Get the handle of the foreground window
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                {
                    return 0u;
                }

                // Get the process ID associated with the foreground window
                GetWindowThreadProcessId(hWnd, out uint pid);
                return pid;
            }).ConfigureAwait(false);
        }

        // Import GetForegroundWindow function from user32.dll
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Import GetWindowThreadProcessId function from user32.dll
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}

