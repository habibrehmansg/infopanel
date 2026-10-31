using InfoPanel.Desktop;
using InfoPanel.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;
using static InfoPanel.Desktop.DesktopHost;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// The desktop chosen per OS (an injected OS) and per <c>--no-windows</c>: on Windows, windows on the engine's own UI
/// thread whatever thread the entry point is on, off with <c>--no-windows</c>, and no tray yet; on Linux nothing, saying
/// so; on macOS as before.
/// </summary>
public sealed class DesktopHostTests
{
    private static EngineOptions Options(params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        return EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out _)!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnWindows_WindowsRun_OnAnyThread_WithoutATrayYet(bool mainThread)
    {
        var choice = Choose(Options(), DesktopOS.Windows, mainThread);

        Assert.True(choice.Windows);
        Assert.False(choice.Tray);
        Assert.True(choice.Loop);
    }

    [Fact]
    public void OnWindows_NoWindows_RunsNothing()
    {
        Assert.Equal(default, Choose(Options("--no-windows"), DesktopOS.Windows, mainThread: true));
        Assert.Null(UnsupportedMessage(Options("--no-windows"), DesktopOS.Windows));
    }

    [Fact]
    public void OnLinux_NothingRuns_AndAProfileWithAWindowIsToldWhy()
    {
        Assert.Equal(default, Choose(Options(), DesktopOS.Other, mainThread: true));
        Assert.Equal("Desktop windows aren't supported on Linux yet", UnsupportedMessage(Options(), DesktopOS.Other));
        Assert.Null(UnsupportedMessage(Options("--no-windows"), DesktopOS.Other));
    }

    [Fact]
    public void OnMacOS_TheChoiceIsAsBefore()
    {
        Assert.Equal(new Choice(true, true), Choose(Options(), DesktopOS.MacOS, mainThread: true));
        Assert.Equal(default, Choose(Options(), DesktopOS.MacOS, mainThread: false));
        Assert.Equal("Desktop windows need the engine's entry point on the main thread; not showing them", UnsupportedMessage(Options(), DesktopOS.MacOS));
    }

    [Fact]
    public void ThisProcess_IsOnItsOwnOS()
    {
        var expected = OperatingSystem.IsMacOS() ? DesktopOS.MacOS : OperatingSystem.IsWindows() ? DesktopOS.Windows : DesktopOS.Other;
        Assert.Equal(expected, CurrentOS);
    }
}
