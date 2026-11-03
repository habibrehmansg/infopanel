using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using InfoPanel.SensorHelper;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The task's helper reading a real engine-like client, opt-in (<c>INFOPANEL_WINDOWS_SESSION_TESTS=1</c> on Windows, run
/// elevated while the test's user is signed in at the PC; skipped elsewhere), as the helper runs: its pipe created and its
/// client read by SYSTEM in session 0 (<c>InfoPanel.SensorHelper.TestHost client-of</c>, the helper's own code, run by a
/// temporary task as SYSTEM), the client a process of the test's user in their desktop session, unelevated (medium
/// integrity), connecting at identification level (a hidden PowerShell, <c>conhost --headless</c>, run by a temporary
/// task with the user's own token). Both tasks are removed after. W5.2 PC check 1 found every such client refused:
/// .NET's <c>RunAsClient</c> throws <c>ERROR_BAD_IMPERSONATION_LEVEL</c> there (an elevated server of the same user
/// doesn't show it, so the server here is SYSTEM).
/// </summary>
public sealed class HelperPipeSessionWindowsTests
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_SESSION_TESTS") == "1";

    private const string Skip = "Runs SYSTEM's side through a task and a client in the signed-in user's desktop session: set INFOPANEL_WINDOWS_SESSION_TESTS=1 on Windows, elevated, to run it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "InfoPanel.SensorHelper.TestHost.exe");

    [Fact]
    public async Task AnIdentificationLevelClient_InTheUsersDesktopSession_IsReadBySystem_ItsUserAtMediumIntegrity()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;

        using var me = WindowsIdentity.GetCurrent();
        var sid = me.User!.Value;
        var id = Guid.NewGuid().ToString("N")[..12];
        var pipe = "ipsensors-test-" + id;
        var output = Path.Combine(AppContext.BaseDirectory, "client-of-" + id + ".txt");
        var server = "InfoPanel test server " + id;
        var client = "InfoPanel test client " + id;
        try
        {
            Register(server, Principal("S-1-5-18", "HighestAvailable", null), HostPath, $"client-of {pipe} {sid} \"{output}\"", logonType: 5);
            Run(server);
            await Task.Delay(1000, Ct); // its pipe created
            var script = "$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + pipe + "', [System.IO.Pipes.PipeDirection]::InOut, "
                + "[System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification); "
                + "$c.Connect(30000); $c.WriteByte(1); $c.Flush(); Start-Sleep -Seconds 5; $c.Dispose()";
            Register(client, Principal(me.Name, "LeastPrivilege", "InteractiveToken"), Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                "--headless powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), logonType: 3);
            Run(client);

            var until = Stopwatch.StartNew();
            while (!File.Exists(output) && until.Elapsed < TimeSpan.FromSeconds(90))
                await Task.Delay(250, Ct);
            await Task.Delay(250, Ct); // its lines written
            var said = File.ReadAllLines(output).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

            Assert.Equal("S-1-5-18", said["server"]);
            Assert.NotEqual("0", said["session"]); // the user's desktop session, not SYSTEM's
            Assert.Equal($"{sid} {HelperTask.MediumIntegrity}", said["client"]);
            Assert.Equal("1", said["read"]); // the helper itself again: it reads its pipe
            Assert.False(said.ContainsKey("error"), said.GetValueOrDefault("error"));
        }
        finally
        {
            Delete(server);
            Delete(client);
            File.Delete(output);
        }
    }

    private static XElement Principal(string user, string runLevel, string? logonType)
    {
        XNamespace t = HelperTask.TaskNamespace;
        return new XElement(t + "Principal", new XAttribute("id", "Author"), new XElement(t + "UserId", user),
            logonType is null ? null : new XElement(t + "LogonType", logonType), new XElement(t + "RunLevel", runLevel));
    }

    [SupportedOSPlatform("windows")]
    private static dynamic Root()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    [SupportedOSPlatform("windows")]
    private static void Register(string name, XElement principal, string command, string arguments, int logonType)
    {
        XNamespace t = HelperTask.TaskNamespace;
        var xml = new XElement(t + "Task", new XAttribute("version", "1.2"),
            new XElement(t + "Principals", principal),
            new XElement(t + "Settings", new XElement(t + "AllowStartOnDemand", "true"), new XElement(t + "ExecutionTimeLimit", "PT2M"),
                new XElement(t + "DisallowStartIfOnBatteries", "false"), new XElement(t + "StopIfGoingOnBatteries", "false")),
            new XElement(t + "Actions", new XAttribute("Context", "Author"),
                new XElement(t + "Exec", new XElement(t + "Command", command), new XElement(t + "Arguments", arguments))));
        Root().RegisterTask(name, xml.ToString(), 6 /* create or update */, logonType == 5 ? "SYSTEM" : null, null, logonType);
    }

    [SupportedOSPlatform("windows")]
    private static void Run(string name) => Root().GetTask(name).Run(null);

    [SupportedOSPlatform("windows")]
    private static void Delete(string name)
    {
        try
        {
            var root = Root();
            try
            {
                root.GetTask(name).Stop(0);
            }
            catch (COMException)
            {
            }

            root.DeleteTask(name, 0);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            // never registered
        }
    }
}
