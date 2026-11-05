using InfoPanel.Security;

namespace InfoPanel.Hosting;

/// <summary>
/// Opens the editor when the user asks for it (the menu's Open Editor, a second launch; never by itself):
/// <see cref="EngineRuntime.EditorLinkFor"/>, the configured web client's code link, with a one-time pairing code minted
/// for this open (<see cref="PairingCodes"/>) and no session token, or the sign-in link, through
/// <see cref="BrowserLauncher"/>, which keeps the link out of every process's arguments and out of the log file.
/// </summary>
public sealed class EditorOpener(EngineRuntime runtime, BrowserLauncher browser, PairingCodes codes)
{
    /// <summary>The editor at its own start.</summary>
    public void Open() => Open(then: null);

    /// <param name="then">The page the editor opens at, one of <see cref="EditorRoutes.Known"/> (the menu's Edit from Another Computer…); null: its own start.</param>
    public void Open(string? then) => browser.Open(runtime.EditorLinkFor(codes.Mint, then));
}
