using System.Diagnostics;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors.Libre;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// The command processor's line that runs the sensor helper elevated (<see cref="LibreElevation.CommandProcessorArguments"/>),
/// executed by Windows' own <c>cmd.exe</c>, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped elsewhere).
/// Nothing here is elevated, registered or prompted for: the line is run with an environment the test plants (as an
/// elevated process takes the approving user's), with Windows' command processor in the helper's place, which writes
/// the environment it was given; and, when <c>INFOPANEL_SENSOR_HELPER</c> names a built helper (never one under Program
/// Files), with that helper and <c>--register</c> for a SID no user has, which it refuses before anything (10 from an unelevated shell, else 11, or 12).
/// </summary>
public sealed class LibreElevationWindowsTests : IDisposable
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Runs Windows' command processor: set INFOPANEL_WINDOWS_TESTS=1 on Windows to run it.";

    /// <summary>
    /// The built helper's refusals of <c>--register</c> for a SID no user has, from a folder not under Program Files: the
    /// test's launcher doesn't elevate, so from an unelevated shell it refuses first for that (Codex W5.2 r3).
    /// </summary>
    internal static readonly int[] HelperRefusals = [HelperTask.ExitNotElevated, HelperTask.ExitNotInProgramFiles, HelperTask.ExitNotAUser];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-elevation-cmd-" + Guid.NewGuid().ToString("N"));

    public LibreElevationWindowsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// A variable for every prefix, mixed case, the runtime's profiler and host's trace among them, with values the
    /// command line could trip on (spaces, <c>&amp;</c>, <c>^</c>, <c>!</c>, <c>%</c>), and names that hold them too.
    /// </summary>
    private Dictionary<string, string> Planted() => new()
    {
        ["DOTNET_EnableEventPipe"] = "1",
        ["DOTNET_EventPipeOutputPath"] = Path.Combine(_folder, "event pipe & more.nettrace"),
        ["dotnet_TieredPGO"] = "0",
        ["Dotnet_a&b"] = "x ^ y",
        ["COMPlus_EnableDiagnostics"] = "1 & echo injected",
        ["complus_ZapDisable"] = "%PATH%",
        ["CORECLR_ENABLE_PROFILING"] = "1",
        ["CORECLR_PROFILER"] = "{9E2B38F2-7355-4C61-A54F-434B7AC266C0}",
        ["CORECLR_PROFILER_PATH_64"] = Path.Combine(_folder, "a profiler!.dll"),
        ["CoreClr_p!q"] = "!PATH!",
        ["COREHOST_TRACE"] = "1",
        ["COREHOST_TRACEFILE"] = Path.Combine(_folder, "host trace.txt"),
        ["corehost_%z%"] = "50%",
        ["COR_ENABLE_PROFILING"] = "1",
        ["COR_PROFILER"] = "{9E2B38F2-7355-4C61-A54F-434B7AC266C0}",
        ["cor_x^y"] = "a \"quoted\" value",
    };

    /// <summary>Runs <paramref name="arguments"/> by Windows' command processor with the planted variables (and its own, PATH among them); its exit code.</summary>
    private static int Run(string arguments, IReadOnlyDictionary<string, string> planted)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var (name, value) in planted)
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30_000), "the command processor didn't end within 30 s");
        return process.ExitCode;
    }

    /// <summary>The lines of the environment the program after the line was given (the command processor's <c>set</c>).</summary>
    private string[] ChildEnvironment(IReadOnlyDictionary<string, string> planted, out int exit)
    {
        var listing = Path.Combine(_folder, "elevation-" + Guid.NewGuid().ToString("N") + ".tmp");
        var written = Path.Combine(_folder, "child-" + Guid.NewGuid().ToString("N") + ".txt");
        var line = LibreElevation.CommandProcessorArguments(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set 1>\"" + written + "\"", listing);
        Assert.NotNull(line);
        exit = Run(line, planted);
        Assert.False(File.Exists(listing), "the listing is left behind");
        return File.Exists(written) ? File.ReadAllLines(written) : [];
    }

    private static string[] Left(string[] environment) =>
        environment.Where(l => LibreElevation.ClearedPrefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToArray();

    [Fact]
    public void EveryPrefixsVariable_IsCleared_BeforeTheProgram_PathKept()
    {
        Assert.SkipUnless(Enabled, Skip);
        var planted = Planted();

        var environment = ChildEnvironment(planted, out var exit);

        Assert.Equal(0, exit);
        Assert.Empty(Left(environment));
        Assert.Contains(environment, l => l.StartsWith("Path=", StringComparison.OrdinalIgnoreCase) && l.Length > "Path=".Length);
        Assert.Contains(environment, l => l.StartsWith("SystemRoot=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AVariableTheClearMisses_IsFound_ExitsFifteen_AndTheProgramDoesntRun()
    {
        Assert.SkipUnless(Enabled, Skip);
        // A name with a line break lists as two lines: the clear reads two other names, and only the check finds it.
        var planted = Planted();
        planted["DOTNET_split\r\nAfter"] = "1";

        var environment = ChildEnvironment(planted, out var exit);

        Assert.Empty(Left(environment));
        Assert.Equal(HelperTask.ExitEnvironmentLeft, exit);
        Assert.Empty(environment);
    }

    [Fact]
    public void TheBuiltHelper_RunsThroughTheLine_WithNoneOfTheVariables()
    {
        Assert.SkipUnless(Enabled, Skip);
        var helper = Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_HELPER");
        Assert.SkipWhen(string.IsNullOrEmpty(helper), "Set INFOPANEL_SENSOR_HELPER to a built helper (not under Program Files) to run it.");
        var planted = Planted();
        var trace = planted["COREHOST_TRACEFILE"];
        var line = LibreElevation.CommandProcessorArguments(helper!, HelperTask.RegisterArguments("S-1-5-21-0-0-0-1"), Path.Combine(_folder, "elevation-" + Guid.NewGuid().ToString("N") + ".tmp"));
        Assert.NotNull(line);

        var exit = Run(line, planted);

        // The helper ran (its own refusal: not elevated, not under Program Files, or no such user), with no host trace (a .NET host
        // reads COREHOST_TRACEFILE before Main) and no EventPipe file (the runtime reads DOTNET_EventPipeOutputPath).
        Assert.Contains(exit, HelperRefusals);
        Assert.False(File.Exists(trace), "the host's trace file was written: COREHOST_ reached the helper");
        Assert.Empty(Directory.GetFiles(_folder, "*.nettrace"));

        // Without the line, the same variables reach it (the markers above are live).
        Assert.Contains(Run("/d /s /c \"\"" + helper + "\" " + HelperTask.RegisterArguments("S-1-5-21-0-0-0-1") + "\"", planted), HelperRefusals);
        Assert.True(File.Exists(trace), "the planted host trace isn't written without the line: the check above proves nothing");
    }
}
