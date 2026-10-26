using Serilog.Events;

namespace InfoPanel.Hosting;

/// <summary>
/// Routes a log event to one sink: events logged inside <c>logger.BeginScope(LogTargets.Only(...))</c> carry a
/// <see cref="Property"/> that the console and file sinks filter on (see <c>Program.cs</c>). Used to keep the
/// token-bearing sign-in link out of the log file.
/// </summary>
public static class LogTargets
{
    public const string Property = "LogTarget";
    public const string Console = "console";
    public const string File = "file";

    /// <summary>A logging scope state that sends the events inside it to <paramref name="target"/> only.</summary>
    public static IReadOnlyDictionary<string, object> Only(string target) =>
        new Dictionary<string, object> { [Property] = target };

    public static bool Is(LogEvent logEvent, string target) =>
        logEvent.Properties.TryGetValue(Property, out var value)
        && value is ScalarValue { Value: string s }
        && s == target;
}
