using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InfoPanel.Documents;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>What a 1.x image path is, before anything is looked up.</summary>
public enum V1PathKind
{
    /// <summary>Relative to the profile's assets; <see cref="V1Path.Segments"/> is where, <c>.</c> and <c>..</c> resolved.</summary>
    Relative,

    /// <summary>Not relative to the profile's assets (<c>relativePath</c> false, a drive, rooted, UNC, or holding <c>:</c>): never read.</summary>
    Absolute,

    /// <summary>Not a file path: a <c>scheme://</c> URL, or a character no file name holds.</summary>
    NotAFilePath,

    /// <summary>Relative, but leaving the assets folder (more <c>..</c> than folders), or naming nothing.</summary>
    Escapes,
}

public readonly record struct V1Path(V1PathKind Kind, IReadOnlyList<string> Segments)
{
    /// <summary>
    /// Classifies a 1.x image path as 1.4.x would combine it with the profile's assets folder: <c>\</c> and <c>/</c> are
    /// both separators; a path that is rooted, has a drive or holds <c>:</c> makes <c>Path.Combine</c> ignore the folder.
    /// </summary>
    public static V1Path Classify(string path, bool relative)
    {
        if (path.Contains("://", StringComparison.Ordinal) || path.Contains('\0'))
            return new(V1PathKind.NotAFilePath, []);
        if (!relative || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'))
            return new(V1PathKind.Absolute, []);

        var segments = new List<string>();
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return new(V1PathKind.Escapes, []);
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? new(V1PathKind.Escapes, []) : new(V1PathKind.Relative, segments);
    }
}

/// <summary>Where a relative 1.x image path was found.</summary>
public enum V1AssetLookup { Found, Missing, Link }

/// <summary>A profile's assets: a 1.x folder's <c>assets/{guid}/</c> or a 1.x archive's <c>assets/</c> entries.</summary>
public interface IV1AssetSource
{
    /// <summary>Finds the file at <paramref name="segments"/> (names matched case-insensitively); <paramref name="file"/> is its location.</summary>
    V1AssetLookup Find(IReadOnlyList<string> segments, out object? handle, out string file);

    /// <summary>
    /// The file's bytes, counted against the request's budget; null when over what <paramref name="cap"/> answers for its
    /// first <paramref name="headBytes"/> bytes (reading stops there).
    /// </summary>
    /// <exception cref="ImportBudgetExceededException">A folder read went over the request's budget.</exception>
    /// <exception cref="ImportRefusal">An archive read went over it (<c>archive.expanded</c>) or failed.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    byte[]? Read(object handle, ReadCap cap, int headBytes);
}

/// <summary>
/// A 1.x folder profile's assets, <c>{v1}/assets/{guid}/</c>. Every name is matched case-insensitively (enumerated,
/// then compared), so a folder copied to a case-sensitive file system still resolves; a link at any step is never
/// followed. Files are opened read-only and shared, regular files only (<see cref="RegularFile.OpenRead"/>); nothing is
/// created, locked or written.
/// </summary>
public sealed class V1FolderAssets : IV1AssetSource
{
    private readonly string? _directory;
    private readonly V1AssetLookup _base;
    private readonly string _label;
    private readonly ImportBudget _budget;

    /// <param name="root">The 1.x folder.</param>
    /// <param name="profileId">The profile's 1.x Guid, the name of its assets folder.</param>
    public V1FolderAssets(string root, Guid profileId, ImportBudget budget)
    {
        _budget = budget;
        _label = "assets/" + profileId;
        _base = Walk(root, ["assets", profileId.ToString()], directoryAtEnd: true, out _directory);
    }

    public V1AssetLookup Find(IReadOnlyList<string> segments, out object? handle, out string file)
    {
        file = _label + "/" + string.Join('/', segments);
        handle = null;
        if (_base != V1AssetLookup.Found)
            return _base;
        var found = Walk(_directory!, segments, directoryAtEnd: false, out var path);
        handle = path;
        return found;
    }

    public byte[]? Read(object handle, ReadCap cap, int headBytes)
    {
        // A regular file only, opened without waiting: a FIFO, socket or device there is refused, not waited on.
        using var stream = RegularFile.OpenRead((string)handle, FileShare.ReadWrite | FileShare.Delete);
        return _budget.ReadAll(stream, cap, headBytes);
    }

    /// <summary>
    /// Walks <paramref name="segments"/> down from <paramref name="start"/>, matching each name case-insensitively and
    /// refusing links; the last segment must be a directory or a file as asked.
    /// </summary>
    internal static V1AssetLookup Walk(string start, IReadOnlyList<string> segments, bool directoryAtEnd, out string? path)
    {
        path = null;
        var current = start;
        for (var i = 0; i < segments.Count; i++)
        {
            if (Match(current, segments[i]) is not { } next)
                return V1AssetLookup.Missing;
            if (IsLink(next))
                return V1AssetLookup.Link;

            var last = i == segments.Count - 1;
            if (last && !directoryAtEnd)
            {
                if (!File.Exists(next))
                    return V1AssetLookup.Missing;
            }
            else if (!Directory.Exists(next))
            {
                return V1AssetLookup.Missing;
            }
            current = next;
        }
        path = current;
        return V1AssetLookup.Found;
    }

    /// <summary>The entry of <paramref name="directory"/> named <paramref name="name"/>, ignoring case (an exact match first).</summary>
    internal static string? Match(string directory, string name)
    {
        try
        {
            var exact = Path.Combine(directory, name);
            if (File.Exists(exact) || Directory.Exists(exact) || IsLink(exact))
                return exact;
            if (!Directory.Exists(directory))
                return null;
            return Directory.EnumerateFileSystemEntries(directory)
                .Where(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is not null || (info.Exists || Directory.Exists(path)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// A 1.x archive's assets: entries starting <c>assets/</c> or <c>assets\</c> (any case), keyed by the rest of their
/// name with <c>/</c> separators and matched case-insensitively. Of two entries with one key the later wins, as 1.4.x
/// extracted with overwrite (<c>import.asset.duplicate</c>). An item path with a sub-folder resolves against the same
/// sub-folder (1.4.x flattened them, so such an item drew the placeholder; its own exports have none).
/// </summary>
public sealed class V1ArchiveAssets : IV1AssetSource
{
    private readonly ArchiveReader _reader;
    private readonly Dictionary<string, ArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public V1ArchiveAssets(ArchiveReader reader, IEnumerable<ArchiveEntry> assetEntries, ImportDiagnostics diagnostics)
    {
        _reader = reader;
        foreach (var entry in assetEntries)
        {
            if (entry.IsDirectory || entry.Segments.Count < 2)
                continue;
            var key = string.Join('/', entry.Segments.Skip(1));
            if (_entries.TryGetValue(key, out var earlier))
                diagnostics.Add(ImportDiagnostics.Info, "import.asset.duplicate", "",
                    $"Two entries name the asset '{key}'; the later one, '{entry.Name}', is used, as 1.x extracted it over the earlier.", earlier.Name);
            _entries[key] = entry;
        }
    }

    /// <summary>The asset entries no item used.</summary>
    public int Unused => _entries.Keys.Count(k => !_used.Contains(k));

    public V1AssetLookup Find(IReadOnlyList<string> segments, out object? handle, out string file)
    {
        var key = string.Join('/', segments);
        if (_entries.TryGetValue(key, out var entry))
        {
            _used.Add(key);
            handle = entry;
            file = entry.Name;
            return V1AssetLookup.Found;
        }
        handle = null;
        file = "assets/" + key;
        return V1AssetLookup.Missing;
    }

    public byte[]? Read(object handle, ReadCap cap, int headBytes) => _reader.Read((ArchiveEntry)handle, cap, headBytes);
}

/// <summary>An image file items use: the props as stored, and the first item using it (for messages).</summary>
public sealed record V1AssetUse(string Path, bool Relative, string ItemPath, string? ItemName);

/// <summary>
/// The assets of a 1.x profile: each distinct (<c>filePath</c>, <c>relativePath</c>) pair the items draw from a file
/// (gauge frames included), resolved inside the profile's assets, checked as media, and stored as <c>POST /api/blobs</c>
/// stores it, only when it passes. Absolute paths are never read, links never followed.
/// </summary>
public static class V1Assets
{
    /// <summary>Every file image of <paramref name="items"/>, in tree order, with the path of the first item using it.</summary>
    public static IReadOnlyList<V1AssetUse> Uses(IReadOnlyList<DisplayItem> items, Func<DisplayItem, string?> pathOf)
    {
        var seen = new HashSet<(string, bool)>();
        var result = new List<V1AssetUse>();
        foreach (var item in ItemTree.Walk(items))
        {
            // An HTTP sensor image's source is its sensor: its Type hides the base one, which stays FILE, so a leftover
            // FilePath isn't a file it draws. SensorImageDisplayItem doesn't hide Type and is read as any image.
            if (item is HttpImageDisplayItem
                || item is not ImageDisplayItem { Type: ImageDisplayItem.ImageType.FILE } image || string.IsNullOrEmpty(image.FilePath))
                continue;
            if (seen.Add((image.FilePath, image.RelativePath)))
                result.Add(new V1AssetUse(image.FilePath, image.RelativePath, pathOf(item) ?? "/items", item.Name));
        }
        return result;
    }

    public static IReadOnlyList<V1Asset> Resolve(IReadOnlyList<V1AssetUse> uses, IV1AssetSource source, Workspace workspace,
        ImportBudget budget, ImportDiagnostics diagnostics)
    {
        var assets = new List<V1Asset>(uses.Count);
        for (var i = 0; i < uses.Count; i++)
            assets.Add(Resolve(uses[i], $"/assets/{i}", source, workspace, budget, diagnostics));
        return assets;
    }

    private static V1Asset Resolve(V1AssetUse use, string at, IV1AssetSource source, Workspace workspace, ImportBudget budget, ImportDiagnostics diagnostics)
    {
        var asset = new V1Asset { Path = use.Path, Relative = use.Relative };
        var who = use.ItemName is { Length: > 0 } name ? $"'{name}' ({use.ItemPath})" : use.ItemPath;

        V1Asset Problem(string code, string message, string? file = null, string? reason = null)
        {
            diagnostics.Add(ImportDiagnostics.Warning, code, at, message, file);
            return asset with { Code = code, Reason = reason };
        }

        var path = V1Path.Classify(use.Path, use.Relative);
        switch (path.Kind)
        {
            case V1PathKind.Absolute:
                return Problem("import.asset.absolute", $"The image of {who} is not relative to the profile's assets ('{use.Path}'): it isn't read, and the missing-image placeholder draws.");
            case V1PathKind.NotAFilePath:
                return Problem("import.asset.path", $"The image of {who} is not a file path ('{use.Path}').");
            case V1PathKind.Escapes:
                return Problem("import.asset.path", $"The image of {who} leaves the profile's assets folder ('{use.Path}').");
        }

        var lookup = source.Find(path.Segments, out var handle, out var file);
        if (lookup == V1AssetLookup.Missing)
            return Problem("import.asset.missing", $"The image of {who} isn't there ('{use.Path}'); the missing-image placeholder draws.", file);
        if (lookup == V1AssetLookup.Link)
            return Problem("import.asset.link", $"The image of {who} is a link ('{use.Path}'): links aren't followed.", file);

        if (budget.Exhausted)
            return Problem("import.asset.media", $"The image of {who} isn't stored: the import already read its limit of bytes.", file, "archive.expanded");

        byte[]? bytes;
        (long Bytes, string Name) cap = (workspace.Limits.MediaBytes, "mediaBytes");
        try
        {
            // A video (1.x played them as images) up to videoBytes, stored like a picture; anything else, told by its
            // first bytes, up to mediaBytes: it is never read on towards videoBytes before being refused.
            bytes = source.Read(handle!, head => (cap = MediaValidator.ByteCap(head, workspace.Limits)).Bytes, MediaValidator.HeadBytes);
        }
        catch (ImportBudgetExceededException)
        {
            return Problem("import.asset.media", $"The image of {who} isn't stored: the import read its limit of bytes.", file, "archive.expanded");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Problem("import.asset.missing", $"The image of {who} can't be read ('{use.Path}': {ex.Message}); the missing-image placeholder draws.", file);
        }

        if (bytes is null)
            return Problem("import.asset.media", $"The image of {who} is larger than {cap.Bytes} bytes ({cap.Name}): it isn't stored.", file, "limit." + cap.Name);

        var check = MediaValidator.Check(bytes, workspace.Limits);
        if (check.Errors.Count > 0)
        {
            var first = check.Errors[0];
            return Problem("import.asset.media", $"The image of {who} isn't stored: {first.Code}: {first.Message}", file, first.Code);
        }

        return asset with { Blob = workspace.PutBlob(bytes).Info };
    }
}
