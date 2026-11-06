namespace InfoPanel.Plugins;

/// <summary>The operating systems a package's <c>Platforms</c> names: <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
public static class PluginPlatforms
{
    /// <summary>The operating system the engine runs on, as <c>Platforms</c> names it; <c>other</c> for any other.</summary>
    public static string Current { get; } =
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
}
