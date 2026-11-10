using System;
using System.Collections.Generic;
using System.Text.Json;

namespace InfoPanel.Updates;

/// <summary>A release as the list gives it: its tag, and whether it is a draft or marked a pre-release on GitHub.</summary>
public readonly record struct FeedRelease(string Tag, bool Draft, bool Prerelease);

/// <summary>A newer version this build is offered, and its release page (built from the tag, <see cref="ReleaseFeed.ReleaseUrl"/>).</summary>
public sealed record UpdateOffer(string Version, string Url);

/// <summary>
/// The engine's public releases as GitHub lists them (docs/2.0/desktop-shell.md, "Updates"), read and chosen from, with no
/// I/O: <see cref="Parse"/> reads each release's <c>tag_name</c>, <c>draft</c> and <c>prerelease</c> and nothing else;
/// <see cref="Choose"/> offers the highest version above this build's that this build's channel takes. The channel is
/// told by the build's version, never by GitHub's flag: a pre-release build (a version with <c>-</c>) takes pre-releases
/// and releases, a release build only versions without <c>-</c> (so a <c>2.0.0</c> user is offered <c>2.0.1</c> while
/// GitHub still marks it a pre-release). Drafts and tags that aren't SemVer 2 versions are skipped; 1.x's versions sort
/// below every 2.x, so a 2.x build is never offered one. The release page is built from the tag, never taken from the list.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>The longest list read, in bytes; a longer one is refused.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Where a release's page is: the engine's public repository's releases, by tag.</summary>
    public const string ReleasePages = "https://github.com/habibrehmansg/infopanel/releases/tag/";

    /// <summary>
    /// The releases in <paramref name="body"/>, a JSON list of objects, in its order: entries that aren't objects or have no
    /// string <c>tag_name</c> that reads as text (a lone surrogate doesn't) are skipped, a missing flag is false. Null when
    /// the body is over <see cref="MaxBytes"/>, isn't JSON, isn't a list, or has anything but whitespace after the list.
    /// </summary>
    public static IReadOnlyList<FeedRelease>? Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBytes)
            return null;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 64 });
            using var document = JsonDocument.ParseValue(ref reader);
            // Only whitespace after the list: anything else is read as a token (true) or refused (JsonException).
            if (reader.Read() || document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var releases = new List<FeedRelease>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                    continue;
                string text;
                try
                {
                    text = tag.GetString()!;
                }
                catch (InvalidOperationException)
                {
                    // Escapes that aren't text (a lone surrogate): skipped, as a tag that isn't SemVer 2 is.
                    continue;
                }

                releases.Add(new FeedRelease(text, Flag(entry, "draft"), Flag(entry, "prerelease")));
            }

            return releases;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Flag(JsonElement entry, string name) => entry.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The highest release above <paramref name="current"/> that its channel takes, with its page; null when there is
    /// none, or <paramref name="current"/> isn't a SemVer 2 version.
    /// </summary>
    public static UpdateOffer? Choose(string current, IEnumerable<FeedRelease> releases)
    {
        if (!SemVer.TryParse(current, out var mine))
            return null;
        SemVer? best = null;
        string? bestTag = null;
        foreach (var release in releases)
        {
            if (release.Draft || !SemVer.TryParse(release.Tag, out var version))
                continue;
            if (!mine.IsPrerelease && version.IsPrerelease)
                continue;
            if (SemVer.Compare(version, mine) <= 0 || (best is { } b && SemVer.Compare(version, b) <= 0))
                continue;
            best = version;
            bestTag = release.Tag;
        }

        return bestTag is null ? null : new UpdateOffer(bestTag, ReleaseUrl(bestTag));
    }

    /// <summary>The release page of <paramref name="tag"/>, built here (a <c>+</c> of build metadata escaped).</summary>
    public static string ReleaseUrl(string tag) => ReleasePages + Uri.EscapeDataString(tag);

    /// <summary>
    /// A build from source or a development build (<c>2.0.0-dev</c>, <c>2.0.0-dev.ci.12</c>): a SemVer 2 version whose
    /// pre-release starts with the identifier <c>dev</c>. Such a build never checks for updates.
    /// </summary>
    public static bool IsDevelopmentBuild(string version) =>
        SemVer.TryParse(version, out var parsed) && parsed.Prerelease.Length > 0 && parsed.Prerelease[0] == "dev";

    /// <summary>Whether <paramref name="version"/> is a SemVer 2 version.</summary>
    public static bool IsVersion(string version) => SemVer.TryParse(version, out _);

    /// <summary>
    /// SemVer 2 §11's precedence of two versions (negative: <paramref name="a"/> is lower); build metadata takes no part.
    /// Both must be versions (<see cref="IsVersion"/>).
    /// </summary>
    public static int Compare(string a, string b)
    {
        if (!SemVer.TryParse(a, out var x) || !SemVer.TryParse(b, out var y))
            throw new ArgumentException("Not a SemVer 2 version.");
        return SemVer.Compare(x, y);
    }

    /// <summary>A SemVer 2 version: its three numbers and its pre-release identifiers (build metadata read, then dropped).</summary>
    private readonly record struct SemVer(ulong Major, ulong Minor, ulong Patch, string[] Prerelease)
    {
        public bool IsPrerelease => Prerelease.Length > 0;

        public static bool TryParse(string text, out SemVer version)
        {
            version = default;
            var plus = text.IndexOf('+');
            var core = plus >= 0 ? text[..plus] : text;
            if (plus >= 0 && !Identifiers(text[(plus + 1)..], numericRule: false, out _))
                return false;
            var dash = core.IndexOf('-');
            var numbers = (dash >= 0 ? core[..dash] : core).Split('.');
            string[] prerelease = [];
            if (dash >= 0 && !Identifiers(core[(dash + 1)..], numericRule: true, out prerelease))
                return false;
            if (numbers.Length != 3 || !Number(numbers[0], out var major) || !Number(numbers[1], out var minor) || !Number(numbers[2], out var patch))
                return false;
            version = new SemVer(major, minor, patch, prerelease);
            return true;
        }

        /// <summary>Dot-separated, non-empty identifiers of <c>[0-9A-Za-z-]</c>; numeric ones without leading zeros when <paramref name="numericRule"/>.</summary>
        private static bool Identifiers(string text, bool numericRule, out string[] identifiers)
        {
            identifiers = text.Split('.');
            foreach (var id in identifiers)
            {
                if (id.Length == 0)
                    return false;
                foreach (var c in id)
                {
                    if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                        return false;
                }

                if (numericRule && IsNumeric(id) && id.Length > 1 && id[0] == '0')
                    return false;
            }

            return true;
        }

        private static bool Number(string text, out ulong value)
        {
            value = 0;
            return text.Length > 0 && IsNumeric(text) && (text.Length == 1 || text[0] != '0') && ulong.TryParse(text, out value);
        }

        private static bool IsNumeric(string text)
        {
            foreach (var c in text)
            {
                if (!char.IsAsciiDigit(c))
                    return false;
            }

            return true;
        }

        public static int Compare(SemVer a, SemVer b)
        {
            var c = a.Major.CompareTo(b.Major);
            if (c == 0)
                c = a.Minor.CompareTo(b.Minor);
            if (c == 0)
                c = a.Patch.CompareTo(b.Patch);
            if (c != 0)
                return c;
            // A pre-release is below its release; then identifier by identifier, numbers as numbers and below words.
            if (a.Prerelease.Length == 0 || b.Prerelease.Length == 0)
                return (a.Prerelease.Length == 0, b.Prerelease.Length == 0) switch { (true, false) => 1, (false, true) => -1, _ => 0 };
            for (var i = 0; i < Math.Min(a.Prerelease.Length, b.Prerelease.Length); i++)
            {
                var x = a.Prerelease[i];
                var y = b.Prerelease[i];
                var xn = IsNumeric(x);
                var yn = IsNumeric(y);
                if (xn && yn)
                {
                    c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                }
                else if (xn != yn)
                {
                    c = xn ? -1 : 1;
                }
                else
                {
                    c = string.CompareOrdinal(x, y);
                }

                if (c != 0)
                    return Math.Sign(c);
            }

            return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
        }
    }
}
