using InfoPanel.Api;
using InfoPanel.Documents;
using InfoPanel.Hosting;

namespace InfoPanel.Shell;

/// <summary>
/// The menu-bar item's engine side (docs/2.0/desktop-shell.md): shows the icon once the server listens, so Open Editor
/// always has a port, and removes it when the engine stops; runs what the user chooses in its menu.
/// <list type="bullet">
/// <item><b>Open Editor</b> opens the editor through <see cref="EditorOpener"/>.</item>
/// <item><b>Quit InfoPanel</b> stops the engine at once when no profile has unsaved changes; otherwise it asks first,
/// naming them: Open Editor (the default), Cancel, or Quit Without Saving.</item>
/// <item>A system quit (logout, shutdown, Activity Monitor) stops the engine without asking.</item>
/// </list>
/// Handlers leave the platform's UI thread before they do anything, so a prompt or a browser start never holds it.
/// The host setting <see cref="TestPromptSetting"/> (tests set <c>Engine__ShellTestPrompt</c>) asks the Quit prompt once
/// the icon shows, so a test can stop an engine whose alert is open; it is read only in the host environment
/// <see cref="TestEnvironment"/> (<c>ASPNETCORE_ENVIRONMENT=Test</c>) and ignored in every other.
/// </summary>
public sealed class ShellService(
    IShellPlatform platform,
    IHostApplicationLifetime lifetime,
    EngineRuntime runtime,
    Workspace workspace,
    EditorOpener editor,
    ILogger<ShellService> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : IHostedService
{
    /// <summary>How many unsaved profiles the Quit prompt names before it says how many more.</summary>
    public const int NamedProfiles = 3;

    /// <summary>The host setting that asks the Quit prompt once the icon shows (a test's; its answer is logged, nothing else follows).</summary>
    public const string TestPromptSetting = "Engine:ShellTestPrompt";

    /// <summary>The host environment a test engine runs in (<c>ASPNETCORE_ENVIRONMENT</c>), the only one that reads <see cref="TestPromptSetting"/>.</summary>
    public const string TestEnvironment = "Test";

    private CancellationTokenRegistration _started;
    private CancellationTokenRegistration _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsSupported)
            return Task.CompletedTask;

        platform.TerminateRequested += OnTerminateRequested;
        _started = lifetime.ApplicationStarted.Register(() =>
        {
            platform.Show(new ShellIcon($"InfoPanel — {runtime.BaseUrl}"), () => ShellMenu.Build(new ShellState(ReadEndpoints.EngineVersion())), InvokedAsync);
            if (environment?.IsEnvironment(TestEnvironment) == true && bool.TryParse(configuration?[TestPromptSetting], out var ask) && ask)
                _ = AskTestPromptAsync();
        });
        _stopping = lifetime.ApplicationStopping.Register(platform.Remove);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        _stopping.Dispose();
        platform.TerminateRequested -= OnTerminateRequested;
        return Task.CompletedTask;
    }

    private async Task AskTestPromptAsync()
    {
        var answer = await platform.ConfirmAsync(QuitPrompt(["Test"])).ConfigureAwait(false);
        logger.LogInformation("The test prompt was answered {Answer}", answer);
    }

    private void OnTerminateRequested()
    {
        logger.LogInformation("The system asked InfoPanel to quit");
        _ = Task.Run(lifetime.StopApplication);
    }

    private async Task InvokedAsync(string id)
    {
        // Off the UI thread: the browser start and the prompt's wait happen on the thread pool.
        await Task.Yield();
        await Task.Run(async () =>
        {
            try
            {
                switch (id)
                {
                    case ShellMenu.OpenEditor:
                        editor.Open();
                        break;
                    case ShellMenu.Quit:
                        await QuitAsync().ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The menu's {Item} failed", id);
            }
        }).ConfigureAwait(false);
    }

    private async Task QuitAsync()
    {
        var unsaved = workspace.List().Where(e => e.Dirty == true).Select(e => e.Name ?? e.Id).ToList();
        if (unsaved.Count == 0)
        {
            logger.LogInformation("Quit from the menu");
            lifetime.StopApplication();
            return;
        }

        var answer = await platform.ConfirmAsync(QuitPrompt(unsaved)).ConfigureAwait(false);
        switch (answer)
        {
            case 0:
                editor.Open();
                break;
            case 2:
                logger.LogInformation("Quit from the menu with {Count} profiles unsaved", unsaved.Count);
                lifetime.StopApplication();
                break;
        }
    }

    /// <summary>"Quit InfoPanel?", naming up to <see cref="NamedProfiles"/> unsaved profiles, then how many more.</summary>
    public static ShellPrompt QuitPrompt(IReadOnlyList<string> unsaved)
    {
        var named = unsaved.Take(NamedProfiles).Select(n => $"“{n}”").ToList();
        var more = unsaved.Count - named.Count;
        var who = more > 0
            ? $"{string.Join(", ", named)} and {more} more"
            : named.Count == 1 ? named[0] : $"{string.Join(", ", named[..^1])} and {named[^1]}";
        var verb = unsaved.Count == 1 ? "has" : "have";
        return new ShellPrompt(
            "Quit InfoPanel?",
            $"{who} {verb} unsaved changes. InfoPanel keeps them for its next start, unsaved.",
            ["Open Editor", "Cancel", "Quit Without Saving"],
            DefaultButton: 0,
            CancelButton: 1);
    }
}
