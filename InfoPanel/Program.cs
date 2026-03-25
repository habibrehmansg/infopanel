using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using InfoPanel.Plugins.Host;
using OpenTK.Windowing.Desktop;
using Serilog;
using StreamJsonRpc;

namespace InfoPanel
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--plugin-host")
            {
                RunPluginHost(args);
            }
            else
            {
                // Allow GLFW initialization from the DisplayWindowThread (secondary STA thread).
                // By default OpenTK checks that GLFW is called from the main thread, but
                // our overlay windows are created on a dedicated STA dispatcher thread.
                GLFWProvider.CheckForMainThread = false;

                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
        }

        private static void RunPluginHost(string[] args)
        {
            string? pipeName = null;
            string? pluginPath = null;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--pipe" && i + 1 < args.Length)
                {
                    pipeName = args[++i];
                }
                else if (args[i] == "--plugin" && i + 1 < args.Length)
                {
                    pluginPath = args[++i];
                }
            }

            if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(pluginPath))
            {
                Console.Error.WriteLine("Usage: InfoPanel.exe --plugin-host --pipe <name> --plugin <path>");
                Environment.Exit(1);
                return;
            }

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InfoPanel", "logs", $"plugin-host-{Path.GetFileNameWithoutExtension(pluginPath)}.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 3)
                .CreateLogger();

            Log.Information("Plugin host starting for {PluginPath} on pipe {PipeName}", pluginPath, pipeName);

            using var pipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                Log.Information("Connecting to pipe {PipeName}...", pipeName);
                pipeStream.ConnectAsync(30000).GetAwaiter().GetResult();
                Log.Information("Connected to pipe");

                var hostService = new HostService(pluginPath);

                using var jsonRpc = JsonRpc.Attach(pipeStream, hostService);
                hostService.SetJsonRpc(jsonRpc);

                Log.Information("JSON-RPC attached, waiting for requests...");
                jsonRpc.Completion.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Plugin host fatal error");
            }
            finally
            {
                Log.Information("Plugin host shutting down");
                Log.CloseAndFlush();
            }
        }
    }
}
