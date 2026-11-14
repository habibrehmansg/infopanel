using System.Globalization;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>One limit window: how much of it is used (0–100) and when it resets.</summary>
internal sealed record Window(double UsedPercent, DateTimeOffset ResetsAt);

/// <summary>
/// What a reader found of one set of limits (Codex's, a Claude folder's): each window when it was seen, when they were seen (<see cref="AsOf"/>),
/// and a status, <see cref="Ok"/> or what's wrong in plain words; <see cref="Short"/> is the caption when there is no
/// window to show.
/// </summary>
internal sealed record LimitsReading(Window? FiveHour, Window? Weekly, DateTimeOffset? AsOf, string Status, string Short)
{
    public const string Ok = "ok";

    /// <summary>No limits, for <paramref name="status"/>; the caption <paramref name="caption"/>.</summary>
    public static LimitsReading None(string status, string caption) => new(null, null, null, status, caption);
}

/// <summary>The entries of one set of limits (Codex, Claude Code, one Claude folder), and how a reading shows in them.</summary>
internal class LimitEntries
{
    /// <summary>Past this age a set's caption says "as of …" instead of its resets.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    public PluginSensor FiveHourUsed { get; } = new("five-hour-used", "Five-hour used", float.NaN, "%");
    public PluginSensor WeeklyUsed { get; } = new("weekly-used", "Weekly used", float.NaN, "%");
    public PluginSensor FiveHourResetIn { get; } = new("five-hour-reset-in", "Five-hour resets in", float.NaN, "min");
    public PluginSensor WeeklyResetIn { get; } = new("weekly-reset-in", "Weekly resets in", float.NaN, "min");
    public PluginText Caption { get; } = new("caption", "Caption", "off");
    public PluginSensor AsOfAge { get; } = new("as-of-age", "Age", float.NaN, "min");
    public PluginText Status { get; } = new("status", "Status", "off");

    public virtual IEnumerable<IPluginData> Entries => [FiveHourUsed, WeeklyUsed, FiveHourResetIn, WeeklyResetIn, Caption, AsOfAge, Status];

    /// <summary>
    /// Shows <paramref name="reading"/> as of <paramref name="now"/>. A window keeps its last value with its age; once its
    /// reset has passed it reads 0 (no use seen since), and its reset has no value until it is seen again.
    /// </summary>
    public void Show(LimitsReading reading, DateTimeOffset now, TimeZoneInfo zone)
    {
        FiveHourUsed.Value = Used(reading.FiveHour, now);
        WeeklyUsed.Value = Used(reading.Weekly, now);
        FiveHourResetIn.Value = ResetIn(reading.FiveHour, now);
        WeeklyResetIn.Value = ResetIn(reading.Weekly, now);
        AsOfAge.Value = reading.AsOf is { } asOf ? (float)Math.Floor(Math.Max(0, (now - asOf).TotalMinutes)) : float.NaN;
        Caption.Value = CaptionOf(reading, now, zone);
        Status.Value = reading.Status;
    }

    /// <summary>
    /// The caption: "resets 14:30 · wk Thu 09:00" while the reading is fresh; "as of Mon 02:11" once older than
    /// <see cref="Stale"/> (or when every window it has has reset since); the reading's short status when it has no window.
    /// </summary>
    public static string CaptionOf(LimitsReading reading, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (reading.AsOf is not { } asOf || (reading.FiveHour == null && reading.Weekly == null))
            return reading.Short;
        var parts = new List<string>();
        if (now - asOf <= Stale)
        {
            if (reading.FiveHour is { } fiveHour && fiveHour.ResetsAt > now)
                parts.Add("resets " + Local(fiveHour.ResetsAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture));
            if (reading.Weekly is { } weekly && weekly.ResetsAt > now)
                parts.Add((parts.Count == 0 ? "wk resets " : "wk ") + Local(weekly.ResetsAt, zone).ToString("ddd HH:mm", CultureInfo.InvariantCulture));
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "as of " + Local(asOf, zone).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Local(DateTimeOffset time, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(time, zone);

    private static float Used(Window? window, DateTimeOffset now) =>
        window == null ? float.NaN : window.ResetsAt <= now ? 0 : (float)window.UsedPercent;

    private static float ResetIn(Window? window, DateTimeOffset now) =>
        window == null || window.ResetsAt <= now ? float.NaN : (float)Math.Ceiling((window.ResetsAt - now).TotalMinutes);
}

/// <summary>Codex's entries: its limits, its credits and plan, and its tokens.</summary>
internal sealed class CodexEntries : LimitEntries
{
    public PluginText Credits { get; } = new("credits", "Credits", "");
    public PluginText Plan { get; } = new("plan", "Plan", "");
    public PluginSensor TokensToday { get; } = new("tokens-today", "Tokens today", float.NaN, "tokens");
    public PluginSensor Tokens7d { get; } = new("tokens-7d", "Tokens 7 days", float.NaN, "tokens");

    public override IEnumerable<IPluginData> Entries => [.. base.Entries, Credits, Plan, TokensToday, Tokens7d];

    public void Show(CodexReading reading, DateTimeOffset now, TimeZoneInfo zone)
    {
        Show(reading.Limits, now, zone);
        Credits.Value = reading.Credits ?? "";
        Plan.Value = reading.Plan ?? "";
        TokensToday.Value = reading.TokensToday is { } today ? today : float.NaN;
        Tokens7d.Value = reading.Tokens7d is { } week ? week : float.NaN;
    }
}

/// <summary>Claude Code's combined entries: the limits of the folder whose status line wrote last, and which folder that is.</summary>
internal sealed class ClaudeEntries : LimitEntries
{
    public PluginText Profile { get; } = new("profile", "Profile", "");

    public override IEnumerable<IPluginData> Entries => [.. base.Entries, Profile];
}
