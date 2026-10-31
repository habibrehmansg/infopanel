namespace InfoPanel.Hosting;

/// <summary>
/// Opens the editor when the user asks for it (the menu's Open Editor): the sign-in link, through the OS's browser
/// opener (<see cref="BrowserLauncher"/>), which keeps the link out of the log file.
/// </summary>
public sealed class EditorOpener(EngineRuntime runtime, BrowserLauncher browser)
{
    public void Open() => browser.Open(runtime.AuthUrl);
}
