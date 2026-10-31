using InfoPanel.Desktop;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// What the desktop runs: windows and the menu-bar item on the Mac's main thread, each unless its flag turns it off, and
/// the AppKit loop whenever either is on; nothing off the main thread (every test host) or on another OS yet.
/// </summary>
public sealed class DesktopChoiceTests
{
    private static EngineOptions Options(params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        return EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out _)!;
    }

    [Fact]
    public void OnTheMacsMainThread_WindowsAndTheMenuBarItemAreOn()
    {
        var choice = DesktopHost.Choose(Options(), macOS: true, mainThread: true);

        Assert.True(choice.Windows);
        Assert.True(choice.Tray);
        Assert.True(choice.Loop);
    }

    [Fact]
    public void NoWindows_KeepsTheMenuBarItem_AndTheLoop()
    {
        var choice = DesktopHost.Choose(Options("--no-windows"), macOS: true, mainThread: true);

        Assert.False(choice.Windows);
        Assert.True(choice.Tray);
        Assert.True(choice.Loop);
    }

    [Fact]
    public void NoTray_KeepsTheWindows_WithoutTheMenuBarItem()
    {
        var choice = DesktopHost.Choose(Options("--no-tray"), macOS: true, mainThread: true);

        Assert.True(choice.Windows);
        Assert.False(choice.Tray);
        Assert.True(choice.Loop);
    }

    [Fact]
    public void BothOff_RunsNoLoop()
    {
        var choice = DesktopHost.Choose(Options("--no-tray", "--no-windows"), macOS: true, mainThread: true);

        Assert.False(choice.Windows);
        Assert.False(choice.Tray);
        Assert.False(choice.Loop);
    }

    [Fact]
    public void OffTheMainThread_OrOnAnotherOS_NothingRuns()
    {
        Assert.Equal(default, DesktopHost.Choose(Options(), macOS: true, mainThread: false));
        Assert.Equal(default, DesktopHost.Choose(Options(), macOS: false, mainThread: true));
    }

    [Fact]
    public void TheTestHost_HasNoShell()
    {
        // The test runner's thread isn't the process main thread: no AppKit, no menu-bar item.
        var host = DesktopHost.Create(Options());

        Assert.IsType<NullShellPlatform>(host.Shell);
        Assert.False(host.Shell.IsSupported);
    }

    [Fact]
    public void NoTray_IsAFlag_OffByDefault_AndInTheHelp()
    {
        Assert.False(Options().NoTray);
        Assert.True(Options("--no-tray", "--port", "5199").NoTray);
        Assert.Contains("--no-tray", EngineCommandLine.Usage);
    }

    [Fact]
    public void TheLaunchRecord_KeepsNoTray()
    {
        Assert.Contains("--no-tray", LaunchRecord.ArgumentsOf(Options("--no-tray")));
        Assert.DoesNotContain("--no-tray", LaunchRecord.ArgumentsOf(Options()));
        Assert.Contains("--no-tray", LaunchRecord.Allowed);
    }
}
