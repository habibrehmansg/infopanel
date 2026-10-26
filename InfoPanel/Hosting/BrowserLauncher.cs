using System.Diagnostics;

namespace InfoPanel.Hosting;

/// <summary>Opens a URL in the user's default browser (<c>open</c> on macOS, shell execute on Windows, <c>xdg-open</c> on Linux).</summary>
public sealed class BrowserLauncher(ILogger<BrowserLauncher> logger)
{
    public void Open(string url)
    {
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(url);
            }

            using var _ = Process.Start(start);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the browser; open the web client link from the log instead");
        }
    }
}
