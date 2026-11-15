using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InfoPanel.Scenes;

/// <summary>
/// The tick's clock (spec/docs/identity.md): <see cref="Elapsed"/> seconds since rendering started (monotonic), the wall
/// clock <see cref="Now"/>, the engine's local zone and the document's regional format (its profile's <c>locale</c>, else
/// the PC's). Read once per tick; every expression of the tick sees it.
/// </summary>
public sealed record SceneClock(double Elapsed, DateTimeOffset Now, TimeZoneInfo LocalZone, RegionalFormat Format)
{
    public static SceneClock At(double elapsed, DateTimeOffset now) => new(elapsed, now, TimeZoneInfo.Local, PcRegionalFormat.Current);
}

/// <summary>Time fields and <c>formatTime</c> patterns (spec/docs/expressions.md).</summary>
internal static class SceneTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CultureInfo?> Cultures = new(StringComparer.Ordinal);

    /// <summary>An IANA zone, or null when this engine doesn't know it (the caller then uses the local zone and reports it).</summary>
    public static TimeZoneInfo? Zone(string name) => Zones.GetOrAdd(name, static n =>
        TimeZoneInfo.TryFindSystemTimeZoneById(n, out var zone) ? zone : null);

    /// <summary>A BCP 47 locale, or null when it is unknown.</summary>
    public static CultureInfo? Culture(string name) => Cultures.GetOrAdd(name, static n =>
    {
        try
        {
            return CultureInfo.GetCultureInfo(n, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    });

    public static DateTime Local(SceneClock clock, TimeZoneInfo zone) => Local(clock.Now, zone);

    /// <summary>
    /// The date and time of <paramref name="instant"/> in <paramref name="zone"/>; one that would fall outside the years
    /// 0001 to 9999 there is the nearest bound, 0001-01-01 00:00:00 or 9999-12-31 23:59:59.999999
    /// (<see cref="TimeZoneInfo.ConvertTime(DateTimeOffset, TimeZoneInfo)"/> saturates to the last tick, here cut to the
    /// microsecond, as the instant's bound is).
    /// </summary>
    public static DateTime Local(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var t = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return t > MaxLocal ? MaxLocal : t;
    }

    private static readonly DateTime MaxLocal = new(DateTime.MaxValue.Ticks - DateTime.MaxValue.Ticks % 10); // 9999-12-31 23:59:59.999999

    private const long MinMicroseconds = -62_135_596_800_000_000; // 0001-01-01T00:00:00Z
    private const long MaxMicroseconds = 253_402_300_799_999_999; // 9999-12-31T23:59:59.999999Z

    /// <summary>
    /// The instant an <c>at</c> gives (spec/docs/expressions.md, "Another instant"): <paramref name="seconds"/> since
    /// 1970-01-01T00:00:00Z, 0 when not finite, rounded to the nearest microsecond (half away from zero) and clamped to the
    /// years 0001 to 9999. The whole seconds and the fraction are split first (both exact) and the fraction is rounded on
    /// its own: scaling the whole value by a million would round it to a double's step there, about 30 microseconds near
    /// the year 9999.
    /// </summary>
    public static DateTimeOffset Instant(double seconds)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        var whole = Math.Truncate(seconds);
        var fraction = (long)Math.Round((seconds - whole) * 1e6, MidpointRounding.AwayFromZero); // -1,000,000 … 1,000,000
        var wholeSeconds = (long)Math.Clamp(whole, MinMicroseconds / 1_000_000 - 1, MaxMicroseconds / 1_000_000 + 1);
        var clamped = Math.Clamp(wholeSeconds * 1_000_000 + fraction, MinMicroseconds, MaxMicroseconds);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + clamped * 10, TimeSpan.Zero);
    }

    public static double Field(TimeField field, SceneClock clock, TimeZoneInfo zone) =>
        field == TimeField.Elapsed ? clock.Elapsed : Field(field, clock.Now, zone);

    /// <summary>A field of <paramref name="now"/>, the tick's time or an instant; never <see cref="TimeField.Elapsed"/>.</summary>
    public static double Field(TimeField field, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (field == TimeField.Epoch) return (now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
        var t = Local(now, zone);
        return field switch
        {
            TimeField.DayTime => t.TimeOfDay.Ticks / (double)TimeSpan.TicksPerSecond,
            TimeField.Year => t.Year,
            TimeField.Month => t.Month,
            TimeField.Day => t.Day,
            TimeField.Weekday => t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek,
            TimeField.Hour => t.Hour,
            TimeField.Minute => t.Minute,
            TimeField.Second => t.Second,
            TimeField.Millisecond => t.Millisecond,
            _ => 0,
        };
    }

    public static TimeField ParseField(string field) => field switch
    {
        "elapsed" => TimeField.Elapsed,
        "epoch" => TimeField.Epoch,
        "dayTime" => TimeField.DayTime,
        "year" => TimeField.Year,
        "month" => TimeField.Month,
        "day" => TimeField.Day,
        "weekday" => TimeField.Weekday,
        "hour" => TimeField.Hour,
        "minute" => TimeField.Minute,
        "second" => TimeField.Second,
        "millisecond" => TimeField.Millisecond,
        _ => TimeField.Elapsed,
    };

    /// <summary>A pattern as tokens: a letter run (<see cref="PatternToken.Letter"/> and its count) or literal text.</summary>
    public static PatternToken[] ParsePattern(string pattern)
    {
        var tokens = new List<PatternToken>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    literal.Append('\'');
                    i += 2;
                    continue;
                }
                // Quoted text, in which '' is a quote; an unclosed quote (refused by validation) reads to the end.
                i++;
                while (i < pattern.Length)
                {
                    if (pattern[i] == '\'')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                        {
                            literal.Append('\'');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    literal.Append(pattern[i++]);
                }
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                var run = 1;
                while (i + run < pattern.Length && pattern[i + run] == c) run++;
                if (literal.Length > 0)
                {
                    tokens.Add(new PatternToken('\0', 0, literal.ToString()));
                    literal.Clear();
                }
                tokens.Add(new PatternToken(c, run, null));
                i += run;
                continue;
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0) tokens.Add(new PatternToken('\0', 0, literal.ToString()));
        return [.. tokens];
    }

    public static string Format(PatternToken[] tokens, DateTime t, CultureInfo culture)
    {
        var dtf = culture.DateTimeFormat;
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            if (token.Text is { } text)
            {
                sb.Append(text);
                continue;
            }
            var n = token.Count;
            switch (token.Letter)
            {
                case 'y':
                    sb.Append(n == 2 ? (t.Year % 100).ToString("00", CultureInfo.InvariantCulture)
                        : n >= 4 ? t.Year.ToString("0000", CultureInfo.InvariantCulture)
                        : t.Year.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'M':
                    sb.Append(n switch
                    {
                        1 => t.Month.ToString(CultureInfo.InvariantCulture),
                        2 => t.Month.ToString("00", CultureInfo.InvariantCulture),
                        3 => dtf.GetAbbreviatedMonthName(t.Month),
                        _ => dtf.GetMonthName(t.Month),
                    });
                    break;
                case 'd': Pad(sb, t.Day, n); break;
                case 'E': sb.Append(n >= 4 ? dtf.GetDayName(t.DayOfWeek) : dtf.GetAbbreviatedDayName(t.DayOfWeek)); break;
                case 'H': Pad(sb, t.Hour, n); break;
                case 'h': Pad(sb, t.Hour % 12 == 0 ? 12 : t.Hour % 12, n); break;
                case 'm': Pad(sb, t.Minute, n); break;
                case 's': Pad(sb, t.Second, n); break;
                case 'S':
                    sb.Append(n switch
                    {
                        1 => (t.Millisecond / 100).ToString(CultureInfo.InvariantCulture),
                        2 => (t.Millisecond / 10).ToString("00", CultureInfo.InvariantCulture),
                        _ => t.Millisecond.ToString("000", CultureInfo.InvariantCulture),
                    });
                    break;
                case 'a':
                    var marker = t.Hour < 12 ? dtf.AMDesignator : dtf.PMDesignator;
                    sb.Append(marker.Length > 0 ? marker : t.Hour < 12 ? "AM" : "PM");
                    break;
            }
            if (sb.Length > 4 * 4096) break;
        }
        return sb.ToString();
    }

    private static void Pad(StringBuilder sb, int value, int count) =>
        sb.Append(value.ToString(count >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
}

internal enum TimeField : byte { Elapsed, Epoch, DayTime, Year, Month, Day, Weekday, Hour, Minute, Second, Millisecond }

internal sealed record PatternToken(char Letter, int Count, string? Text);
