using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Hosting;

/// <summary>
/// The release lines' codenames (<c>packaging/codenames.txt</c>, embedded; docs/2.0/packaging.md, "Codenames"): the name
/// of a version's release line (major.minor), shown beside the version in the session (<c>engineCodename</c>) and on the
/// menu's and the tray's version lines. The list is read by the rule the release workflow checks it with
/// (<c>.github/scripts/release-version.sh</c>): LF-ended lines, each a <c>#</c> comment, empty, or <c>major.minor=Name</c>;
/// no control character on any line; each key a release line written as a version writes its numbers (<c>2.0</c>, no
/// leading zeros), at most once; each name ASCII letters and digits, words joined by single spaces or hyphens, starting
/// with a letter, at most 32 characters. A list that breaks the rule anywhere names no line.
/// </summary>
public static partial class ReleaseCodename
{
    /// <summary>The embedded list's resource name (InfoPanel/InfoPanel.csproj).</summary>
    public const string Resource = "InfoPanel.Hosting.codenames.txt";

    /// <summary>The most characters a name has.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// The list's entries, the name by release line; a <see cref="FormatException"/> naming the first line the rule
    /// refuses (never repeating its name).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        if (text.Contains('\0'))
            throw new FormatException("The release lines' codenames hold a NUL byte");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        // The LF that ends the last line starts no other.
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++)
        {
            var entry = lines[i];
            var at = $"The release lines' codenames, line {i + 1}";
            if (entry.Any(c => c < ' ' || c == '\u007f'))
                throw new FormatException($"{at} holds a control character (a CRLF line end, a tab): LF-ended lines only");
            if (entry.Length == 0 || entry[0] == '#')
                continue;
            var equals = entry.IndexOf('=');
            if (equals < 0)
                throw new FormatException($"{at} isn't a comment, an empty line or major.minor=Name");
            var key = entry[..equals];
            var name = entry[(equals + 1)..];
            if (!KeyRule().IsMatch(key))
                throw new FormatException($"{at}: the key isn't a release line such as 2.0 (major.minor, no leading zeros)");
            if (!entries.TryAdd(key, name))
                throw new FormatException($"{at}: the release line {key} is listed twice");
            if (name.Length > MaxLength || !NameRule().IsMatch(name))
                throw new FormatException($"{at}: the codename for {key} isn't 1 to {MaxLength} ASCII letters and digits, words joined by single spaces or hyphens, starting with a letter");
        }

        return entries;
    }

    /// <summary>The name of <paramref name="version"/>'s release line in <paramref name="entries"/>, or null: none for that line, or not a SemVer 2 version.</summary>
    public static string? Of(string version, IReadOnlyDictionary<string, string> entries)
    {
        var match = SemVer().Match(version);
        return match.Success && entries.TryGetValue($"{match.Groups[1].Value}.{match.Groups[2].Value}", out var name) ? name : null;
    }

    /// <summary>
    /// The entries of the list <paramref name="open"/> gives. A list that isn't there, can't be read or breaks the rule
    /// gives none (so no version has a name) and one warning, never an exception.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(Func<Stream?> open, Serilog.ILogger logger)
    {
        try
        {
            using var stream = open() ?? throw new FileNotFoundException($"The engine holds no {Resource}");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            // As the workflow reads it, byte by byte: no byte order mark taken off, a stray byte kept (and refused in a name).
            return Parse(new UTF8Encoding(false, false).GetString(bytes.GetBuffer(), 0, (int)bytes.Length));
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "The release lines' codenames couldn't be read: versions show without a codename");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>A version as a version line shows it: <c>2.0.0 “Evan”</c> with its line's name, else the version alone.</summary>
    public static string VersionLine(string version, string? codename) =>
        codename is null ? version : $"{version} “{codename}”";

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRule();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*([ -][A-Za-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();

    /// <summary>SemVer 2, as the release workflow checks a version: numbers without leading zeros, an optional -prerelease and +build.</summary>
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();
}
