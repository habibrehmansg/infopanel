using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using InfoPanel.Documents;

namespace InfoPanel.Persistence;

/// <summary>
/// The engine's data folder. It keeps the 1.x layout (<c>profiles.xml</c>, <c>profiles/{guid}.xml</c>,
/// <c>assets/{guid}/</c>) and adds <c>fonts/</c>, <c>logs/</c>, <c>runtime.json</c> and <c>engine.lock</c>.
/// The 2.0 document store (<see cref="DocumentStore"/>, <see cref="BlobStore"/>) adds <c>profiles.json</c>,
/// <c>profiles/{id}/document.json</c> and <c>blobs/{sha256 hex}</c>; the engine adds <c>tokens.json</c>, and the 1.x
/// import its run-once record <c>import-v1.json</c>. <see cref="EngineFiles"/> and <see cref="EngineFolders"/> list them.
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

    /// <summary>The 1.x migration's run-once record, in the data folder.</summary>
    public const string ImportRecordFileName = "import-v1.json";

    /// <summary>
    /// The top-level files the engine writes in its data folder: the 2.0 store's profile order, the integration
    /// tokens, the runtime file, the lock, step-1's profile list (which this engine leaves alone) and the 1.x
    /// migration's run-once record. The one list of them: <c>--fresh</c> wipes only a folder that holds nothing else,
    /// and the 1.x import keeps the 1.x folder clear of them.
    /// </summary>
    public static IReadOnlyList<string> EngineFiles { get; } =
        ["profiles.json", "tokens.json", "runtime.json", "engine.lock", "profiles.xml", ImportRecordFileName];

    /// <summary>The top-level folders the engine writes in its data folder (<c>assets</c> is step-1's).</summary>
    public static IReadOnlyList<string> EngineFolders { get; } = ["profiles", "blobs", "fonts", "logs", "assets"];

    /// <summary>The engine files written by renaming a temp file (<c>{name}.{32 hex}.tmp</c>) over them.</summary>
    private static readonly string[] ReplacedFiles = ["profiles.json", "tokens.json", "runtime.json", "profiles.xml", ImportRecordFileName];

    private static readonly Regex EngineTempFile = new(
        $"^({string.Join('|', ReplacedFiles.Select(Regex.Escape))})\\.[0-9a-f]{{32}}\\.tmp$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="name"/> (matched ignoring case) is a top-level entry the engine writes in its data
    /// folder: one of <see cref="EngineFiles"/> or <see cref="EngineFolders"/>, or a temp file an interrupted write of
    /// one of those files left.
    /// </summary>
    public static bool IsEngineEntry(string name) =>
        EngineFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || EngineFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
        || EngineTempFile.IsMatch(name);

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

    /// <summary>The display order of the stored documents (<c>{"order":[ids]}</c>).</summary>
    public string ProfileOrderFile => Path.Combine(Root, "profiles.json");

    /// <summary>A stored document's folder.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid profile id (<see cref="DocumentStore.IsValidId"/>).</exception>
    public string ProfileFolder(string id) =>
        DocumentStore.IsValidId(id) ? Path.Combine(ProfilesDirectory, id) : throw new ArgumentException($"'{id}' is not a valid profile id.", nameof(id));

    /// <summary>A stored document's saved generation.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid profile id.</exception>
    public string DocumentFile(string id) => Path.Combine(ProfileFolder(id), DocumentStore.DocumentFileName);

    /// <summary>The integration tokens: their records and the SHA-256 of each secret, never a secret.</summary>
    public string TokensFile => Path.Combine(Root, "tokens.json");

    /// <summary>Stored blobs, one file per content hash.</summary>
    public string BlobsDirectory => Path.Combine(Root, "blobs");

    /// <summary>A blob's file, <c>blobs/{64 hex}</c>, for its id <c>sha256:{64 hex}</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid blob id (<see cref="BlobStore.IsValidId"/>).</exception>
    public string BlobFile(string id) =>
        BlobStore.IsValidId(id) ? Path.Combine(BlobsDirectory, id[BlobStore.IdPrefix.Length..]) : throw new ArgumentException($"'{id}' is not a valid blob id.", nameof(id));
}
