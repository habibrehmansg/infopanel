using System.Diagnostics;

namespace InfoPanel.Hosting;

/// <summary>
/// Opens a URL in the user's default browser (<c>open</c> on macOS, shell execute on Windows, <c>xdg-open</c> on Linux).
/// The process start is the seam tests replace, so no test opens a browser.
/// </summary>
public class BrowserLauncher(ILogger<BrowserLauncher> logger, Func<ProcessStartInfo, Process?> start)
{
    public BrowserLauncher(ILogger<BrowserLauncher> logger) : this(logger, Process.Start) { }

    public virtual void Open(string url)
    {
        try
        {
            ProcessStartInfo command;
            if (OperatingSystem.IsWindows())
            {
                command = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else
            {
                command = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                command.ArgumentList.Add(url);
            }

            using var _ = start(command);
        }
        catch (Exception ex)
        {
            // The error can name what was started, the URL, and a sign-in link carries the token (on Windows .NET puts the
            // file name, the link, in it): it goes to the console only, as the link does. The log file, and the entries kept
            // for clients, get the failure by the error's type, which names no URL.
            using (logger.BeginScope(LogTargets.Only(LogTargets.Console)))
                logger.LogWarning(ex, "Could not open the browser; open the editor link above instead");
            using (logger.BeginScope(LogTargets.Only(LogTargets.File)))
                logger.LogWarning("Could not open the browser ({Error}); open the editor link the engine printed on its console instead", ex.GetType().FullName);
        }
    }
}
