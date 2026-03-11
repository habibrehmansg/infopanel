using System.CommandLine;
using System.Diagnostics;
using System.IO.Pipes;
using InfoPanel.Plugins.Host;
using Serilog;
using StreamJsonRpc;

var pipeOption = new Option<string>("--pipe", "Named pipe name") { IsRequired = true };
var pluginOption = new Option<string>("--plugin", "Path to plugin DLL") { IsRequired = true };
var parentPidOption = new Option<int>("--parent-pid", "Parent process ID for watchdog") { IsRequired = true };

var rootCommand = new RootCommand("InfoPanel Plugin Host Process");
rootCommand.AddOption(pipeOption);
rootCommand.AddOption(pluginOption);
rootCommand.AddOption(parentPidOption);

rootCommand.SetHandler(async (string pipeName, string pluginPath, int parentPid) =>
{
    var logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InfoPanel", "logs", $"plugin-host-{Path.GetFileNameWithoutExtension(pluginPath)}.log");

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 3)
        .CreateLogger();

    Log.Information("Plugin host starting for {PluginPath} on pipe {PipeName}", pluginPath, pipeName);

    // Parent process watchdog
    _ = Task.Run(async () =>
    {
        try
        {
            var parentProcess = Process.GetProcessById(parentPid);
            await parentProcess.WaitForExitAsync();
            Log.Warning("Parent process {Pid} exited, shutting down", parentPid);
            Environment.Exit(0);
        }
        catch (ArgumentException)
        {
            Log.Warning("Parent process {Pid} not found, shutting down", parentPid);
            Environment.Exit(0);
        }
    });

    using var pipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    try
    {
        Log.Information("Connecting to pipe {PipeName}...", pipeName);
        await pipeStream.ConnectAsync(30000);
        Log.Information("Connected to pipe");

        var hostService = new HostService(pluginPath);

        using var jsonRpc = JsonRpc.Attach(pipeStream, hostService);
        hostService.SetJsonRpc(jsonRpc);

        Log.Information("JSON-RPC attached, waiting for requests...");
        await jsonRpc.Completion;
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
}, pipeOption, pluginOption, parentPidOption);

return await rootCommand.InvokeAsync(args);
