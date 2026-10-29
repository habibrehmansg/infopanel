using System.Diagnostics;
using System.Text.Json;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The engine's build as the build records it (<c>InfoPanel/EngineBuild.targets</c>, the session's <c>engineBuild</c>),
/// run on its own in a probe project outside any repository, with git replaced by a script whose answers each case sets,
/// or with no git at all: what is recorded follows from what git answers, not from the machine the tests run on.
/// </summary>
public class EngineBuildTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Commit = "004cf521a45fc7c18c426ed6163ac6b7f73ba593";

    /// <summary>
    /// What the stand-in git answers. <paramref name="Head"/>: what <c>rev-parse</c> prints, or null for the commit
    /// abbreviated as git does it (to the length asked, or to <paramref name="Abbrev"/>, its <c>core.abbrev</c>, when none
    /// is); <paramref name="Changes"/>: what <c>status --porcelain</c> prints; <paramref name="Hang"/>: a file it writes its
    /// process id to before it hangs, answering nothing.
    /// </summary>
    private sealed record Git(
        string? Head = null, int HeadExit = 0, string Changes = "", int ChangesExit = 0, int Abbrev = 7, string? Hang = null);

    [Fact]
    public async Task TheBuild_IsUnknown_WithoutGit()
    {
        SkipOnWindows();
        Assert.Equal("unknown", await Recorded(git: null));
    }

    [Fact]
    public async Task TheBuild_IsUnknown_WithNoShellOnThePath()
    {
        // MSBuild runs commands through a shell; a PATH without one must not fail the build.
        SkipOnWindows();
        Assert.Equal("unknown", await Recorded(git: null, shell: false));
    }

    [Fact]
    public async Task TheBuild_IsUnknown_OutsideARepository()
    {
        // The real git, if the machine has one, in a folder that isn't in any repository (git's search stops above it).
        SkipOnWindows();
        Assert.Equal("unknown", await Recorded(git: null, realGit: true));
    }

    public static TheoryData<string, int, string, int> GitFailures => new()
    {
        // rev-parse fails, whatever it printed
        { Commit[..7], 1, "", 0 },
        { Commit[..7], 128, "", 0 },
        // status fails, whatever it listed
        { Commit[..7], 0, "", 128 },
        { Commit[..7], 0, " M InfoPanel/Program.cs", 1 },
        // rev-parse answers something that isn't a commit id
        { "fatal: not a git repository", 0, "", 0 },
        { "", 0, "", 0 },
        { Commit[..7].ToUpperInvariant(), 0, "", 0 },
        { Commit[..6], 0, "", 0 },
    };

    [Theory]
    [MemberData(nameof(GitFailures))]
    public async Task TheBuild_IsUnknown_WhenGitFails(string head, int headExit, string changes, int changesExit)
    {
        SkipOnWindows();
        Assert.Equal("unknown", await Recorded(new Git(head, headExit, changes, changesExit)));
    }

    [Fact]
    public async Task TheBuild_IsTheCommit_DirtyWhenGitListsChanges()
    {
        SkipOnWindows();
        Assert.Equal(Commit[..7], await Recorded(new Git()));
        Assert.Equal(Commit[..7] + "-dirty", await Recorded(new Git(Changes: " M InfoPanel/Program.cs\n?? notes.txt")));
        Assert.Equal(Commit[..7] + "-dirty", await Recorded(new Git(Changes: "?? it's \"quoted\".txt")));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(12)]
    public async Task TheBuild_AsksForSevenCharacters_WhateverGitIsSetToAbbreviate(int abbrev)
    {
        // A bare --short follows core.abbrev; the build asks for 7 (git still writes more when 7 would be ambiguous).
        SkipOnWindows();
        Assert.Equal(Commit[..7], await Recorded(new Git(Abbrev: abbrev)));
    }

    [Fact]
    public async Task AStoppedProbe_StopsWhatItStarted()
    {
        // A probe stopped while git hangs (its time limit, or the test run cancelled) takes its build and git down with
        // it before their folder is deleted.
        SkipOnWindows();
        var hang = Directory.CreateTempSubdirectory("infopanel-build-target-hang-");
        try
        {
            var pidFile = Path.Combine(hang.FullName, "git.pid");
            using var stop = new CancellationTokenSource();
            var probe = Recorded(new Git(Hang: pidFile), cancel: stop.Token);
            while (!File.Exists(pidFile) && !probe.IsCompleted)
                await Task.Delay(50, Ct);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            var git = int.Parse(await File.ReadAllTextAsync(pidFile, Ct));
            Assert.True(await Exited(git), $"git ({git}) is still running after its probe was stopped");
        }
        finally
        {
            hang.Delete(recursive: true);
        }
    }

    /// <summary>Whether the process has gone within 5 s (a killed process takes a moment to be reaped).</summary>
    private static async Task<bool> Exited(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(50, Ct);
        }
        return false;
    }

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in git is a shell script.");

    /// <summary>The dotnet host running these tests.</summary>
    private static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
            return host;
        // The shared runtime is {dotnet root}/shared/Microsoft.NETCore.App/{version}/.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet"));
    }

    /// <summary>
    /// Runs EngineBuild.targets in a probe project in a new folder outside any repository and answers the build it records.
    /// <paramref name="git"/>: the stand-in git on the PATH; null: no git (only the shell on the PATH), or with
    /// <paramref name="realGit"/> the PATH as it is; <paramref name="shell"/> false: no shell on the PATH either.
    /// <paramref name="cancel"/> stops the probe early, as its time limit does.
    /// </summary>
    private static async Task<string> Recorded(Git? git, bool realGit = false, bool shell = true, CancellationToken cancel = default)
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-build-target-");
        try
        {
            var targets = Path.Combine(LiveEngine.RepoRoot(), "InfoPanel", "EngineBuild.targets");
            var probe = Path.Combine(dir.FullName, "probe.proj");
            await File.WriteAllTextAsync(probe,
                $"""<Project><Import Project="{targets}" /><Target Name="GetAssemblyAttributes" /></Project>""", Ct);
            // The PATH's one folder: the shell MSBuild runs commands with, and the stand-in git when there is one.
            var bin = Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
            if (shell)
                File.CreateSymbolicLink(Path.Combine(bin.FullName, "sh"), "/bin/sh");
            if (git is not null)
            {
                var script = Path.Combine(bin.FullName, "git");
                await File.WriteAllTextAsync(script, Script(git), Ct);
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var start = new ProcessStartInfo(Dotnet())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir.FullName,
            };
            foreach (var arg in new[] { "msbuild", probe, "-t:GetAssemblyAttributes", "-getItem:AssemblyAttribute", "-nologo", "-nodeReuse:false" })
                start.ArgumentList.Add(arg);
            // The test run's own MSBuild settings stay out of the probe's build.
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
                start.Environment.Remove(name);
            start.Environment["GIT_CEILING_DIRECTORIES"] = dir.Parent!.FullName;
            if (!realGit)
                start.Environment["PATH"] = git is null ? bin.FullName : $"{bin.FullName}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin";

            using var build = Process.Start(start)!;
            var output = build.StandardOutput.ReadToEndAsync(Ct);
            var errors = build.StandardError.ReadToEndAsync(Ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct, cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await build.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Stopped early: the build and what it started (the shell, git) go before the folder they run in does.
                build.Kill(entireProcessTree: true);
                await build.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            Assert.True(build.ExitCode == 0, $"msbuild exited {build.ExitCode}: {await output}{await errors}");

            using var items = JsonDocument.Parse(await output);
            var recorded = items.RootElement.GetProperty("Items").GetProperty("AssemblyAttribute").EnumerateArray()
                .Single(a => a.GetProperty("_Parameter1").GetString() == "InfoPanel.Build");
            return recorded.GetProperty("_Parameter2").GetString()!;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string Quoted(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>A POSIX shell script that answers the target's two git commands as <paramref name="git"/> says.</summary>
    private static string Script(Git git)
    {
        var hang = git.Hang is null
            ? ""
            : $"printf '%s' $$ > {Quoted(git.Hang + ".new")} && mv {Quoted(git.Hang + ".new")} {Quoted(git.Hang)} && exec sleep 600";
        var head = git.Head is null
            ? $"printf '%s\\n' \"$(printf '%s' {Commit} | cut -c1-$n)\""
            : $"printf '%s\\n' {Quoted(git.Head)}";
        return $$"""
            #!/bin/sh
            {{hang}}
            case "$1 $2" in
              "rev-parse --short") n={{git.Abbrev}} ;;
              "rev-parse --short="*) n="${2#--short=}" ;;
              "--no-optional-locks status") printf '%s' {{Quoted(git.Changes)}}; exit {{git.ChangesExit}} ;;
              *) echo "unexpected: git $*" >&2; exit 2 ;;
            esac
            {{head}}
            exit {{git.HeadExit}}

            """;
    }
}
