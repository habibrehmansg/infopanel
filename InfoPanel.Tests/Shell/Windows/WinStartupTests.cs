using System.Runtime.Versioning;
using InfoPanel.Shell.Windows;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// Start when I sign in: the <c>InfoPanel</c> value under the user's Run key, read as Task Manager reads it (its
/// StartupApproved entry can turn it off), written with the engine's own path, removed with its StartupApproved entry.
/// Through <see cref="FakeRegistry"/> on any OS; the registry itself on Windows, under a key of the test's own.
/// </summary>
public sealed class WinStartupTests
{
    private const string Command = "\"C:\\Program Files\\InfoPanel\\InfoPanel.exe\"";

    private readonly FakeRegistry _registry = new();

    private WinStartup Startup() => new(_registry, Command);

    private static byte[] Approved(byte state) => [state, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Off_WithoutTheRunValue_On_WithIt()
    {
        Assert.False(Startup().IsOn());

        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, "\"D:\\elsewhere\\InfoPanel.exe\"");

        Assert.True(Startup().IsOn());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    public void TaskManagersSwitch_IsRead(byte state, bool on)
    {
        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, Command);
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(state);

        Assert.Equal(on, Startup().IsOn());
    }

    [Fact]
    public void TurningItOn_WritesTheEnginesPath_UnderTheRunKey_AndClearsTaskManagersSwitch()
    {
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(3);

        Startup().Set(true);

        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", WinStartup.RunKey);
        Assert.Equal("InfoPanel", WinStartup.ValueName);
        var (key, value) = Assert.Single(_registry.Values);
        Assert.Equal((WinStartup.RunKey, WinStartup.ValueName), key);
        Assert.Equal(Command, value);
        Assert.True(Startup().IsOn());
    }

    [Fact]
    public void TurningItOff_RemovesTheValue_AndTaskManagersSwitch()
    {
        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, Command);
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(2);
        _registry.SetString(WinStartup.RunKey, "Other app", "other.exe");

        Startup().Set(false);

        Assert.Equal((WinStartup.RunKey, "Other app"), Assert.Single(_registry.Values).Key);
        Assert.False(Startup().IsOn());
    }

    [Fact]
    public void TheCommand_IsTheEnginesQuotedPath_OrTheDotnetHostWithTheEngine()
    {
        Assert.Equal("\"C:\\Program Files\\InfoPanel\\InfoPanel.exe\"",
            WinStartup.CommandFor(@"C:\Program Files\InfoPanel\InfoPanel.exe", @"C:\Program Files\InfoPanel\InfoPanel.dll"));
        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\ip\\engine\\InfoPanel.dll\"",
            WinStartup.CommandFor(@"C:\Program Files\dotnet\dotnet.exe", @"C:\ip\engine\InfoPanel.dll"));
    }

    [Fact]
    public void TheRegistry_ReadsWritesAndDeletesAValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry exists only on Windows.");
        if (OperatingSystem.IsWindows())
            RegistryRoundTrip();
    }

    [SupportedOSPlatform("windows")]
    private static void RegistryRoundTrip()
    {
        var key = @"Software\InfoPanelTests\" + Guid.NewGuid().ToString("N");
        var registry = new WinRegistry();
        try
        {
            Assert.Null(registry.Get(key, "InfoPanel"));
            registry.SetString(key, "InfoPanel", Command);
            Assert.Equal(Command, registry.Get(key, "InfoPanel"));
            registry.Delete(key, "InfoPanel");
            registry.Delete(key, "InfoPanel");
            Assert.Null(registry.Get(key, "InfoPanel"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
        }
    }
}
