using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace InfoPanel.Hosting;

/// <summary>One of the engine's recent log entries: its text is for people to read, never a contract.</summary>
/// <param name="Seq">1 and up within the run, one more for each entry.</param>
/// <param name="Level"><c>verbose</c>, <c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c> or <c>fatal</c>.</param>
/// <param name="Source">The logging class (<c>SourceContext</c>), cut to <see cref="RecentLog.MaxSourceLength"/>, or empty.</param>
/// <param name="Message">The rendered message, cut to <see cref="RecentLog.MaxMessageLength"/>.</param>
/// <param name="Exception">The exception's text, cut to <see cref="RecentLog.MaxExceptionLength"/>, or null.</param>
public sealed record RecentLogEntry(long Seq, DateTimeOffset Time, string Level, string Source, string Message, string? Exception);

/// <summary>
/// The engine's recent log entries, in memory, for clients to show (1.4.x's Logs page reads this run's last 2,000 lines):
/// a sink in the log file's branch of the engine's logger, so it gets exactly what the file gets (at the level setting)
/// and never what goes to the console only (the sign-in link with its token). It keeps the newest
/// <see cref="MaxEntries"/> entries and at most <see cref="MaxBytes"/> of text, dropping the oldest first. Thread-safe.
/// </summary>
public sealed class RecentLog : ILogEventSink
{
    public const int MaxEntries = 2000;

    /// <summary>The most text kept: the UTF-8 bytes of every entry's source, message and exception.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public const int MaxMessageLength = 4096;

    public const int MaxExceptionLength = 8192;

    /// <summary>
    /// The longest source kept. With the message and exception cut too, every entry stays far under <see cref="MaxBytes"/>,
    /// so the budget holds whatever an entry carries.
    /// </summary>
    public const int MaxSourceLength = 256;

    private const string Cut = "…";

    private static readonly MessageTemplateTextFormatter MessageFormat = new("{Message:lj}", CultureInfo.InvariantCulture);

    private readonly Lock _lock = new();
    private readonly Queue<(RecentLogEntry Entry, int Bytes)> _entries = new();
    private long _seq;
    private long _bytes;

    /// <summary>The newest entry's <see cref="RecentLogEntry.Seq"/>, or 0 before the first.</summary>
    public long Latest
    {
        get
        {
            lock (_lock)
                return _seq;
        }
    }

    /// <summary>The kept entries newer than <paramref name="after"/>, oldest first.</summary>
    public IReadOnlyList<RecentLogEntry> After(long after) => Page(after).Entries;

    /// <summary>
    /// The kept entries newer than <paramref name="after"/>, oldest first, with <see cref="Latest"/> read at the same
    /// moment, so the newest entry answered is never past it.
    /// </summary>
    public (IReadOnlyList<RecentLogEntry> Entries, long Latest) Page(long after)
    {
        lock (_lock)
            return (_entries.Where(e => e.Entry.Seq > after).Select(e => e.Entry).ToList(), _seq);
    }

    public void Emit(LogEvent logEvent)
    {
        using var message = new StringWriter(CultureInfo.InvariantCulture);
        MessageFormat.Format(logEvent, message);
        var text = Clip(message.ToString(), MaxMessageLength);
        var exception = logEvent.Exception is { } ex ? Clip(ex.ToString(), MaxExceptionLength) : null;
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context }
            ? Clip(context, MaxSourceLength)
            : "";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text) + System.Text.Encoding.UTF8.GetByteCount(source)
            + (exception is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(exception));

        lock (_lock)
        {
            var entry = new RecentLogEntry(++_seq, logEvent.Timestamp, LevelName(logEvent.Level), source, text, exception);
            _entries.Enqueue((entry, bytes));
            _bytes += bytes;
            while (_entries.Count > MaxEntries || (_bytes > MaxBytes && _entries.Count > 1))
                _bytes -= _entries.Dequeue().Bytes;
        }
    }

    /// <summary>A level's name in the entries.</summary>
    public static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "verbose",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "information",
        LogEventLevel.Warning => "warning",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "fatal",
        _ => "information",
    };

    /// <summary><paramref name="text"/> cut to at most <paramref name="length"/> characters, ending in "…" when cut, never between the halves of a pair.</summary>
    private static string Clip(string text, int length)
    {
        if (text.Length <= length)
            return text;
        var keep = length - Cut.Length;
        if (char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep] + Cut;
    }
}
