using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using InfoPanel.Hosting;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// On Windows the engine is a Windows-subsystem program, so no console window opens with it (the PE subsystem of a
/// Windows publish's <c>InfoPanel.exe</c> is pinned in <see cref="EnginePublishTests"/>): the engine's project makes it
/// <c>WinExe</c> for a Windows runtime only, whatever computer builds it, and <see cref="ParentConsole"/> attaches a start
/// from a terminal to that terminal's console, never touching streams the parent redirected and never making a console.
/// </summary>
public sealed class WindowsSubsystemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("win-x64", "WinExe")]
    [InlineData("win-arm64", "WinExe")]
    [InlineData("osx-arm64", "Exe")]
    [InlineData("osx-x64", "Exe")]
    [InlineData("linux-x64", "Exe")]
    [InlineData("linux-arm64", "Exe")]
    [InlineData("", "Exe")]
    public async Task TheEnginesOutputType_IsWinExe_ForAWindowsRuntimeOnly(string runtime, string expected)
    {
        Assert.Equal(expected, await OutputType(runtime));
    }

    [Fact]
    public void NotOnWindows_NothingIsAsked()
    {
        var api = new FakeConsoleApi(output: 0, error: 0, parentConsole: true);

        Assert.False(ParentConsole.AttachIfNeeded(windows: false, api));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void BothStreamsRedirected_AreLeftAsTheyAre_AndNothingIsAttached()
    {
        var api = new FakeConsoleApi(output: 0x100, error: 0x104, parentConsole: true);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, api));
        // Asked and probed (a write of zero bytes), never set.
        Assert.DoesNotContain(api.Calls, c => !c.StartsWith("get ", StringComparison.Ordinal) && !c.StartsWith("write ", StringComparison.Ordinal));
        Assert.Equal(((nint)0x100, (nint)0x104), (api.Handles[ParentConsole.StdOutput], api.Handles[ParentConsole.StdError]));
    }

    [Theory]
    [InlineData(false, 0L, 0L)]
    [InlineData(false, -1L, -1L)]
    [InlineData(false, 0L, -1L)]
    [InlineData(true, 0L, 0L)]
    [InlineData(true, -1L, -1L)]
    [InlineData(true, 0L, -1L)]
    public void NoStreams_FromATerminal_AttachToTheParentsConsole(bool attachReplacesAll, long output, long error)
    {
        var api = new FakeConsoleApi(output: (nint)output, error: (nint)error, parentConsole: true, attachReplacesAll);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        // Nothing the parent redirected, so nothing is put back; a -1 is cleared to 0 first (the attach fills only 0).
        var cleared = new[] { (ParentConsole.StdOutput, output), (ParentConsole.StdError, error) }.Where(s => s.Item2 == -1).Select(s => $"set {s.Item1} 0");
        Assert.Equal(["get -10", "get -11", "get -12", .. cleared, $"attach {ParentConsole.AttachParentProcess}"], api.Calls);
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
    }

    [Fact]
    public void NoParentConsole_NothingIsAttachedOrMade()
    {
        var api = new FakeConsoleApi(output: 0, error: 0, parentConsole: false);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(["get -10", "get -11", "get -12", $"attach {ParentConsole.AttachParentProcess}"], api.Calls);
        Assert.Equal(((nint)0, (nint)0), (api.Handles[ParentConsole.StdOutput], api.Handles[ParentConsole.StdError]));
    }

    [Fact]
    public void OneStreamRedirected_StaysWhereTheParentSentIt_TheOtherGoesToTheTerminal()
    {
        // InfoPanel.exe --help 2>err.txt: Windows' attach may hand every stream the console's; the file is put back.
        var api = new FakeConsoleApi(output: 0, error: 0x104, parentConsole: true, attachReplacesAll: true);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal((nint)0x104, api.Handles[ParentConsole.StdError]);
        Assert.Contains("set -12 260", api.Calls);
    }

    [Fact]
    public void ARedirectedInput_StaysWhereTheParentSentIt()
    {
        var api = new FakeConsoleApi(output: 0, error: 0, parentConsole: true, attachReplacesAll: true, input: 0x108);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal((nint)0x108, api.Handles[ParentConsole.StdInput]);
    }

    [Fact]
    public void BothStreamsWritable_OnlyTheProbeRuns_AndNothingElse()
    {
        // Redirected to a file or a pipe: each takes a write of zero bytes, and nothing is set or attached.
        var api = new FakeConsoleApi(output: 0x100, error: 0x104, parentConsole: true, input: 0x108);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(["get -10", "get -11", "get -12", "write 256", "write 260"], api.Calls);
        Assert.Equal(((nint)0x108, (nint)0x100, (nint)0x104), (api.Handles[ParentConsole.StdInput], api.Handles[ParentConsole.StdOutput], api.Handles[ParentConsole.StdError]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothStreamsStale_FromATerminal_AttachAndBothBecomeTheConsoles(bool attachReplacesAll)
    {
        // A parent can hand a Windows-subsystem child standard-handle values that are no open handle in it.
        var api = new FakeConsoleApi(output: 0x200, error: 0x204, parentConsole: true, attachReplacesAll, stale: [0x200, 0x204]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
        // The stale slots are cleared before the attach (never closed: the seam has no close) and never set back.
        Assert.Equal(["get -10", "get -11", "get -12", "write 512", "write 516", "set -11 0", "set -12 0", $"attach {ParentConsole.AttachParentProcess}"], api.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void OneStreamStale_OneRedirected_TheRedirectIsKept_TheStaleOneBecomesTheConsoles(bool attachReplacesAll, bool outputStale)
    {
        var api = outputStale
            ? new FakeConsoleApi(output: 0x200, error: 0x104, parentConsole: true, attachReplacesAll, stale: [0x200])
            : new FakeConsoleApi(output: 0x100, error: 0x204, parentConsole: true, attachReplacesAll, stale: [0x204]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(outputStale ? FakeConsoleApi.ConsoleOutput : 0x100, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(outputStale ? 0x104 : FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
        Assert.DoesNotContain(api.Calls, c => c is "set -11 512" or "set -12 516");
    }

    [Theory]
    [InlineData(false, 0x200L, 0L, 0L)]
    [InlineData(true, 0x200L, 0L, 0L)]
    [InlineData(false, 0L, 0x204L, 0L)]
    [InlineData(true, 0L, 0x204L, 0L)]
    [InlineData(false, 0L, 0L, 0x208L)]
    [InlineData(true, 0L, 0L, 0x208L)]
    public void AStaleSlot_IsNeverRestoredOverTheAttachedConsolesHandle(bool attachReplacesAll, long output, long error, long input)
    {
        // Stdout with no handle at all, so the attach runs; a stale value in any slot, stdin included, must not come back.
        var stale = new[] { (nint)output, (nint)error, (nint)input }.Where(h => h != 0).ToArray();
        var api = new FakeConsoleApi(output: (nint)output, error: (nint)error, parentConsole: true, attachReplacesAll, input: (nint)input, stale: stale);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(FakeConsoleApi.ConsoleInput, api.Handles[ParentConsole.StdInput]);
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
        Assert.DoesNotContain(api.Calls, c => c is "set -10 520" or "set -11 512" or "set -12 516");
    }

    [Fact]
    public void StaleStreams_AndNoParentConsole_NothingIsAttachedOrThrown()
    {
        var api = new FakeConsoleApi(output: 0x200, error: 0x204, parentConsole: false, stale: [0x200, 0x204]);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, api));
        // The attach was asked and failed: the stale slots stay cleared, nothing is restored and no console is made.
        Assert.Equal(["get -10", "get -11", "get -12", "write 512", "write 516", "set -11 0", "set -12 0", $"attach {ParentConsole.AttachParentProcess}"], api.Calls);
        Assert.Equal(((nint)0, (nint)0), (api.Handles[ParentConsole.StdOutput], api.Handles[ParentConsole.StdError]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedButUnwritableStreams_FromATerminal_AttachAndBothBecomeTheConsoles(bool attachReplacesAll)
    {
        // A console parent can pass its own console's handles to a Windows-subsystem child that isn't attached to it:
        // Windows types them (a character device), but a write to them fails, so .NET would give the stream nothing.
        var api = new FakeConsoleApi(output: 0x300, error: 0x304, parentConsole: true, attachReplacesAll, devices: [0x300, 0x304], unwritable: [0x300, 0x304]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
        Assert.Equal(["get -10", "get -11", "get -12", "write 768", "write 772", "set -11 0", "set -12 0", $"attach {ParentConsole.AttachParentProcess}"], api.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void AReadOnlyOutputHandle_IsNotUsable_AndBecomesTheConsoles(bool attachReplacesAll, bool outputReadOnly)
    {
        // A file opened for reading only: Windows knows its type, but it takes no write.
        var api = outputReadOnly
            ? new FakeConsoleApi(output: 0x400, error: 0x104, parentConsole: true, attachReplacesAll, unwritable: [0x400])
            : new FakeConsoleApi(output: 0x100, error: 0x404, parentConsole: true, attachReplacesAll, unwritable: [0x404]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(outputReadOnly ? FakeConsoleApi.ConsoleOutput : 0x100, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal(outputReadOnly ? 0x104 : FakeConsoleApi.ConsoleError, api.Handles[ParentConsole.StdError]);
        Assert.DoesNotContain(api.Calls, c => c is "set -11 1024" or "set -12 1028");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StdoutInvalid_StderrRedirected_StdoutBecomesTheConsoles_StderrIsKept(bool attachReplacesAll)
    {
        // STARTF_USESTDHANDLES with stdout -1 and stderr to a file: Windows' attach fills only an empty (0) slot.
        var api = new FakeConsoleApi(output: -1, error: 0x104, parentConsole: true, attachReplacesAll);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(FakeConsoleApi.ConsoleOutput, api.Handles[ParentConsole.StdOutput]);
        Assert.Equal((nint)0x104, api.Handles[ParentConsole.StdError]);
        Assert.True(api.Calls.IndexOf("set -11 0") is >= 0 and var at && at < api.Calls.IndexOf($"attach {ParentConsole.AttachParentProcess}"));
    }

    /// <summary>The kinds of unusable value a slot can hold.</summary>
    public enum Unusable
    {
        /// <summary>-1, <c>INVALID_HANDLE_VALUE</c>.</summary>
        Invalid,

        /// <summary>No open handle in the process.</summary>
        Stale,

        /// <summary>An open handle Windows types that takes no write (output streams only).</summary>
        Unwritable,
    }

    [Theory]
    [InlineData(false, ParentConsole.StdInput, Unusable.Invalid)]
    [InlineData(true, ParentConsole.StdInput, Unusable.Invalid)]
    [InlineData(false, ParentConsole.StdInput, Unusable.Stale)]
    [InlineData(true, ParentConsole.StdInput, Unusable.Stale)]
    [InlineData(false, ParentConsole.StdOutput, Unusable.Invalid)]
    [InlineData(true, ParentConsole.StdOutput, Unusable.Invalid)]
    [InlineData(false, ParentConsole.StdOutput, Unusable.Stale)]
    [InlineData(true, ParentConsole.StdOutput, Unusable.Stale)]
    [InlineData(false, ParentConsole.StdOutput, Unusable.Unwritable)]
    [InlineData(true, ParentConsole.StdOutput, Unusable.Unwritable)]
    [InlineData(false, ParentConsole.StdError, Unusable.Invalid)]
    [InlineData(true, ParentConsole.StdError, Unusable.Invalid)]
    [InlineData(false, ParentConsole.StdError, Unusable.Stale)]
    [InlineData(true, ParentConsole.StdError, Unusable.Stale)]
    [InlineData(false, ParentConsole.StdError, Unusable.Unwritable)]
    [InlineData(true, ParentConsole.StdError, Unusable.Unwritable)]
    public void EveryUnusableValue_IsClearedToZeroBeforeTheAttach_AndTheSlotBecomesTheConsoles(bool attachReplacesAll, int slot, Unusable kind)
    {
        nint value = kind == Unusable.Invalid ? -1 : 0x600;
        nint Of(int which) => which == slot ? value : 0;
        var api = new FakeConsoleApi(
            output: Of(ParentConsole.StdOutput), error: Of(ParentConsole.StdError), parentConsole: true, attachReplacesAll,
            input: Of(ParentConsole.StdInput), stale: kind == Unusable.Stale ? [0x600] : null, unwritable: kind == Unusable.Unwritable ? [0x600] : null);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        var attach = api.Calls.IndexOf($"attach {ParentConsole.AttachParentProcess}");
        Assert.True(api.Calls.IndexOf($"set {slot} 0") is >= 0 and var at && at < attach, string.Join(", ", api.Calls));
        // Cleared, never closed (the seam has no close), and never put back.
        Assert.DoesNotContain($"set {slot} {value}", api.Calls);
        var console = slot switch { ParentConsole.StdInput => FakeConsoleApi.ConsoleInput, ParentConsole.StdOutput => FakeConsoleApi.ConsoleOutput, _ => FakeConsoleApi.ConsoleError };
        Assert.Equal(console, api.Handles[slot]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NulCountsAsUsable(bool attachReplacesAll)
    {
        // NUL is a character device that takes every write: a start with both streams there is left alone.
        var both = new FakeConsoleApi(output: 0x500, error: 0x504, parentConsole: true, attachReplacesAll, devices: [0x500, 0x504]);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, both));
        Assert.Equal(["get -10", "get -11", "get -12", "write 1280", "write 1284"], both.Calls);

        // stdout to NUL and stderr to the terminal: NUL is kept.
        var one = new FakeConsoleApi(output: 0x500, error: 0, parentConsole: true, attachReplacesAll, devices: [0x500]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, one));
        Assert.Equal(((nint)0x500, FakeConsoleApi.ConsoleError), (one.Handles[ParentConsole.StdOutput], one.Handles[ParentConsole.StdError]));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(0x208L)]
    [InlineData(0x108L)]
    public void Stdin_IsNeverAReasonToAttach(long input)
    {
        // Both outputs writable: whatever stdin holds (none, -1, stale or valid), only the probe runs and nothing changes.
        var api = new FakeConsoleApi(output: 0x100, error: 0x104, parentConsole: true, input: (nint)input, stale: [0x208]);

        Assert.False(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(["get -10", "get -11", "get -12", "write 256", "write 260"], api.Calls);
        Assert.Equal((nint)input, api.Handles[ParentConsole.StdInput]);
    }

    [Theory]
    [InlineData(false, 0L, false)]
    [InlineData(true, 0L, false)]
    [InlineData(false, 0x208L, false)]
    [InlineData(true, 0x208L, false)]
    [InlineData(false, 0x108L, true)]
    [InlineData(true, 0x108L, true)]
    public void Stdin_IsJudgedApart_WhenAttaching_AValidOneIsKept_AStaleOrAbsentOneBecomesTheConsoles(bool attachReplacesAll, long input, bool kept)
    {
        // Stdin is judged by its type alone (it is only read): a valid one is kept, never probed with a write.
        var api = new FakeConsoleApi(output: 0, error: 0, parentConsole: true, attachReplacesAll, input: (nint)input, stale: [0x208]);

        Assert.True(ParentConsole.AttachIfNeeded(windows: true, api));
        Assert.Equal(kept ? (nint)input : FakeConsoleApi.ConsoleInput, api.Handles[ParentConsole.StdInput]);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    /// <summary>
    /// No console is ever made: no P/Invoke in the engine's assembly binds <c>AllocConsole</c> (nor <c>AllocConsoleWithOptions</c>),
    /// and the attach binds <c>AttachConsole</c>.
    /// </summary>
    [Fact]
    public void TheEngine_NeverAllocatesAConsole()
    {
        var entries = typeof(ParentConsole).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => m.GetCustomAttribute<DllImportAttribute>()?.EntryPoint ?? m.Name)
            .ToList();

        Assert.Contains("AttachConsole", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("AllocConsole", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fake console: the three standard handles, and a parent's console (or none). As Windows documents (GetStdHandle,
    /// "Attach/detach behavior"), the attach gives the console's handles only to the slots that hold 0 (a parent that
    /// passed its handles with <c>STARTF_USESTDHANDLES</c>), or to all three with <c>attachReplacesAll</c> (one that
    /// didn't). Values in <c>stale</c> are no open handle in the process: their type is unknown and a write to them
    /// fails. Values in <c>unwritable</c> are open and typed (a read-only file, a console the process isn't attached to)
    /// but a write to them fails. Values in <c>devices</c> are character devices (a console, <c>NUL</c>); any other open
    /// value is a file. Every call is recorded, a type check as <c>get type</c>, the zero-byte write as <c>write</c>.
    /// </summary>
    private sealed class FakeConsoleApi(
        nint output, nint error, bool parentConsole, bool attachReplacesAll = false, nint input = 0,
        nint[]? stale = null, nint[]? unwritable = null, nint[]? devices = null) : ParentConsole.IConsoleApi
    {
        public static readonly nint ConsoleInput = 0x1000, ConsoleOutput = 0x1004, ConsoleError = 0x1008;

        public Dictionary<int, nint> Handles { get; } = new()
        {
            [ParentConsole.StdInput] = input,
            [ParentConsole.StdOutput] = output,
            [ParentConsole.StdError] = error,
        };

        public List<string> Calls { get; } = [];

        public nint GetStdHandle(int which)
        {
            Calls.Add($"get {which}");
            return Handles[which];
        }

        public bool SetStdHandle(int which, nint handle)
        {
            Calls.Add($"set {which} {handle}");
            Handles[which] = handle;
            return true;
        }

        public bool AttachConsole(uint processId)
        {
            Calls.Add($"attach {processId}");
            if (!parentConsole)
                return false;
            foreach (var (which, console) in new[] { (ParentConsole.StdInput, ConsoleInput), (ParentConsole.StdOutput, ConsoleOutput), (ParentConsole.StdError, ConsoleError) })
            {
                // Without attachReplacesAll, only a slot holding 0 gets the console's: any other value, -1 and a stale or
                // unwritable one included, is kept (the cases that need the rule to clear such a slot itself).
                if (attachReplacesAll || Handles[which] == 0)
                    Handles[which] = console;
            }
            return true;
        }

        /// <summary>A stale value (no open handle in the process) is unknown; a device or the console's is a character device; any other open handle a file.</summary>
        public uint GetFileType(nint handle)
        {
            Calls.Add($"get type {handle}");
            if (handle == 0 || handle == -1 || (stale ?? []).Contains(handle))
                return ParentConsole.FileTypeUnknown;
            return handle == ConsoleInput || handle == ConsoleOutput || handle == ConsoleError || (devices ?? []).Contains(handle) ? 2u : 1u;
        }

        /// <summary>Fails for 0, -1, a stale value and an unwritable one; any other open handle takes it.</summary>
        public bool WritesZeroBytes(nint handle)
        {
            Calls.Add($"write {handle}");
            return handle != 0 && handle != -1 && !(stale ?? []).Contains(handle) && !(unwritable ?? []).Contains(handle);
        }
    }

    /// <summary>The engine project's <c>OutputType</c> for <paramref name="runtime"/> (none when empty), as MSBuild evaluates it. Nothing is built.</summary>
    private static async Task<string?> OutputType(string runtime)
    {
        var project = Path.Combine(LiveEngine.RepoRoot(), "InfoPanel", "InfoPanel.csproj");
        var start = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(project)!,
        };
        foreach (var arg in new[] { "msbuild", project, "-getProperty:OutputType", "-nologo", "-nodeReuse:false" })
            start.ArgumentList.Add(arg);
        if (runtime.Length > 0)
            start.ArgumentList.Add($"-p:RuntimeIdentifier={runtime}");
        // The test run's own MSBuild settings stay out of the evaluation.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);

        using var evaluate = Process.Start(start)!;
        var output = evaluate.StandardOutput.ReadToEndAsync(Ct);
        var errors = evaluate.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await evaluate.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            evaluate.Kill(entireProcessTree: true);
            await evaluate.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(evaluate.ExitCode == 0, $"msbuild exited {evaluate.ExitCode}: {await output}{await errors}");
        // One property asked: MSBuild prints its value alone.
        return (await output).Trim();
    }

    /// <summary>The dotnet host running these tests.</summary>
    private static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
            return host;
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    }
}
