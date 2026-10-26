using System;
using System.IO;

namespace InfoPanel.Persistence;

/// <summary>
/// The engine's data folder. It keeps the 1.x layout (<c>profiles.xml</c>, <c>profiles/{guid}.xml</c>,
/// <c>assets/{guid}/</c>) and adds <c>fonts/</c>, <c>logs/</c>, <c>runtime.json</c> and <c>engine.lock</c>.
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// <c>{LocalApplicationData}/InfoPanel/v2</c>: <c>%LOCALAPPDATA%\InfoPanel\v2</c> on Windows,
    /// <c>~/Library/Application Support/InfoPanel/v2</c> on macOS. 1.x data in <c>InfoPanel/</c> is left alone.
    /// </summary>
    public static DataPaths DefaultRoot() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "InfoPanel", "v2"));

    public string Root { get; }

    /// <summary>The profile list.</summary>
    public string ProfilesFile => Path.Combine(Root, "profiles.xml");

    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    /// <summary>A profile's display items.</summary>
    public string ProfileItemsFile(Guid profileId) => Path.Combine(ProfilesDirectory, profileId + ".xml");

    public string AssetsDirectory => Path.Combine(Root, "assets");

    /// <summary>A profile's assets (images referenced by relative paths).</summary>
    public string ProfileAssetsDirectory(Guid profileId) => Path.Combine(AssetsDirectory, profileId.ToString());

    /// <summary>User-added fonts.</summary>
    public string FontsDirectory => Path.Combine(Root, "fonts");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Written by the running engine (pid, port, url) so launchers and companions can find it.</summary>
    public string RuntimeFile => Path.Combine(Root, "runtime.json");

    /// <summary>Held by the running engine so a second instance can detect it.</summary>
    public string EngineLockFile => Path.Combine(Root, "engine.lock");
}
