using System.Diagnostics;

namespace InfoPanel.Hosting;

/// <summary>The OS whose browser openers <see cref="BrowserLauncher"/> uses (a parameter, so every OS's commands are tested anywhere).</summary>
public enum BrowserOS
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Opens the editor in a browser, only when the user asks for it (the engine never opens one by itself).
/// <list type="bullet">
/// <item>The link carries the session token, so it never goes into a process's arguments (another user can read
/// those on some systems): it goes into a private redirect file (<see cref="RedirectFiles"/>), and only the file's path
/// goes to the browser.</item>
/// <item>A web client's pairing link (<see cref="EditorLink.WebClient"/>) goes to Chrome when it is installed, else to the
/// default web browser: macOS <c>open -b com.google.Chrome</c> (which fails when no app has that id); Windows Chrome's
/// <c>chrome.exe</c> from App Paths; Linux <c>google-chrome-stable</c> or <c>google-chrome</c> on the <c>PATH</c>. The sign-in
/// link goes to the default web browser. A command that exits with an error within <see cref="StartupWindow"/> failed,
/// and the next is tried.</item>
/// <item>The default web browser is the one the system opens <c>http</c> links with (<see cref="DefaultBrowser"/>), named
/// in the command, so the file never reaches the app that opens <c>.html</c> files (which may be a text editor): macOS
/// <c>open -a {app}</c>, Windows the <c>http</c> association's command, Linux the <c>Exec</c> line of
/// <c>xdg-settings</c>' desktop entry. Only when it can't be told does the file go by its association: <c>open</c>, the
/// shell, <c>xdg-open</c>.</item>
/// <item>A failure is logged without the link: the error to the console only, its type to the log file and the entries
/// kept for clients.</item>
/// </list>
/// The process start is the seam tests replace, so no test opens a browser.
/// </summary>
public class BrowserLauncher(
    ILogger<BrowserLauncher> logger,
    RedirectFiles redirects,
    Func<ProcessStartInfo, int> run,
    Func<string?>? findChrome = null,
    BrowserOS? os = null,
    Func<string?>? findBrowser = null)
{
    /// <summary>Chrome's bundle id on macOS.</summary>
    public const string ChromeBundleId = "com.google.Chrome";

    /// <summary>Chrome's names on Linux, in the order they are looked for on the <c>PATH</c>.</summary>
    public static readonly IReadOnlyList<string> LinuxChromeNames = ["google-chrome-stable", "google-chrome"];

    private readonly BrowserOS _os = os ?? ThisOS();
    private readonly Func<string?> _findChrome = findChrome ?? FindChrome;
    private readonly Func<string?> _findBrowser = findBrowser ?? (() => DefaultBrowser.Find(os ?? ThisOS()));

    /// <summary>The launcher that starts real processes on this OS.</summary>
    public BrowserLauncher(ILogger<BrowserLauncher> logger, RedirectFiles redirects) : this(logger, redirects, Run) { }

    public virtual void Open(EditorLink link)
    {
        string file;
        try
        {
            file = redirects.Write(link.Url);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Never a fallback to the link in the arguments: the user opens the link the console printed instead.
            Failed(ex, "the private link file couldn't be written");
            return;
        }

        var commands = Commands(_os, file, link.WebClient, link.WebClient && _os != BrowserOS.MacOS ? _findChrome() : null, FindBrowser());
        Exception? error = null;
        int? exit = null;
        for (var i = 0; i < commands.Count; i++)
        {
            try
            {
                exit = run(commands[i]);
                error = null;
                if (exit == 0)
                {
                    if (i > 0)
                        logger.LogInformation("Chrome isn't installed or didn't start; opened the editor in the default browser");
                    return;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        redirects.Remove(file);
        Failed(error, error == null ? $"exit code {exit}" : null);
    }

    /// <summary>The default web browser, or null when it can't be told (or finding it failed).</summary>
    private string? FindBrowser()
    {
        try
        {
            return _findBrowser();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not tell the default web browser ({Error}); opening the editor by the file's association", ex.GetType().FullName);
            return null;
        }
    }

    /// <summary>
    /// The commands that open <paramref name="file"/>, in the order tried: Chrome first for a web client's link when it is
    /// there (<paramref name="chrome"/>, its path on Windows and Linux; on macOS Launch Services finds it by bundle id),
    /// then the default web browser (<paramref name="browser"/>, in <see cref="DefaultBrowser"/>'s form for
    /// <paramref name="os"/>), or the file's association when that is null. Nothing but the file's path is passed.
    /// </summary>
    public static IReadOnlyList<ProcessStartInfo> Commands(BrowserOS os, string file, bool webClient, string? chrome, string? browser)
    {
        var commands = new List<ProcessStartInfo>();
        switch (os)
        {
            case BrowserOS.MacOS:
                if (webClient)
                    commands.Add(Command("open", "-b", ChromeBundleId, file));
                commands.Add(browser != null ? Command("open", "-a", browser, file) : Command("open", file));
                break;
            case BrowserOS.Windows:
                if (webClient && chrome != null)
                    commands.Add(Command(chrome, file));
                commands.Add((browser != null ? DefaultBrowser.WindowsCommand(browser, file) : null)
                    ?? new ProcessStartInfo(file) { UseShellExecute = true });
                break;
            default:
                if (webClient && chrome != null)
                    commands.Add(Command(chrome, file));
                commands.Add((browser != null ? DefaultBrowser.LinuxCommand(browser, file) : null) ?? Command("xdg-open", file));
                break;
        }

        return commands;
    }

    private static ProcessStartInfo Command(string program, params string[] arguments)
    {
        var command = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
            command.ArgumentList.Add(argument);
        return command;
    }

    /// <summary>
    /// How long a program started to open the file gets to fail: one that exits within it with an error didn't open it
    /// (a Chrome that can't start), and the next command is tried; one still running, or done without an error (Chrome
    /// handing the file to a Chrome already running), did.
    /// </summary>
    public static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(2);

    /// <summary>The real runner: <see cref="Run(ProcessStartInfo, TimeSpan)"/> within <see cref="StartupWindow"/>.</summary>
    internal static int Run(ProcessStartInfo command) => Run(command, StartupWindow);

    /// <summary>
    /// Starts <paramref name="command"/> and answers its exit code once it has exited, or 0 when it is still running:
    /// macOS's <c>open</c> is waited for up to 10 s (it returns once Launch Services has the file, non-zero when no app has
    /// the bundle id); anything else for <paramref name="startup"/>. A shell start that hands the file to an app already
    /// running starts no process, and counts as opened.
    /// </summary>
    internal static int Run(ProcessStartInfo command, TimeSpan startup)
    {
        using var process = Process.Start(command);
        if (process == null)
            return 0;
        var wait = command.FileName == "open" ? TimeSpan.FromSeconds(10) : startup;
        return process.WaitForExit(wait) ? process.ExitCode : 0;
    }

    private void Failed(Exception? error, string? reason)
    {
        // The error names what was started (the redirect file, never the link); still, it goes to the console only, as
        // the link does. The log file, and the entries kept for clients, get the failure by the error's type.
        using (logger.BeginScope(LogTargets.Only(LogTargets.Console)))
            logger.LogWarning(error, "Could not open the browser{Reason}; open the editor link above instead", reason == null ? "" : $" ({reason})");
        using (logger.BeginScope(LogTargets.Only(LogTargets.File)))
            logger.LogWarning("Could not open the browser ({Error}); open the editor link the engine printed on its console instead",
                error?.GetType().FullName ?? reason);
    }

    private static BrowserOS ThisOS() =>
        OperatingSystem.IsMacOS() ? BrowserOS.MacOS : OperatingSystem.IsWindows() ? BrowserOS.Windows : BrowserOS.Linux;

    /// <summary>Chrome on this computer, for Windows and Linux (macOS asks Launch Services by bundle id): its path, or null.</summary>
    private static string? FindChrome()
    {
        if (OperatingSystem.IsWindows())
            return FindChromeInAppPaths();
        if (OperatingSystem.IsMacOS())
            return null;
        return FindChromeOnPath(Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>The first of <see cref="LinuxChromeNames"/> that is an executable file in a folder of <paramref name="path"/>.</summary>
    public static string? FindChromeOnPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var folders = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in LinuxChromeNames)
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & UnixFileMode.UserExecute) != 0))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Chrome's <c>chrome.exe</c> as App Paths registers it, for the user and then the computer.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindChromeInAppPaths()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var registered = hive.OpenSubKey(key);
                if (registered?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }
}
