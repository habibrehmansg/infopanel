namespace InfoPanel.Hosting;

/// <summary>
/// Opens the editor when the user asks for it (the menu's Open Editor, a second launch; never by itself):
/// <see cref="EngineRuntime.EditorLink"/>, the configured web client's pairing link or the sign-in link, through
/// <see cref="BrowserLauncher"/>, which keeps the link out of every process's arguments and out of the log file.
/// </summary>
public sealed class EditorOpener(EngineRuntime runtime, BrowserLauncher browser)
{
    public void Open() => browser.Open(runtime.EditorLink);
}
