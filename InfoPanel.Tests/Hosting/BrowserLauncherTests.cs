using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The browser launcher: the link goes into a private redirect file (owner-only, in the engine's run folder) and only the
/// file's path to the browser, so no process's arguments hold the token; a web client's link goes to Chrome first, then
/// the default browser; a sign-in link to the default browser. A failure's error goes to the console only, the log file
/// gets a line naming its type. Its process start is replaced, so nothing opens.
/// </summary>
public sealed class BrowserLauncherTests : IDisposable
{
    private const string Token = "secret-token_42";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly EditorLink SignIn = new($"http://127.0.0.1:5300/auth/{Token}", WebClient: false);
    private static readonly EditorLink Pairing = new($"https://client.example/#engine=http://127.0.0.1:5300&token={Token}", WebClient: true);

    private readonly string _run = Path.Combine(Path.GetTempPath(), "ipb-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(DateTimeOffset.UnixEpoch);
    private readonly RedirectFiles _redirects;

    public BrowserLauncherTests()
    {
        _redirects = new RedirectFiles(_run, "0123456789abcdef", _time, NullLogger<RedirectFiles>.Instance);
    }

    public void Dispose()
    {
        _redirects.Dispose();
        foreach (var folder in new[] { _run, _run + "-bin" })
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A launcher for <paramref name="os"/> that runs <paramref name="run"/>, with <paramref name="chrome"/> as Chrome and
    /// <paramref name="browser"/> as the default web browser (none found when null).
    /// </summary>
    private BrowserLauncher Launcher(ILogger<BrowserLauncher> logger, Func<ProcessStartInfo, int> run, BrowserOS os = BrowserOS.MacOS, string? chrome = null,
        string? browser = null) =>
        new(logger, _redirects, run, () => chrome, os, () => browser);

    private static IEnumerable<string> Words(ProcessStartInfo command) => [command.FileName, .. command.ArgumentList, command.Arguments];

    [Fact]
    public void AStartThatFails_LogsTheErrorToTheConsoleOnly_AndALineWithoutTheErrorToTheFile()
    {
        var logger = new ScopedLogger();
        var launcher = Launcher(logger, command =>
            throw new Win32Exception($"An error occurred trying to start process '{string.Join(" ", Words(command))}'."));

        launcher.Open(SignIn);

        Assert.Equal(2, logger.Entries.Count);
        var console = Assert.Single(logger.Entries, e => e.Target == LogTargets.Console);
        Assert.Equal(LogLevel.Warning, console.Level);
        Assert.NotNull(console.Exception);
        var file = Assert.Single(logger.Entries, e => e.Target == LogTargets.File);
        Assert.Equal(LogLevel.Warning, file.Level);
        Assert.Null(file.Exception);
        Assert.Contains("Could not open the browser", file.Message);
        Assert.Contains(typeof(Win32Exception).FullName!, file.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(Token, $"{e.Message}\n{e.Exception}"));
        // A file nothing opened is removed at once.
        Assert.Empty(Directory.GetFiles(_run));
    }

    [Fact]
    public void AStartThatWorks_LogsNothing_AndHandsTheBrowserOnlyThePrivateFile()
    {
        var logger = new ScopedLogger();
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(logger, command => { started.Add(command); return 0; });

        launcher.Open(SignIn);

        var command = Assert.Single(started);
        Assert.Equal("open", command.FileName);
        var file = Assert.Single(command.ArgumentList);
        Assert.All(Words(command), w => Assert.DoesNotContain(Token, w));
        Assert.Equal(Path.GetFullPath(_run), Path.GetDirectoryName(file));
        Assert.Matches(@"^0123456789abcdef\.open-[0-9a-f]{32}\.html$", Path.GetFileName(file));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TheRedirectFile_IsOwnerOnly_InAnOwnerOnlyFolder_AndForwardsToTheLink()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(new ScopedLogger(), command => { started.Add(command); return 0; });

        launcher.Open(Pairing);

        var file = Assert.Single(started).ArgumentList.Last();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_run));
        }
        var html = File.ReadAllText(file);
        var encoded = WebUtility.HtmlEncode(Pairing.Url);
        Assert.Contains($"<meta http-equiv=\"refresh\" content=\"0;url={encoded}\">", html);
        Assert.Contains($"<a href=\"{encoded}\">", html);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", html);
        Assert.Equal(RedirectFiles.Html(Pairing.Url), html);
    }

    [Fact]
    public void TheRedirectPage_EncodesWhatItForwardsTo()
    {
        var html = RedirectFiles.Html("https://client.example/#a=\"<b>\"&c='d'");

        Assert.DoesNotContain("\"<b>\"", html);
        Assert.DoesNotContain("'d'", html);
        Assert.Contains("&quot;&lt;b&gt;&quot;&amp;c=&#39;d&#39;", html);
    }

    [Fact]
    public void TheRedirectFile_IsRemovedOnceTheBrowserHasHadTimeToReadIt()
    {
        var launcher = Launcher(new ScopedLogger(), _ => 0);

        launcher.Open(SignIn);
        var file = Assert.Single(Directory.GetFiles(_run));

        _time.Advance(RedirectFiles.ReadWindow - TimeSpan.FromSeconds(1));
        Assert.True(File.Exists(file));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(file));
        Assert.Empty(_redirects.Pending);
    }

    [Fact]
    public void RedirectFiles_AreRemovedWhenTheEngineStops_AndACrashsLeftoversAtTheNextStart()
    {
        var launcher = Launcher(new ScopedLogger(), _ => 0);
        launcher.Open(SignIn);
        launcher.Open(Pairing);
        Assert.Equal(2, Directory.GetFiles(_run).Length);

        _redirects.Dispose();
        Assert.Empty(Directory.GetFiles(_run));

        // A crash left two of this folder's files, and another data folder's engine has one of its own.
        File.WriteAllText(Path.Combine(_run, "0123456789abcdef.open-00000000000000000000000000000001.html"), "x");
        File.WriteAllText(Path.Combine(_run, "0123456789abcdef.open-00000000000000000000000000000002.html"), "x");
        File.WriteAllText(Path.Combine(_run, "fedcba9876543210.open-00000000000000000000000000000003.html"), "x");
        File.WriteAllText(Path.Combine(_run, "0123456789abcdef.launch.json"), "{}");
        using var next = new RedirectFiles(_run, "0123456789abcdef", _time, NullLogger<RedirectFiles>.Instance);
        next.RemoveLeftovers();

        Assert.Equal(["0123456789abcdef.launch.json", "fedcba9876543210.open-00000000000000000000000000000003.html"],
            Directory.GetFiles(_run).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARedirectFileThatCouldNotBeRemovedInTime_IsRemovedWhenTheEngineStops()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A folder's mode makes the removal fail here; Windows holds the file open instead.");
        var launcher = Launcher(new ScopedLogger(), _ => 0);
        launcher.Open(SignIn);
        var file = Assert.Single(Directory.GetFiles(_run));

        // The removal at the end of the read window fails (the folder can't be written in for now).
        File.SetUnixFileMode(_run, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _time.Advance(RedirectFiles.ReadWindow);
            Assert.True(File.Exists(file));
            Assert.Equal([file], _redirects.Pending);
        }
        finally
        {
            File.SetUnixFileMode(_run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _redirects.Dispose();
        Assert.False(File.Exists(file));
        Assert.Empty(_redirects.Pending);
    }

    [Fact]
    public async Task ARemovalThatFails_WhileTheEngineStops_IsTriedAgainByTheStop()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A folder's mode makes the removal fail here; Windows holds the file open instead.");
        var logger = new PausingLogger();
        var redirects = new RedirectFiles(_run, "fedcba9876543210", _time, logger);
        var file = redirects.Write($"http://127.0.0.1:5300/auth/{Token}");

        // The expiry's removal fails (the folder can't be written in for now) and is still under way, held in its warning.
        File.SetUnixFileMode(_run, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Task expiry;
        try
        {
            expiry = Task.Run(() => _time.Advance(RedirectFiles.ReadWindow), Ct);
            Assert.True(logger.Paused.Wait(TimeSpan.FromSeconds(10), Ct), "the removal never failed");
        }
        finally
        {
            File.SetUnixFileMode(_run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The file can be removed again (Windows: the browser let go of it), and the engine stops before the failed
        // removal has finished.
        // (A stop may finish at once, or wait for the removal under way.)
        var stop = Task.Run(redirects.Dispose, Ct);
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(1), Ct));
        logger.Resume.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await expiry.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(File.Exists(file));
        Assert.Empty(redirects.Pending);
    }

    /// <summary>A logger whose first warning waits until <see cref="Resume"/> is set, <see cref="Paused"/> set meanwhile.</summary>
    private sealed class PausingLogger : ILogger<RedirectFiles>
    {
        private int _warnings;

        public ManualResetEventSlim Paused { get; } = new();

        public ManualResetEventSlim Resume { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning || Interlocked.Increment(ref _warnings) != 1)
                return;
            Paused.Set();
            Resume.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void ARunFolderAnotherUserCouldWriteIn_IsNotUsed_AndNothingOpens()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_run);
        var link = _run + "-link";
        Directory.CreateSymbolicLink(link, _run);
        try
        {
            var logger = new ScopedLogger();
            using var redirects = new RedirectFiles(link, "0123456789abcdef", _time, NullLogger<RedirectFiles>.Instance);
            var started = new List<ProcessStartInfo>();
            var launcher = new BrowserLauncher(logger, redirects, command => { started.Add(command); return 0; }, () => null, BrowserOS.MacOS);

            launcher.Open(SignIn);

            Assert.Empty(started);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Could not open the browser"));
            Assert.All(logger.Entries, e => Assert.DoesNotContain(Token, $"{e.Message}\n{e.Exception}"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // ---- which browser, per OS ---------------------------------------------------------------------------------------

    [Fact]
    public void OnTheMac_AWebClientsLink_GoesToChrome_ThenTheDefaultBrowserWhenChromeIsMissing()
    {
        var started = new List<ProcessStartInfo>();
        var logger = new ScopedLogger();
        // open -b exits 1 when no app has the bundle id.
        var launcher = Launcher(logger, command => { started.Add(command); return command.ArgumentList.Contains("-b") ? 1 : 0; });

        launcher.Open(Pairing);

        Assert.Equal(2, started.Count);
        Assert.Equal(["-b", BrowserLauncher.ChromeBundleId], started[0].ArgumentList.Take(2));
        Assert.Equal("open", started[1].FileName);
        Assert.Single(started[1].ArgumentList);
        Assert.Equal(started[0].ArgumentList.Last(), started[1].ArgumentList.Last());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void OnTheMac_AWebClientsLink_OpensInChrome_WhenItIsThere()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(new ScopedLogger(), command => { started.Add(command); return 0; });

        launcher.Open(Pairing);

        var command = Assert.Single(started);
        Assert.Equal("open", command.FileName);
        Assert.Equal(["-b", BrowserLauncher.ChromeBundleId], command.ArgumentList.Take(2));
    }

    [Fact]
    public void ASignInLink_GoesToTheDefaultBrowser_NeverToChrome()
    {
        foreach (var os in new[] { BrowserOS.MacOS, BrowserOS.Windows, BrowserOS.Linux })
        {
            var started = new List<ProcessStartInfo>();
            var launcher = Launcher(new ScopedLogger(), command => { started.Add(command); return 0; }, os, chrome: "/opt/chrome/chrome");

            launcher.Open(SignIn);

            var command = Assert.Single(started);
            Assert.DoesNotContain("-b", command.ArgumentList);
            Assert.DoesNotContain("/opt/chrome/chrome", Words(command));
        }
    }

    [Fact]
    public void OnWindows_AWebClientsLink_GoesToChromeFromAppPaths_ThenTheShell()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(new ScopedLogger(), command =>
        {
            started.Add(command);
            return command.UseShellExecute ? 0 : throw new Win32Exception("The system cannot find the file specified.");
        }, BrowserOS.Windows, chrome: @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        launcher.Open(Pairing);

        Assert.Equal(2, started.Count);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", started[0].FileName);
        Assert.False(started[0].UseShellExecute);
        var file = Assert.Single(started[0].ArgumentList);
        Assert.True(started[1].UseShellExecute);
        Assert.Equal(file, started[1].FileName);
        Assert.All(started.SelectMany(Words), w => Assert.DoesNotContain(Token, w));
    }

    [Fact]
    public void OnWindows_WithoutChrome_TheShellOpensTheFile()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(new ScopedLogger(), command => { started.Add(command); return 0; }, BrowserOS.Windows, chrome: null);

        launcher.Open(Pairing);

        var command = Assert.Single(started);
        Assert.True(command.UseShellExecute);
        Assert.EndsWith(".html", command.FileName);
    }

    [Theory]
    [InlineData(BrowserOS.Linux)]
    [InlineData(BrowserOS.Windows)]
    public void AChromeThatExitsWithAnErrorAsItStarts_FallsBackToTheDefaultBrowser(BrowserOS os)
    {
        // The real runner starts a stand-in Chrome that fails at once; the default browser is only recorded.
        var chrome = StandInChrome("exit 3");
        var started = new List<ProcessStartInfo>();
        var launcher = Launcher(new ScopedLogger(), command =>
        {
            started.Add(command);
            return command.FileName == chrome ? BrowserLauncher.Run(command) : 0;
        }, os, chrome);

        launcher.Open(Pairing);

        Assert.Equal(2, started.Count);
        Assert.Equal(chrome, started[0].FileName);
        Assert.NotEqual(chrome, started[1].FileName);
        Assert.Contains(started[0].ArgumentList.Single(), Words(started[1]));
    }

    [Fact]
    public void ABrowserStartedDirectly_ThatExitsWithAnErrorAsItStarts_HasFailed_AndOneStillRunningOrDoneHasNot()
    {
        Assert.Equal(3, BrowserLauncher.Run(Command(StandInChrome("exit 3")), TimeSpan.FromSeconds(10)));
        // Chrome handing the file to a Chrome already running exits 0.
        Assert.Equal(0, BrowserLauncher.Run(Command(StandInChrome("exit 0")), TimeSpan.FromSeconds(10)));
        // Still running at the end of the window: it started.
        var watch = Stopwatch.StartNew();
        Assert.Equal(0, BrowserLauncher.Run(Command(StandInChrome("sleep 3; exit 3")), TimeSpan.FromMilliseconds(300)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Run waited {watch.Elapsed}");
        Assert.Equal(TimeSpan.FromSeconds(2), BrowserLauncher.StartupWindow);

        static ProcessStartInfo Command(string program)
        {
            var command = new ProcessStartInfo(program) { UseShellExecute = false };
            command.ArgumentList.Add("/nonexistent/file.html");
            return command;
        }
    }

    // ---- the default web browser, not the .html file's handler -------------------------------------------------------

    public static TheoryData<BrowserOS, string, string, string[]> DefaultBrowsers => new()
    {
        { BrowserOS.MacOS, "/Applications/Some Browser.app", "open", ["-a", "/Applications/Some Browser.app", "{file}"] },
        { BrowserOS.Windows, "\"C:\\Program Files\\Browser\\browser.exe\" --single-argument %1", @"C:\Program Files\Browser\browser.exe", ["--single-argument \"{file}\""] },
        { BrowserOS.Linux, "browser --new-window %u", "browser", ["--new-window", "{file}"] },
    };

    [Theory]
    [MemberData(nameof(DefaultBrowsers))]
    public void ALink_OpensInTheDefaultWebBrowser_NamedExplicitly_NotByTheFilesAssociation(BrowserOS os, string browser, string program, string[] arguments)
    {
        foreach (var link in new[] { SignIn, Pairing })
        {
            var started = new List<ProcessStartInfo>();
            // No Chrome: on the Mac, open -b fails.
            var launcher = Launcher(new ScopedLogger(), command => { started.Add(command); return command.ArgumentList.Contains("-b") ? 1 : 0; },
                os, chrome: null, browser);
            var before = _redirects.Pending.ToList();

            launcher.Open(link);

            var file = Assert.Single(_redirects.Pending.Except(before));
            var command = started.Last();
            Assert.Equal(program, command.FileName);
            Assert.False(command.UseShellExecute);
            Assert.Equal(arguments.Select(a => a.Replace("{file}", file)), command.ArgumentList.Count > 0 ? command.ArgumentList : [command.Arguments]);
            Assert.All(started.SelectMany(Words), w => Assert.DoesNotContain(Token, w));
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\B\\b.exe\" -osint -url \"%1\"", @"C:\Program Files\B\b.exe", "-osint -url \"{file}\"")]
    [InlineData("\"C:\\B\\b.exe\" %1", @"C:\B\b.exe", "\"{file}\"")]
    [InlineData("\"C:\\B\\b.exe\" -- \"%L\" %*", @"C:\B\b.exe", "-- \"{file}\"")]
    [InlineData("C:\\B\\b.exe", @"C:\B\b.exe", "\"{file}\"")]
    [InlineData("\"C:\\B\\b.exe\" --single-argument", @"C:\B\b.exe", "--single-argument \"{file}\"")]
    public void OnWindows_TheHttpAssociationsCommand_GetsTheFileForItsArgument(string template, string program, string arguments)
    {
        const string file = @"C:\Users\A User\AppData\Local\InfoPanel\run\0123456789abcdef.open-1.html";

        var command = DefaultBrowser.WindowsCommand(template, file);

        Assert.NotNull(command);
        Assert.Equal(program, command.FileName);
        Assert.Equal(arguments.Replace("{file}", file), command.Arguments);
        Assert.Empty(command.ArgumentList);
        Assert.False(command.UseShellExecute);
    }

    [Theory]
    [InlineData("firefox %u", new[] { "firefox", "{file}" })]
    [InlineData("\"/opt/My Browser/browser\" --new-window %U", new[] { "/opt/My Browser/browser", "--new-window", "{file}" })]
    [InlineData("env A=1 browser %F --class=x %i %c %k", new[] { "env", "A=1", "browser", "{file}", "--class=x" })]
    [InlineData("browser", new[] { "browser", "{file}" })]
    [InlineData("browser --url=%f 100%%", new[] { "browser", "--url={file}", "100%" })]
    [InlineData("\"/opt/a \\\"b\\\"/browser\" %u", new[] { "/opt/a \"b\"/browser", "{file}" })]
    public void OnLinux_TheDesktopEntrysExecLine_GetsTheFileForItsFieldCode(string exec, string[] expected)
    {
        const string file = "/home/a user/.local/share/InfoPanel/run/0123456789abcdef.open-1.html";

        var command = DefaultBrowser.LinuxCommand(exec, file);

        Assert.NotNull(command);
        Assert.Equal(expected.Select(e => e.Replace("{file}", file)), [command.FileName, .. command.ArgumentList]);
        Assert.False(command.UseShellExecute);
    }

    [Fact]
    public void OnLinux_TheDefaultWebBrowser_IsXdgSettingsDesktopEntry_FoundInTheDataFolders()
    {
        var home = Directory.CreateDirectory(Path.Combine(_run + "-bin", "home", "applications")).Parent!.FullName;
        var system = Directory.CreateDirectory(Path.Combine(_run + "-bin", "system", "applications")).Parent!.FullName;
        File.WriteAllText(Path.Combine(system, "applications", "browser.desktop"),
            "[Desktop Entry]\nName=Browser\nExec[de]=nicht %u\nExec=/opt/My\\sBrowser/browser %u\n\n[Desktop Action new-window]\nExec=browser --new-window %u\n");
        File.WriteAllText(Path.Combine(home, "applications", "other.desktop"), "[Desktop Entry]\nExec=other %u\n");

        Assert.Equal("/opt/My Browser/browser %u", DefaultBrowser.LinuxExec([home, system], () => "browser.desktop\n"));
        Assert.Equal("other %u", DefaultBrowser.LinuxExec([home, system], () => "other.desktop"));
        Assert.Null(DefaultBrowser.LinuxExec([home, system], () => "missing.desktop"));
        Assert.Null(DefaultBrowser.LinuxExec([home, system], () => "../system/applications/browser.desktop"));
        Assert.Null(DefaultBrowser.LinuxExec([home, system], () => null));
    }

    [Fact]
    public void OnTheMac_TheDefaultWebBrowser_IsTheAppLaunchServicesNamesForHttp()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Launch Services is macOS's.");

        var app = DefaultBrowser.MacApp();

        Assert.NotNull(app);
        Assert.EndsWith(".app", app);
        Assert.True(Directory.Exists(app), app);
        Assert.Equal(app, DefaultBrowser.Find(BrowserOS.MacOS));
    }

    /// <summary>An executable script standing in for Chrome, running <paramref name="body"/>.</summary>
    private string StandInChrome(string body)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in Chrome is a shell script.");
        var bin = Directory.CreateDirectory(Path.Combine(_run + "-bin", Guid.NewGuid().ToString("N")[..8])).FullName;
        var path = Path.Combine(bin, "google-chrome");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void ChromeOnThePath_IsFoundInLinuxOrder()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_run, "bin")).FullName;
        foreach (var name in new[] { "google-chrome", "google-chrome-stable" })
        {
            var path = Path.Combine(bin, name);
            File.WriteAllText(path, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(Path.Combine(bin, "google-chrome-stable"), BrowserLauncher.FindChromeOnPath(bin));
        Assert.Null(BrowserLauncher.FindChromeOnPath(Path.Combine(_run, "nothing-here")));
    }

    /// <summary>A logger that records each entry with the <see cref="LogTargets"/> target of its scope.</summary>
    private sealed class ScopedLogger : ILogger<BrowserLauncher>
    {
        private readonly Stack<string?> _targets = new();

        public List<(LogLevel Level, string Message, Exception? Exception, string? Target)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _targets.Push(state is IReadOnlyDictionary<string, object> d && d.TryGetValue(LogTargets.Property, out var t) ? t as string : null);
            return new Pop(_targets);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception, _targets.FirstOrDefault(t => t != null)));

        private sealed class Pop(Stack<string?> targets) : IDisposable
        {
            public void Dispose() => targets.Pop();
        }
    }
}
