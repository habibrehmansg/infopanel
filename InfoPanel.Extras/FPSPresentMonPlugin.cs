using InfoPanel.Plugins;
using PresentMonFps;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Vanara.PInvoke;

namespace InfoPanel.Extras
{
    public class FpsPlugin : BasePlugin
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS Presentmon");
        private CancellationTokenSource? _cancellationTokenSource;

        public FpsPlugin() : base("fps-plugin", "FPS Info - PresentMonFPS", "Retrieves FPS information periodically using PresentMonFPS.")
        {
        }

        private string? _configFilePath = null;
        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            StartFPSMonitoring(_cancellationTokenSource.Token);
        }

        public override void Close()
        {
            _cancellationTokenSource?.Cancel();
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
            await GetFps();
        }

        private async void StartFPSMonitoring(CancellationToken cancellationToken)
        {
            uint pid = await GetActiveFullscreenProcessIdAsync();
            if (pid == 0)
            {
                Debug.WriteLine("No fullscreen application found.");
                return;
            }

            var fpsRequest = new FpsRequest
            {
                TargetPid = pid // Ensure TargetPid is set correctly
            };

            Debug.WriteLine($"Starting FPS monitoring for process ID: {pid}");

            await FpsInspector.StartForeverAsync(fpsRequest, (result) =>
            {
                _fpsSensor.Value = (float)result.Fps;
                Debug.WriteLine($"FPS: {result.Fps}"); // Log the FPS value
            }, cancellationToken);
        }

        private async Task GetFps()
        {
            uint pid = await GetActiveFullscreenProcessIdAsync();
            if (pid == 0)
            {
                Debug.WriteLine("No fullscreen application found.");
                return;
            }

            var fpsRequest = new FpsRequest
            {
                TargetPid = pid // Ensure TargetPid is set correctly
            };

            Debug.WriteLine($"Getting FPS for process ID: {pid}");

            var fpsResult = await FpsInspector.StartOnceAsync(fpsRequest);
            _fpsSensor.Value = (float)fpsResult.Fps;
            Debug.WriteLine($"FPS: {fpsResult.Fps}"); // Log the FPS value
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

                GetWindowThreadProcessId(hWnd, out uint pid);
                return pid;
            });
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}

