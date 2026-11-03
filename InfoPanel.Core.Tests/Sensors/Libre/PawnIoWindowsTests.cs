using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors.Libre;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// The PawnIO driver on real Windows, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped elsewhere), run from
/// an elevated shell (an administrator's full token), so <c>runas</c> asks nothing. No test runs PawnIO's installer: the launches run
/// a harmless stand-in, the tests' helper stand-in (<c>InfoPanel.SensorHelper.TestHost</c>) copied as
/// <c>PawnIO_setup.exe</c>, which, given the installer's arguments, writes <c>ran.txt</c> beside itself and exits with the
/// number in <c>exit-code.txt</c> there. Each test works in a folder of its own under the temporary folder and removes it,
/// its junction first (<c>rmdir</c>, which never touches the target's contents). And the real sensor helper, built beside
/// these tests, says the driver the engine reads in its hello, as a helper with its rights says it (installed without
/// administrator rights is <c>notLoaded</c>). (Making a symbolic link needs an administrator here, so
/// <c>PawnIoInstallerTests</c>' link case runs on Windows only in this class.)
/// </summary>
public sealed class PawnIoWindowsTests : IDisposable
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Launches through Windows' shell and runs the real sensor helper: set INFOPANEL_WINDOWS_TESTS=1 on Windows, elevated, to run it.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-pawnio-win-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly List<string> _dotted = [];

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            if (Directory.Exists(junction))
                Cmd($"rmdir \"{junction}\"");
        }

        // A folder named with a trailing dot, through its \\?\ path only: its files, then itself.
        foreach (var dotted in _dotted)
        {
            if (!Directory.Exists(dotted))
                continue;
            foreach (var file in Directory.GetFiles(dotted))
                File.Delete(file);
            Directory.Delete(dotted);
        }

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void Say(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// A folder <paramref name="name"/> holding the stand-in as <c>PawnIO_setup.exe</c>, exiting with
    /// <paramref name="code"/>; <paramref name="extra"/> bytes after its end (which Windows ignores) give it a hash of its own.
    /// </summary>
    private string StandIn(string name, int code, byte[]? extra = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        const string Host = "InfoPanel.SensorHelper.TestHost";
        foreach (var file in new[] { Host + ".dll", Host + ".deps.json", Host + ".runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        var exe = Path.Combine(folder, "PawnIO_setup.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, Host + ".exe"), exe);
        if (extra is not null)
        {
            using var append = new FileStream(exe, FileMode.Append, FileAccess.Write);
            append.Write(extra);
        }

        File.WriteAllText(Path.Combine(folder, "exit-code.txt"), code.ToString(CultureInfo.InvariantCulture));
        return exe;
    }

    private void Junction(string link, string target)
    {
        Assert.Equal(0, Cmd($"mklink /J \"{link}\" \"{target}\""));
        _junctions.Add(link);
    }

    /// <summary>Runs <c>cmd /c</c> <paramref name="command"/> with no window; its exit code.</summary>
    private static int Cmd(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Say($"cmd /c {command}: exit {process.ExitCode} {output.Trim()}");
        return process.ExitCode;
    }

    /// <summary>
    /// A junction at <paramref name="link"/> to <paramref name="target"/> exactly as given, written as its reparse point
    /// (<c>mklink /J</c> would drop a trailing dot from the target's name, leading to another folder).
    /// </summary>
    private void ExactJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        _junctions.Add(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        // REPARSE_DATA_BUFFER for a mount point: tag, data length, reserved; the substitute name's offset and length, the
        // print name's (empty); then the names, each ended by a null.
        var names = substitute.Length + 2 + 2;
        var buffer = new byte[16 + names];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(8 + names));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        substitute.CopyTo(buffer, 16);

        const uint GenericWrite = 0x40000000, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        const uint SetReparsePoint = 0x000900A4; // FSCTL_SET_REPARSE_POINT
        using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"{link} couldn't be opened: {Marshal.GetLastPInvokeError()}");
        Assert.True(DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"{link} couldn't be made a junction: {Marshal.GetLastPInvokeError()}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength, IntPtr output, int outputLength,
        out int returned, IntPtr overlapped);

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task AJunctionRetargetedAfterTheHash_StillRunsTheFileHashed_NeverTheOther()
    {
        Assert.SkipUnless(Enabled, Skip);
        StandIn("hashed", 0);
        var other = StandIn("other", 99, "another file"u8.ToArray());
        var junction = Path.Combine(_root, "pawnio");
        Junction(junction, Path.Combine(_root, "hashed"));
        var path = Path.Combine(junction, "PawnIO_setup.exe");
        var sha256 = Sha256Of(path);
        Assert.NotEqual(sha256, Sha256Of(other));

        // After the hash, before the shell resolves anything: the junction now leads to the other file.
        var swapped = false;
        var launcher = new BeforeLaunch(new ShellPawnIoLauncher(), start =>
        {
            Say($"launching {start.FileName} {start.Arguments} as {start.Verb}");
            var removed = Cmd($"rmdir \"{junction}\"") == 0;
            swapped = removed && Cmd($"mklink /J \"{junction}\" \"{Path.GetDirectoryName(other)}\"") == 0;
        });
        var installed = 0;
        using var installer = new PawnIoInstaller(path, launcher, () => Interlocked.Increment(ref installed), supported: true, sha256: sha256);

        Assert.Equal(PawnIoInstaller.BundledVersion, installer.Available);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        await installer.Run!.WaitAsync(Patience, Ct);
        Say($"swapped: {swapped}; outcome: {installer.LastInstall}");

        Assert.True(swapped, "the junction couldn't be retargeted while the installer was held open");
        Assert.Equal("99", File.ReadAllText(Path.Combine(junction, "exit-code.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "other", "ran.txt")), "the file the junction led to after the hash ran");
        Assert.True(File.Exists(Path.Combine(_root, "hashed", "ran.txt")), "the file hashed didn't run");
        Say($"ran: {File.ReadAllText(Path.Combine(_root, "hashed", "ran.txt"))}");
        Assert.Equal(new PawnIoOutcome(PawnIoOutcome.Installed, null), installer.LastInstall);
        Assert.Equal(1, Volatile.Read(ref installed));
    }

    [Fact]
    public async Task AFolderNamedWithATrailingDot_IsRefused_AndItsUndottedSiblingNeverRuns()
    {
        Assert.SkipUnless(Enabled, Skip);
        // The file hashed in `verified.`, a name only a \\?\ path makes, behind the junction; beside it `verified`, whose
        // file Windows' normalisation of `verified.` would name.
        var plain = StandIn("plain", 0);
        var sibling = StandIn("verified", 99, "the undotted sibling"u8.ToArray());
        var dotted = @"\\?\" + Path.Combine(_root, "verified.");
        Directory.CreateDirectory(dotted);
        _dotted.Add(dotted);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(plain)!))
            File.Copy(file, Path.Combine(dotted, Path.GetFileName(file)));
        var junction = Path.Combine(_root, "pawnio");
        ExactJunction(junction, Path.Combine(_root, "verified."));
        var path = Path.Combine(junction, "PawnIO_setup.exe");
        var sha256 = Sha256Of(path);
        Assert.Equal(Sha256Of(plain), sha256);
        Assert.NotEqual(sha256, Sha256Of(sibling));

        var launched = new List<string>();
        var launcher = new BeforeLaunch(new ShellPawnIoLauncher(), start =>
        {
            Say($"launching {start.FileName} {start.Arguments} as {start.Verb}");
            launched.Add(start.FileName);
        });
        var installed = 0;
        using var installer = new PawnIoInstaller(path, launcher, () => Interlocked.Increment(ref installed), supported: true, sha256: sha256);

        Assert.Equal(PawnIoInstaller.BundledVersion, installer.Available);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        await installer.Run!.WaitAsync(Patience, Ct);
        Say($"launched: [{string.Join(", ", launched)}]; outcome: {installer.LastInstall}");

        Assert.False(File.Exists(Path.Combine(_root, "verified", "ran.txt")), "the undotted sibling, never hashed, ran");
        Assert.False(File.Exists(Path.Combine(dotted, "ran.txt")), "a file ran");
        Assert.Empty(launched);
        Assert.Equal(new PawnIoOutcome(PawnIoOutcome.Failed, null), installer.LastInstall);
        Assert.Equal(0, Volatile.Read(ref installed));
    }

    [Fact]
    public void AnInstallerThatIsASymbolicLink_IsRefused_AndNotOffered_EvenWithThePinnedHash()
    {
        Assert.SkipUnless(Enabled, Skip);
        var real = StandIn("real", 0);
        var place = Path.Combine(_root, "pawnio");
        Directory.CreateDirectory(place);
        var path = Path.Combine(place, "PawnIO_setup.exe");
        File.CreateSymbolicLink(path, real);
        var launched = 0;
        using var installer = new PawnIoInstaller(path, new BeforeLaunch(new ShellPawnIoLauncher(), _ => launched++), () => { },
            supported: true, sha256: Sha256Of(real));

        Assert.Null(installer.Available);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Equal(0, launched);
        Assert.False(installer.Installing);
        Assert.False(File.Exists(Path.Combine(_root, "real", "ran.txt")));
    }

    [Fact]
    public async Task TheShellsRunasLaunch_ExitsZeroThen3010_AndEachComesBackAsItsOutcome()
    {
        Assert.SkipUnless(Enabled, Skip);
        var exe = StandIn("runas", 0);
        var installed = 0;
        using var installer = new PawnIoInstaller(exe, new ShellPawnIoLauncher(), () => Interlocked.Increment(ref installed), supported: true, sha256: Sha256Of(exe));

        Assert.Equal(PawnIoStart.Started, installer.Start());
        await installer.Run!.WaitAsync(Patience, Ct);
        Say($"exit 0: {installer.LastInstall}");
        Assert.Equal(new PawnIoOutcome(PawnIoOutcome.Installed, null), installer.LastInstall);
        Assert.Equal(1, Volatile.Read(ref installed));

        File.WriteAllText(Path.Combine(_root, "runas", "exit-code.txt"), "3010");
        Assert.Equal(PawnIoStart.Started, installer.Start());
        await installer.Run!.WaitAsync(Patience, Ct);
        Say($"exit 3010: {installer.LastInstall}");
        Assert.Equal(new PawnIoOutcome(PawnIoOutcome.RestartNeeded, null), installer.LastInstall);
        Assert.Equal(1, Volatile.Read(ref installed));
        Assert.True(File.Exists(Path.Combine(_root, "runas", "ran.txt")));
    }

    [Fact]
    public async Task TheRealSensorHelpersHello_SaysTheDriverTheEngineReads()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;
        // Built beside these tests: {repo}/InfoPanel.SensorHelper/bin/{configuration}/net8.0-windows/.
        var tests = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tests.Parent!.Name;
        var repo = tests.Parent.Parent!.Parent!.Parent!.FullName;
        var helperPath = Path.Combine(repo, "InfoPanel.SensorHelper", "bin", configuration, "net8.0-windows", "InfoPanel.SensorHelper.exe");
        Assert.True(File.Exists(helperPath), $"no sensor helper built at {helperPath}");

        var engine = PawnIoDriver.Read(new WindowsPawnIoRegistry());
        await using var helper = await new LibreHelperProcess(helperPath).StartAsync(null, Ct).WaitAsync(Patience, Ct);
        var hello = await helper.HelloAsync(Ct).WaitAsync(Patience, Ct);
        Say($"the engine reads {engine}; the helper's hello says {hello.Driver}");

        Assert.Equal(PawnIoDriver.OfHelper(engine, hello.Elevated), hello.Driver);
    }

    /// <summary>Calls <paramref name="before"/> as a launch starts (after the hash, the file still held), then launches.</summary>
    private sealed class BeforeLaunch(IPawnIoLauncher inner, Action<ProcessStartInfo> before) : IPawnIoLauncher
    {
        public IPawnIoProcess? Start(ProcessStartInfo start)
        {
            before(start);
            return inner.Start(start);
        }
    }
}
