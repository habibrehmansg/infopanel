using InfoPanel.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace InfoPanel.Hosting;

/// <summary>
/// Once the server listens: confirms the port from <see cref="IServerAddressesFeature"/> (absent under
/// <c>TestServer</c>, where the configured port stands), writes <c>runtime.json</c>, logs the sign-in links (with the token on the console only, redacted in the file log) and
/// opens the browser unless <c>--no-browser</c>. Deletes <c>runtime.json</c> on clean shutdown.
/// </summary>
public sealed class EngineLifetime(
    IHostApplicationLifetime lifetime,
    IServer server,
    EngineRuntime runtime,
    EngineOptions options,
    DataPaths paths,
    BrowserLauncher browser,
    ILogger<EngineLifetime> logger) : IHostedService
{
    private CancellationTokenRegistration _started;
    private volatile bool _wroteRuntimeFile;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = lifetime.ApplicationStarted.Register(OnStarted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        if (_wroteRuntimeFile)
            RuntimeFile.Delete(paths.RuntimeFile);
        return Task.CompletedTask;
    }

    private void OnStarted()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var listening = addresses?
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) ? uri.Port : -1)
            .FirstOrDefault(p => p > 0) ?? -1;

        if (listening > 0 && listening != runtime.Port)
        {
            logger.LogWarning("Listening on port {Actual}, not the expected {Expected}", listening, runtime.Port);
            runtime.Port = listening;
        }

        try
        {
            RuntimeFile.Write(paths.RuntimeFile, new RuntimeFile.Content(Environment.ProcessId, runtime.Port, runtime.BaseUrl));
            _wroteRuntimeFile = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Path}", paths.RuntimeFile);
        }

        logger.LogInformation("InfoPanel engine listening on {Url} (data folder {DataDir})", runtime.BaseUrl, paths.Root);
        LogSignInLinks(redacted: false);
        LogSignInLinks(redacted: true);

        if (!options.NoBrowser)
            browser.Open(runtime.AuthUrl);
    }

    /// <summary>The sign-in links: with the token to the console only, redacted to the file log only.</summary>
    private void LogSignInLinks(bool redacted)
    {
        using var scope = logger.BeginScope(LogTargets.Only(redacted ? LogTargets.File : LogTargets.Console));
        logger.LogInformation("Open the editor: {AuthUrl}", runtime.AuthUrlFor(runtime.BaseUrl, redacted));
        foreach (var origin in runtime.DevOrigins)
            logger.LogInformation("Open the editor (dev server): {AuthUrl}", runtime.AuthUrlFor(origin, redacted));
    }
}
