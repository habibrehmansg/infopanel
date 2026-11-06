using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using InfoPanel.Api;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Service;
using InfoPanel.Settings;
using InfoPanel.Spec;

namespace InfoPanel.Shell;

/// <summary>
/// The menu-bar item's engine side (docs/2.0/desktop-shell.md): shows the icon once the server listens, so Open Editor
/// always has a port, and removes it when the engine stops; builds the menu from the engine's state each time it opens
/// (<see cref="ShellMenu.Build"/>) and runs what the user chooses in it. While the icon can show, it runs
/// <see cref="ServiceHolder"/>, which asks the configured service who holds the credential, for the signed-in line.
/// <list type="bullet">
/// <item><b>Open Editor</b> (and Show on Desktop's <b>More in the Editor…</b>) opens the editor through <see cref="EditorOpener"/>;
/// <b>Edit from Another Computer…</b> and the row of who is signed in open it at its sign-in settings, where another
/// computer is set up to edit this one (<see cref="EditorRoutes.AccountSettings"/>).</item>
/// <item><b>Show on Desktop</b>'s profiles turn a profile's desktop window on or off as the editor's switch does: one
/// <c>profile</c> commit of <c>window.enabled</c> at the revision read (read again and retried on a conflict), saved at
/// once at that revision when the profile had no unsaved changes before it, else left unsaved with them.</item>
/// <item><b>Show Data Folder in Finder</b> and <b>Open Logs Folder</b> open the engine's own folders through
/// <see cref="IFolderOpener"/>, as <c>POST /api/folders/{folder}/open</c> does.</item>
/// <item><b>Copy Engine Details</b> puts <see cref="EngineDetails"/> on the clipboard: never the token, a link or a profile.</item>
/// <item><b>Quit InfoPanel</b> stops the engine at once when no profile has unsaved changes; otherwise it asks first,
/// naming them: Open Editor (the default), Cancel, or Quit Without Saving.</item>
/// <item>A system quit (logout, shutdown, Activity Monitor) stops the engine without asking.</item>
/// <item>Opening the app again while it runs (Finder or the Dock on macOS) runs Open Editor.</item>
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
    ServiceHolder holder,
    DataPaths paths,
    IFolderOpener folders,
    DisplayWindowManager windows,
    ILogger<ShellService> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : IHostedService
{
    /// <summary>How many unsaved profiles the Quit prompt names before it says how many more.</summary>
    public const int NamedProfiles = 3;

    /// <summary>How many times Show on Desktop reads the profile again after a conflict before it gives up.</summary>
    internal const int CommitAttempts = 3;

    /// <summary>The host setting that asks the Quit prompt once the icon shows (a test's; its answer is logged, nothing else follows).</summary>
    public const string TestPromptSetting = "Engine:ShellTestPrompt";

    /// <summary>The host environment a test engine runs in (<c>ASPNETCORE_ENVIRONMENT</c>), the only one that reads <see cref="TestPromptSetting"/>.</summary>
    public const string TestEnvironment = "Test";

    private CancellationTokenRegistration _started;
    private CancellationTokenRegistration _stopping;

    /// <summary>The user's home folder and how its volume compares paths, probed once, at the first Copy Engine Details.</summary>
    private readonly Lazy<(string Home, StringComparison Rule)> _home = new(() =>
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return (home, EngineDetails.CaseRule(home, EngineDetails.IsCaseSensitive));
    });

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsSupported)
            return Task.CompletedTask;

        platform.TerminateRequested += OnTerminateRequested;
        platform.ReopenRequested += OnReopenRequested;
        _started = lifetime.ApplicationStarted.Register(() =>
        {
            platform.Show(new ShellIcon($"InfoPanel — {runtime.BaseUrl}"), Menu, InvokedAsync);
            if (environment?.IsEnvironment(TestEnvironment) == true && bool.TryParse(configuration?[TestPromptSetting], out var ask) && ask)
                _ = AskTestPromptAsync();
        });
        _stopping = lifetime.ApplicationStopping.Register(platform.Remove);
        // Who holds the service credential, for the signed-in line: asked only while there is a menu to show it, and again
        // at once when the network changes after a failed ask.
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        holder.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        _stopping.Dispose();
        platform.TerminateRequested -= OnTerminateRequested;
        platform.ReopenRequested -= OnReopenRequested;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        await holder.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => holder.NetworkChanged();

    /// <summary>The menu as it opens, from the engine's state now; a state that can't be read leaves the lines that need none.</summary>
    private ShellMenu Menu()
    {
        try
        {
            return ShellMenu.Build(State());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The menu couldn't read the engine's state");
            return ShellMenu.Build(new ShellState(ReadEndpoints.EngineVersion()) { HasCredential = true });
        }
    }

    /// <summary>
    /// The engine's state for the menu: the credential and its holder's name and email, and, when desktop windows are on, the
    /// profiles that can be opened in the library's order (one past what the menu lists, so it knows there are more),
    /// each with its window on or off.
    /// </summary>
    private ShellState State()
    {
        var desktop = windows.Enabled;
        var profiles = new List<ShellProfile>();
        if (desktop)
        {
            foreach (var entry in workspace.List())
            {
                if (profiles.Count > ShellMenu.DesktopProfiles)
                    break;
                if (entry.Status != "ok" || workspace.Open(entry.Id) is not { } doc)
                    continue;
                profiles.Add(new ShellProfile(entry.Id, entry.Name ?? entry.Id, doc.Current.Document.Profile.Window?.Enabled == true));
            }
        }

        var signedIn = holder.Current;
        return new ShellState(ReadEndpoints.EngineVersion())
        {
            HasCredential = signedIn.Held,
            SignedInAs = signedIn.Name,
            SignedInEmail = signedIn.Email,
            Profiles = profiles,
            DesktopWindows = desktop,
        };
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

    /// <summary>The app was opened again while it runs (Finder or the Dock on macOS): Open Editor, as a second launch does.</summary>
    private void OnReopenRequested()
    {
        logger.LogInformation("InfoPanel was opened again: opening the editor");
        _ = InvokedAsync(ShellMenu.OpenEditor);
    }

    private async Task InvokedAsync(string id)
    {
        // Off the UI thread: the browser start, the commit, the file manager and the prompt's wait happen on the thread pool.
        await Task.Yield();
        await Task.Run(async () =>
        {
            try
            {
                switch (id)
                {
                    case ShellMenu.OpenEditor or ShellMenu.MoreProfiles:
                        editor.Open();
                        break;
                    case ShellMenu.EditFromAnotherComputer:
                        editor.Open(EditorRoutes.AccountSettings);
                        break;
                    case ShellMenu.SignedIn:
                        editor.Open(EditorRoutes.AccountSettings);
                        break;
                    case ShellMenu.ShowDataFolder:
                        folders.Open(paths.Root);
                        break;
                    case ShellMenu.OpenLogsFolder:
                        folders.Open(paths.LogsDirectory);
                        break;
                    case ShellMenu.CopyEngineDetails:
                        platform.CopyText(Details().Text(_home.Value.Home, _home.Value.Rule));
                        logger.LogInformation("Copied the engine's details from the menu");
                        break;
                    case ShellMenu.Quit:
                        await QuitAsync().ConfigureAwait(false);
                        break;
                    default:
                        if (ShellMenu.TryReadDesktopItem(id, out var profile, out var show))
                            SetShown(profile, show);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The menu's {Item} failed", id);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The engine's details as the session reports them, for Copy Engine Details.</summary>
    private EngineDetails Details() => new(
        ReadEndpoints.EngineVersion(), ReadEndpoints.EngineBuild(), ReadEndpoints.Protocol, ReadEndpoints.Platform(),
        ReadEndpoints.OsVersion(), ReadEndpoints.Arch(), ReadEndpoints.Timestamp(runtime.Started), runtime.BaseUrl, paths.Root);

    /// <summary>
    /// Turns the profile's desktop window on (<paramref name="show"/>) or off, as the editor's Show on desktop does: when it
    /// isn't so already, one <c>profile</c> commit of the whole <c>window</c> as read with <c>enabled</c> changed, at the
    /// revision read (a conflict reads again, up to <see cref="CommitAttempts"/> times, so another client's window change is
    /// kept), saved at once at that revision when the profile had no unsaved changes before it.
    /// </summary>
    private void SetShown(string id, bool show)
    {
        if (workspace.Open(id) is not { } doc)
        {
            logger.LogWarning("Show on Desktop: there is no profile {ProfileId} that can be opened", id);
            return;
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var current = doc.Current;
                var window = current.Document.Profile.Window;
                if ((window?.Enabled == true) == show)
                    return;

                var body = JsonSerializer.SerializeToUtf8Bytes(new CommitRequest
                {
                    OpId = Guid.NewGuid().ToString("N"),
                    Instance = doc.Instance,
                    BaseRev = current.Rev,
                    Ops = [new ProfileOp { BaseRev = current.Rev, Set = new ProfileFields { Window = (window ?? new WindowSettings { Enabled = show }) with { Enabled = show } } }],
                }, SpecJson.Options);
                var outcome = doc.Commit(body, out var wasClean);
                if ((outcome.Conflict is not null || outcome.Expired is not null) && attempt < CommitAttempts)
                    continue;

                if (outcome.Result is not { } result)
                {
                    var reason = outcome.Invalid?.Errors.FirstOrDefault()?.Message ?? outcome.Conflict?.Detail ?? outcome.Expired?.Detail;
                    logger.LogWarning("Show on Desktop couldn't change profile {ProfileId} ({Status}: {Reason})", id, outcome.Status, reason);
                    return;
                }

                var saved = wasClean && doc.Save(new HistoryRequest { Instance = doc.Instance, BaseRev = result.Rev }).Result is not null;
                logger.LogInformation("Show on Desktop turned the desktop window of profile {ProfileId} {State}{Saved}", id, show ? "on" : "off",
                    saved ? ", saved" : wasClean ? ", not saved: it changed meanwhile" : ", unsaved with its other changes");
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            logger.LogWarning("Show on Desktop: profile {ProfileId} was deleted meanwhile", id);
        }
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

/// <summary>
/// What Copy Engine Details puts on the clipboard (docs/2.0/desktop-shell.md): what a bug report needs when the editor
/// can't connect, with the labels of the editor's own details for a bug report, one a line inside <c>```</c> fences. The
/// values are the session's (<c>GET /api/session</c>); the data folder with the home folder as <c>~</c>. Never the token,
/// a link, a profile or a reading.
/// </summary>
public sealed record EngineDetails(
    string Version, string Build, int Protocol, string Platform, string OsVersion, string Arch, string Started, string Address, string DataDir)
{
    /// <summary>
    /// How this computer's file systems compare paths by default: without case on macOS (its default volumes) and Windows,
    /// with case elsewhere. The rule when the home folder's volume can't be asked.
    /// </summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// How paths under <paramref name="home"/> compare: as <paramref name="caseSensitive"/> says of its volume (a macOS
    /// volume may be case-sensitive), else, when it can't tell or there is no home folder, <see cref="PathComparison"/>.
    /// </summary>
    internal static StringComparison CaseRule(string? home, Func<string, bool?> caseSensitive)
    {
        if (string.IsNullOrEmpty(home))
            return PathComparison;
        return caseSensitive(home) switch
        {
            true => StringComparison.Ordinal,
            false => StringComparison.OrdinalIgnoreCase,
            null => PathComparison,
        };
    }

    /// <summary>
    /// Whether the volume holding <paramref name="path"/> tells names apart by case: on macOS as the volume says
    /// (<c>pathconf</c>'s <c>_PC_CASE_SENSITIVE</c>; null when it can't be asked, a path that doesn't exist included);
    /// never on Windows; always elsewhere.
    /// </summary>
    internal static bool? IsCaseSensitive(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;
        if (!OperatingSystem.IsMacOS())
            return true;
        return PathConf(path, PcCaseSensitive) switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
    }

    /// <summary>macOS's <c>_PC_CASE_SENSITIVE</c> (<c>sys/unistd.h</c>).</summary>
    private const int PcCaseSensitive = 11;

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pathconf")]
    private static extern nint PathConf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);

    /// <summary>The text, the data folder under <paramref name="home"/> (the user's home folder, or null) written from <c>~</c>.</summary>
    public string Text(string? home) => Text(home, CaseRule(home, IsCaseSensitive));

    /// <summary>As <see cref="Text(string?)"/>, the paths compared with <paramref name="comparison"/>.</summary>
    internal string Text(string? home, StringComparison comparison) => string.Join("\n",
        "```",
        "InfoPanel engine details",
        $"Engine: {Version}, protocol {Protocol}, {PlatformName(Platform)}",
        $"Engine build: {Build}",
        $"Engine started: {Started}",
        $"OS version: {OsVersion}",
        $"Architecture: {Arch}",
        $"Engine address: {Address}",
        $"Data folder: {WithoutHome(DataDir, home, comparison)}",
        "```");

    private static string PlatformName(string platform) => platform switch
    {
        "macos" => "macOS",
        "windows" => "Windows",
        "linux" => "Linux",
        _ => platform,
    };

    /// <summary>
    /// <paramref name="path"/> with <paramref name="home"/> as <c>~</c> when it is that folder or inside it (a folder's end
    /// next, so <c>/Users/annabel</c> isn't inside <c>/Users/ann</c>), compared with <paramref name="comparison"/>.
    /// </summary>
    private static string WithoutHome(string path, string? home, StringComparison comparison)
    {
        var root = home?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(root) || !path.StartsWith(root, comparison))
            return path;
        if (path.Length == root.Length)
            return "~";
        return path[root.Length] is '/' or '\\' ? "~" + path[root.Length..] : path;
    }
}
