using System.Text;
using InfoPanel.Persistence;

namespace InfoPanel.Plugins;

/// <summary>
/// What a package's <c>PluginInfo.ini</c> says of it, read by the engine when it finds the package (never through the
/// loader's ini parser): the <c>[PluginInfo]</c> section's <c>Name</c> (else the folder's name), <c>Description</c>,
/// <c>Author</c>, <c>Version</c>, <c>Website</c> (only an <c>http</c> or <c>https</c> URL) and <c>Platforms</c>, the
/// operating systems it runs on, in lower case (empty: anywhere); and its <c>Access</c> lines (<see cref="Access"/>).
/// </summary>
public sealed record PluginPackageInfo(string Name, string? Version, string? Author, string? Description, string? Website, IReadOnlyList<string> Platforms)
{
    public const string FileName = "PluginInfo.ini";

    /// <summary>The largest <c>PluginInfo.ini</c> read (64 KiB).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The most <see cref="Access"/> lines kept; the rest are left out.</summary>
    public const int MaxAccessLines = 8;

    /// <summary>
    /// What the package says its plugins read or change, one line each, in the order written: its section's <c>Access</c>
    /// keys, each line cut to <see cref="PluginLimits.NameChars"/>, empty ones left out, at most
    /// <see cref="MaxAccessLines"/>. Declared, not enforced: a client shows them with the package.
    /// </summary>
    public IReadOnlyList<string> Access { get; init; } = [];

    private const string Section = "PluginInfo";

    /// <summary>A package without a <c>PluginInfo.ini</c> it can read: its folder's name, and nothing else.</summary>
    public static PluginPackageInfo Default(string folder) => new(folder, null, null, null, null, []);

    /// <summary>Whether it runs on <paramref name="platform"/> (<see cref="PluginPlatforms"/>): its <c>Platforms</c> names it, or names none.</summary>
    public bool RunsOn(string platform) => Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.Ordinal);

    // A record's equality would compare the list by reference.
    public bool Equals(PluginPackageInfo? other) =>
        other is not null && Name == other.Name && Version == other.Version && Author == other.Author && Description == other.Description
        && Website == other.Website && Platforms.SequenceEqual(other.Platforms) && Access.SequenceEqual(other.Access);

    public override int GetHashCode() => HashCode.Combine(Name, Version, Author, Description, Website, Platforms.Count, Access.Count);

    /// <summary>
    /// Reads <c>{directory}/PluginInfo.ini</c>: a regular file, not a link, of at most <see cref="MaxBytes"/>, UTF-8 with
    /// or without a BOM; values cut to <see cref="PluginLimits.NameChars"/> (the description to
    /// <see cref="PluginLimits.TextChars"/>). Missing or unreadable: <see cref="Default"/>, with one log line. Never throws.
    /// </summary>
    public static PluginPackageInfo Read(string directory, string folder, PluginLimits limits, ILogger logger)
    {
        var path = Path.Combine(directory, FileName);
        byte[] bytes;
        try
        {
            switch (RegularFile.StateOf(path))
            {
                case PathState.Absent:
                    logger.LogInformation("The plugin {Folder} has no {File}: its folder is its name", folder, FileName);
                    return Default(folder);
                case PathState.Other when new FileInfo(path).LinkTarget == null:
                    bytes = RegularFile.ReadAtMost(path, MaxBytes);
                    break;
                default:
                    logger.LogWarning("The plugin {Folder}'s {File} isn't read: it is a link or isn't a regular file", folder, FileName);
                    return Default(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning("The plugin {Folder}'s {File} can't be read: {Error}", folder, FileName, ex.Message);
            return Default(folder);
        }

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        if (text.StartsWith('﻿'))
            text = text[1..];

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var access = new List<string>();
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;
            if (line[0] == '[')
            {
                inSection = line.EndsWith(']') && string.Equals(line[1..^1].Trim(), Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (!inSection || equals <= 0)
                continue;
            var (key, value) = (line[..equals].Trim(), line[(equals + 1)..].Trim());
            // Access is the one key written once a line.
            if (string.Equals(key, "Access", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0 && access.Count < MaxAccessLines)
                    access.Add(value.Length > limits.NameChars ? value[..limits.NameChars] : value);
                continue;
            }
            values.TryAdd(key, value);
        }

        string? Value(string key, int chars) =>
            values.TryGetValue(key, out var value) && value.Length > 0 ? (value.Length > chars ? value[..chars] : value) : null;

        var platforms = (Value("Platforms", limits.NameChars) ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new PluginPackageInfo(
            Value("Name", limits.NameChars) ?? folder,
            Value("Version", limits.NameChars),
            Value("Author", limits.NameChars),
            Value("Description", limits.TextChars),
            WebsiteOf(values.GetValueOrDefault("Website"), limits.NameChars),
            platforms)
        {
            Access = access,
        };
    }

    /// <summary>The website when it is an absolute <c>http</c> or <c>https</c> URL with a host, within <paramref name="chars"/>; else null (never cut into another address).</summary>
    private static string? WebsiteOf(string? value, int chars) =>
        value is { Length: > 0 } && value.Length <= chars
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? value
            : null;
}
