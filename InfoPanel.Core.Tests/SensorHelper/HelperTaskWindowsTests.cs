using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors.Libre;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The sensor helper's task on real Windows, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped elsewhere).
/// Nothing here registers, runs or removes a task, starts anything elevated, or needs SYSTEM: the definition is read by
/// the Task Scheduler's schema into a new definition never registered; the pipe is created by the test's own process
/// with the helper's real security (owned by the test's user, since only SYSTEM can make SYSTEM its owner), and its owner
/// and DACL read back through the engine's own check; a squatter pipe its user owns is refused by the helper's choice.
/// </summary>
public sealed class HelperTaskWindowsTests
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Reads Windows' Task Scheduler, pipes and users: set INFOPANEL_WINDOWS_TESTS=1 on Windows to run it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Me()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    [Fact]
    public void TheTasksDefinition_IsReadByTheTaskSchedulersSchema()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        WindowsTaskScheduler.Validate(HelperTask.Xml(@"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", Me()));
        Assert.ThrowsAny<Exception>(() => WindowsTaskScheduler.Validate("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Nonsense/></Task>"));
    }

    [Fact]
    public void ProgramFiles_ComesFromTheKnownFolder_AndTheUsersAccountIsAUser()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        var folder = HelperWindows.ProgramFiles();
        Assert.NotNull(folder);
        Assert.True(Directory.Exists(folder));
        Assert.True(HelperWindows.IsUserAccount(Me()));
        Assert.False(HelperWindows.IsUserAccount("S-1-5-32-544")); // Administrators: a group
        Assert.False(HelperWindows.IsUserAccount("S-1-5-21-1-2-3-4294967290")); // nobody
    }

    [Fact]
    public async Task ThePipe_IsOwnedAsCreated_ItsDaclGrantsTheUserAndSystemOnly_AndDeniesTheNetwork()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        var me = Me();
        var name = "ipsensors-test-" + Guid.NewGuid().ToString("N")[..12];
        using var server = WindowsHelperPipe.Create(name, me, owner: me);
        var accepted = server.WaitForConnectionAsync(Ct);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(10_000, Ct);
        await accepted;

        Assert.Equal(me, WindowsHelperPipe.OwnerOf(client.SafePipeHandle));
        Assert.Equal(me, new WindowsLibreElevationSystem().PipeOwner(client));
        var served = WindowsHelperPipe.ClientOf(server);
        Assert.Equal(me, served?.Sid);
        Assert.True(served?.Integrity >= HelperTask.MediumIntegrity, $"integrity {served?.Integrity}"); // this test's own process: medium, or high elevated

        var security = server.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
            .Select(r => (Sid: ((SecurityIdentifier)r.IdentityReference).Value, r.AccessControlType, r.PipeAccessRights)).ToList();
        Assert.Contains(("S-1-5-2", AccessControlType.Deny, PipeAccessRights.FullControl), rules);
        Assert.Contains(("S-1-5-18", AccessControlType.Allow, PipeAccessRights.FullControl), rules);
        Assert.Contains(rules, r => r.Sid == me && r.AccessControlType == AccessControlType.Allow && (r.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite
            && (r.PipeAccessRights & (PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership | PipeAccessRights.CreateNewInstance)) == 0);
        Assert.Equal(3, rules.Count);
    }

    [Fact]
    public void ASecondPipeOfTheSameName_IsRefused_FirstInstance()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        var me = Me();
        var name = "ipsensors-test-" + Guid.NewGuid().ToString("N")[..12];
        using var first = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.ThrowsAny<Exception>(() => WindowsHelperPipe.Create(name, me, owner: me).Dispose());
    }

    [Fact]
    public async Task ASquatterPipeTheUserOwns_IsRefused_AndTheChildStartsInstead()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        var name = "ipsensors-test-" + Guid.NewGuid().ToString("N")[..12];
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = squatter.WaitForConnectionAsync(Ct);
        var child = new Sensors.Libre.ScriptedLauncher();
        var system = new SquatterSystem();
        var chooser = new LibreHelperChooser(child, () => Me(), system, pipeName: _ => name);

        await using var helper = await chooser.StartAsync(null, Ct);
        await accepted;

        Assert.Equal(1, child.Starts);
        Assert.Contains(Me(), chooser.LastFallback?.Message);
        Assert.Contains("not SYSTEM", chooser.LastFallback?.Message);
        Assert.Equal(LibreHelperChooser.NotSystem, chooser.LastFallback?.Code);
        Assert.Equal(0, await squatter.ReadAsync(new byte[16], Ct)); // nothing written before the owner was checked
    }

    /// <summary>Windows' own pipe owner, the rest a stand-in (no task is run).</summary>
    [SupportedOSPlatform("windows")]
    private sealed class SquatterSystem : ILibreElevationSystem
    {
        private readonly WindowsLibreElevationSystem _real = new();

        public bool EngineElevated => false;

        public string? UserSid => _real.UserSid;

        public string? ProgramFiles => _real.ProgramFiles;

        public string FinalPathOf(FileStream file) => _real.FinalPathOf(file);

        public (string Path, string Arguments)? TaskAction(string sid) => null;

        public void RunTask(string sid)
        {
        }

        public string? PipeOwner(NamedPipeClientStream pipe) => _real.PipeOwner(pipe);
    }
}
