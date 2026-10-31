using InfoPanel.Desktop;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// Test engines open no real desktop window on any OS: on macOS the test host is off the main thread, but on Windows
/// nothing like that guards them, and a seed or a commit that turns a profile's window on would open one. Every
/// <see cref="EngineFactory"/> engine has a platform that shows nothing; a test that wants windows brings its own (a
/// <see cref="FakeDesktopPlatform"/>), which replaces it.
/// </summary>
public sealed class TestEngineDesktopTests
{
    [Fact]
    public void ATestEngine_ShowsNoDesktopWindows_WhateverTheOS()
    {
        using var engine = new EngineFactory();

        var platform = engine.Services.GetRequiredService<IDesktopPlatform>();
        Assert.IsType<NullDesktopPlatform>(platform);
        Assert.False(platform.IsSupported);
        Assert.Equal("Test engines show no desktop windows", platform.UnsupportedMessage);
        Assert.False(engine.Services.GetRequiredService<DisplayWindowManager>().Enabled);
    }

    [Fact]
    public async Task ATestEngineWithAFakeDesktop_ShowsWindowsOnIt()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(new DisplayInfo(@"\\.\DISPLAY1", new PointRect(0, 0, 2560, 1440), 1));
        using var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IDesktopPlatform>(platform)));

        Assert.Same(platform, factory.Services.GetRequiredService<IDesktopPlatform>());
        Assert.True(factory.Services.GetRequiredService<DisplayWindowManager>().Enabled);
        await platform.IdleAsync();
    }
}
