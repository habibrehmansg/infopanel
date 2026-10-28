using System;
using System.Globalization;
using System.Text;

namespace InfoPanel.Scenes;

/// <summary>
/// A regional format (spec/docs/expressions.md, "Regional format"): the tag it was read as, the culture whose month and
/// day names and AM/PM marker <c>formatTime</c> uses, and the decimal and group separators <c>format</c> uses when a
/// scene gives none. Digits, the minus sign and groups of three never depend on it. A profile's <c>locale</c> is looked
/// up with <see cref="Lookup"/>; the PC's is read once at start-up (<see cref="PcRegionalFormat"/>).
/// </summary>
public sealed class RegionalFormat
{
    /// <summary>The most code points a separator may have, as <c>format</c>'s own separators.</summary>
    public const int MaxSeparatorLength = 4;

    /// <summary>The tag of the invariant format (no language).</summary>
    public const string UndeterminedTag = "und";

    private static readonly BoundedCache<RegionalFormat?> Tags = new(CacheLimit);
    private static readonly BoundedCache<bool> Reported = new(CacheLimit);

    private RegionalFormat(string tag, CultureInfo culture, string decimalSeparator, string groupSeparator)
    {
        Tag = tag;
        // A frozen copy: a culture the caller still holds can't change this format's names afterwards.
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        DecimalSeparator = decimalSeparator;
        GroupSeparator = groupSeparator;
    }

    /// <summary>The most tags <see cref="Lookup"/> remembers, and the most unknown tags <see cref="FirstReport"/> remembers.</summary>
    internal const int CacheLimit = 64;

    internal static int CachedCount => Tags.Count;

    internal static int ReportedCount => Reported.Count;

    /// <summary>The invariant format: <c>.</c> and <c>,</c>, English names, tag <c>und</c>.</summary>
    public static RegionalFormat Invariant { get; } = new(UndeterminedTag, CultureInfo.InvariantCulture, ".", ",");

    /// <summary>Its language and region as a BCP 47 tag (<c>und</c> for the invariant format).</summary>
    public string Tag { get; }

    /// <summary>The culture whose names and AM/PM marker <c>formatTime</c> uses.</summary>
    public CultureInfo Culture { get; }

    public string DecimalSeparator { get; }

    public string GroupSeparator { get; }

    /// <summary>A culture's format, its separators as the culture gives them (user overrides included when it has them).</summary>
    public static RegionalFormat Of(CultureInfo culture) =>
        Create(culture, culture.NumberFormat.NumberDecimalSeparator, culture.NumberFormat.NumberGroupSeparator);

    /// <summary>
    /// <paramref name="culture"/>'s names with the given separators (a custom number format); a separator that is empty or
    /// longer than <see cref="MaxSeparatorLength"/> code points is replaced by the culture's own, and that by the
    /// invariant one when it doesn't fit either.
    /// </summary>
    public static RegionalFormat Create(CultureInfo culture, string? decimalSeparator, string? groupSeparator)
    {
        var own = culture.NumberFormat;
        return new RegionalFormat(TagOf(culture), culture,
            Fits(decimalSeparator) ? decimalSeparator! : Fits(own.NumberDecimalSeparator) ? own.NumberDecimalSeparator : ".",
            Fits(groupSeparator) ? groupSeparator! : Fits(own.NumberGroupSeparator) ? own.NumberGroupSeparator : ",");
    }

    /// <summary>
    /// A BCP 47 tag's format from the platform's locale data, with no user overrides on any platform; null when this
    /// engine doesn't know the tag.
    /// </summary>
    public static RegionalFormat? Lookup(string tag)
    {
        if (Tags.TryGet(tag, out var known)) return known;
        RegionalFormat? format;
        try
        {
            format = Of(CultureInfo.GetCultureInfo(tag, predefinedOnly: true));
        }
        catch (CultureNotFoundException)
        {
            format = null;
        }
        Tags.Add(tag, format);
        return format;
    }

    /// <summary>
    /// True the first time this process asks about <paramref name="tag"/> (an unknown profile <c>locale</c>), so a scene's
    /// tag is logged once however many runtimes load it; the tags remembered are bounded (the least recently asked about
    /// is forgotten, and reported again if it returns).
    /// </summary>
    public static bool FirstReport(string tag) => Reported.TryAddNew(tag, true);

    /// <summary>Whether <paramref name="separator"/> is 1–<see cref="MaxSeparatorLength"/> code points.</summary>
    public static bool Fits(string? separator)
    {
        if (string.IsNullOrEmpty(separator)) return false;
        var n = 0;
        foreach (var _ in separator.EnumerateRunes())
            if (++n > MaxSeparatorLength) return false;
        return true;
    }

    private static string TagOf(CultureInfo culture) => culture.Name.Length == 0 ? UndeterminedTag : culture.Name;

    public override string ToString() => $"{Tag} (decimal '{DecimalSeparator}', group '{GroupSeparator}')";
}
