using System.Reflection;

namespace InfoPanel.Utils;

public static class VersionHelper
{
    public static string AppVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var infoVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (infoVersion != null)
        {
            // Strip +commitHash build metadata (e.g., "1.4.0-preview.1+abc1234" → "1.4.0-preview.1")
            var plusIdx = infoVersion.IndexOf('+');
            return plusIdx >= 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
