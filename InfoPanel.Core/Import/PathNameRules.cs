using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InfoPanel.Import;

/// <summary>
/// How a filesystem compares names, for comparing paths as it would: whether letter case is ignored, and whether
/// Unicode-equivalent spellings (a composed <c>é</c> and <c>e</c> with a combining accent) are one name.
/// </summary>
internal readonly record struct PathNameRules(bool IgnoreCase, bool UnicodeEquivalent)
{
    /// <summary>The rules of this platform's default filesystem (<see cref="For"/>).</summary>
    public static PathNameRules Platform { get; } =
        For(OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux);

    /// <summary>
    /// The rules of <paramref name="platform"/>'s default filesystem: Windows' (NTFS) and macOS's (APFS, HFS+) ignore
    /// letter case, Linux's (and any other) compare it exactly; on macOS Unicode-equivalent spellings are one name,
    /// compared here in normalisation form C.
    /// </summary>
    internal static PathNameRules For(OSPlatform platform) =>
        new(IgnoreCase: platform == OSPlatform.Windows || platform == OSPlatform.OSX, UnicodeEquivalent: platform == OSPlatform.OSX);

    /// <summary>Whether the full path <paramref name="child"/> is <paramref name="parent"/> or inside it.</summary>
    public bool IsInside(string child, string parent)
    {
        var c = Key(child);
        var p = Key(parent);
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (c.Equals(p, comparison))
            return true;

        // "/assets/abc" is not inside "/assets/ab"; a root ("/", "C:\") already ends with its separator.
        var prefix = Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, comparison);
    }

    private string Key(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!UnicodeEquivalent)
            return full;
        try
        {
            return full.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return full; // not valid Unicode (a lone surrogate): no filesystem name has it, so compare it as it is
        }
    }
}
