using System.CommandLine;
using System.IO.Pipes;
using InfoPanel.Plugins.Host;
using Serilog;
using StreamJsonRpc;

var pipeOption = new Option<string>("--pipe") { Description = "Named pipe name", Required = true };
var pluginOption = new Option<string>("--plugin") { Description = "Path to plugin DLL", Required = true };

var rootCommand = new RootCommand("InfoPanel Plugin Host Process");
rootCommand.Options.Add(pipeOption);
rootCommand.Options.Add(pluginOption);

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var pipeName = parseResult.GetValue(pipeOption)!;
    var pluginPath = parseResult.GetValue(pluginOption)!;

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
        await pipeStream.ConnectAsync(30000, cancellationToken);
        Log.Information("Connected to pipe");

        var hostService = new HostService(pluginPath);

        using var jsonRpc = new JsonRpc(pipeStream, pipeStream);
        jsonRpc.AddLocalRpcTarget(hostService);
        hostService.SetJsonRpc(jsonRpc);
        hostService.OnShutdownRequested = () => jsonRpc.Dispose();
        jsonRpc.StartListening();

        Log.Information("JSON-RPC started, waiting for requests...");
        await jsonRpc.Completion;
        return 0;
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Plugin host fatal error");
        return 1;
    }
    finally
    {
        Log.Information("Plugin host shutting down");
        Log.CloseAndFlush();
    }
});

return await rootCommand.Parse(args).InvokeAsync();
