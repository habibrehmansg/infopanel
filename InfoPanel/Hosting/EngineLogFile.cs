using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.Persistence;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace InfoPanel.Hosting;

/// <summary>
/// The engine's log file: <c>logs/infopanel-{yyyyMMdd}.log</c> by local date (then <c>_001</c>, <c>_002</c>… once one
/// reaches <see cref="FileSizeLimit"/>), the newest <see cref="RetainedFiles"/> kept, each event flushed as it is written.
/// Every file is opened through <see cref="RegularFile.OpenAppend"/> (docs/2.0/data-folder.md, "Special files and
/// links"): a regular file only, created when nothing is there, never waited on. When the folder or the day's file is
/// something else (a FIFO, a device, a link whose target can't be reached), the file log is off for the run and the
/// engine says so once on the console, which keeps logging; so it is when the day's files are full up to the last
/// sequence a name holds, or past <see cref="RolloverAttempts"/> full files in a row. A file that can't be written for
/// another reason is tried again on the next event, said only once. Thread-safe.
/// </summary>
public sealed partial class EngineLogFile : ILogEventSink, IDisposable
{
    /// <summary>The engine's file format: Serilog's default file template.</summary>
    public const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public const long FileSizeLimit = 100L * 1024 * 1024;

    public const int RetainedFiles = 7;

    /// <summary>How many of the day's files one rollover opens, at most, looking for one under the size limit.</summary>
    public const int RolloverAttempts = 8;

    private const string Prefix = "infopanel-";

    private readonly string _folder;
    private readonly Action<string> _warn;
    private readonly MessageTemplateTextFormatter _formatter = new(Template, CultureInfo.InvariantCulture);
    private readonly Lock _lock = new();
    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateTime _day;
    private int _sequence;
    private bool _off;
    private bool _warnedOff;
    private bool _warnedRetry;

    /// <param name="folder">The logs folder, created (owner-only on Unix) when nothing is there.</param>
    /// <param name="warn">Says something on the console, once: the file log is off, or a write failed.</param>
    public EngineLogFile(string folder, Action<string> warn)
    {
        _folder = folder;
        _warn = warn;
    }

    /// <summary>Whether the file log is off for the run.</summary>
    public bool IsOff
    {
        get
        {
            lock (_lock)
                return _off;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            if (_off)
                return;
            try
            {
                var day = DateTime.Now.Date;
                if (_writer == null || day != _day)
                    Open(day);
                _formatter.Format(logEvent, _writer!);
                _writer!.Flush();
                if (_stream!.Length >= FileSizeLimit)
                    Close(); // the next event opens the day's next file
            }
            catch (Exception ex) when (ex is NotARegularFileException or UnresolvedLinkException or NotAFolderException or RolloverException)
            {
                Close();
                _off = true;
                if (!_warnedOff)
                    _warn($"InfoPanel: the log file can't be written ({ex.Message}); logging goes to the console only for this run.");
                _warnedOff = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Close();
                if (!_warnedRetry)
                    _warn($"InfoPanel: the log file in {_folder} can't be written ({ex.Message}); it is tried again with each event.");
                _warnedRetry = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
            _off = true;
        }
    }

    /// <summary>Opens the day's file to append to: the newest one of the day while it is under the size limit, else the next.</summary>
    private void Open(DateTime day)
    {
        Close();
        switch (RegularFile.StateOf(_folder))
        {
            case PathState.Absent:
                DataFolder.CreatePrivate(_folder);
                break;
            case PathState.Unresolved:
                throw new UnresolvedLinkException(_folder);
            case PathState.Other:
                throw new NotAFolderException($"{_folder} isn't a folder");
        }

        var logs = Logs();
        var sequence = logs.Where(l => l.Day == day).Select(l => l.Sequence).DefaultIfEmpty(0).Max();
        for (var attempt = 1; ; attempt++, sequence++)
        {
            var path = PathOf(day, sequence);
            var stream = RegularFile.OpenAppend(path);
            if (stream.Length < FileSizeLimit)
            {
                _stream = stream;
                break;
            }
            stream.Dispose();
            if (sequence == int.MaxValue)
                throw new RolloverException($"{path} is full and is the day's last file a name can hold");
            if (attempt == RolloverAttempts)
                throw new RolloverException($"{RolloverAttempts} of the day's files in a row are full, up to {path}");
        }

        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _day = day;
        _sequence = sequence;
        Prune(logs, PathOf(day, sequence));
    }

    /// <summary>Deletes all but the newest <see cref="RetainedFiles"/> log files, the one being written counted among them.</summary>
    private void Prune(List<(string Path, DateTime Day, int Sequence)> logs, string current)
    {
        var older = logs.Where(l => l.Path != current).OrderByDescending(l => l.Day).ThenByDescending(l => l.Sequence).Skip(RetainedFiles - 1);
        foreach (var (path, _, _) in older)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again when the next file is opened.
            }
        }
    }

    /// <summary>The log files in the folder, by their names (a listing bounded as every one in the data folder).</summary>
    private List<(string Path, DateTime Day, int Sequence)> Logs()
    {
        var logs = new List<(string, DateTime, int)>();
        foreach (var entry in RegularFile.List(_folder, new ListingBudget(RegularFile.MaxListing, $"Listing {_folder}")))
        {
            var match = LogName().Match(entry.Name);
            if (entry is FileInfo && match.Success
                && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                logs.Add((entry.FullName, day, !match.Groups[2].Success ? 0
                    : int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ? sequence
                    : int.MaxValue)); // more digits than a sequence holds: the last one
        }

        return logs;
    }

    private string PathOf(DateTime day, int sequence) =>
        Path.Combine(_folder, $"{Prefix}{day:yyyyMMdd}{(sequence > 0 ? "_" + sequence.ToString("000", CultureInfo.InvariantCulture) : "")}.log");

    private void Close()
    {
        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _stream = null;
    }

    private sealed class NotAFolderException(string message) : IOException(message);

    /// <summary>No file of the day under the size limit within <see cref="RolloverAttempts"/>, or none left to try.</summary>
    private sealed class RolloverException(string message) : IOException(message);

    [GeneratedRegex("\\Ainfopanel-([0-9]{8})(?:_([0-9]{3,}))?\\.log\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogName();
}
