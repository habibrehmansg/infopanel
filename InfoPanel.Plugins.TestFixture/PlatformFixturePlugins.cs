using System.Runtime.Versioning;
using InfoPanel.Plugins;

namespace InfoPanel.TestFixture;

/// <summary>
/// A plugin type marked for Windows whose constructor fails elsewhere, as a plugin built on a Windows-only library
/// does: the loader must skip it on another OS rather than create it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOnlyFixturePlugin : BasePlugin
{
    public WindowsOnlyFixturePlugin() : base("windows-only-fixture", "Windows-only fixture", "")
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("fixture: Windows only");
    }

    public override TimeSpan UpdateInterval => TimeSpan.Zero;
    public override void Initialize() { }
    public override void Load(List<IPluginContainer> containers) { }
    public override void Update() { }
    public override Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Close() { }
}

/// <summary>The same for macOS and Linux: whichever OS the tests run on, one of these two is for another.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
public sealed class UnixOnlyFixturePlugin : BasePlugin
{
    public UnixOnlyFixturePlugin() : base("unix-only-fixture", "Unix-only fixture", "")
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("fixture: macOS and Linux only");
    }

    public override TimeSpan UpdateInterval => TimeSpan.Zero;
    public override void Initialize() { }
    public override void Load(List<IPluginContainer> containers) { }
    public override void Update() { }
    public override Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Close() { }
}
