using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Serilog;

namespace InfoPanel.Updates;

/// <summary>
/// <c>updates.json</c> in the data folder (docs/2.0/data-folder.md): <c>{"format":"infopanel.updates/1","notified":"2.0.0-beta.2"}</c>,
/// the highest version (by SemVer 2 precedence) the engine has attempted a system notification for, so each version
/// notifies at most once, ever, not at every start, and no lower one after it. Read once when opened; written (through
/// <see cref="AtomicFile"/>) only by <see cref="Remember"/>, before a notification is shown (a write that fails shows
/// nothing for that version), or when Check for Updates… answers in its prompt. A file it can't read (not a
/// regular file, over <see cref="MaxBytes"/>, not JSON, text that isn't valid Unicode, another format, no SemVer 2
/// version) is logged and treated as none: the next notification is shown and the file replaced. Members it
/// doesn't know are ignored and not kept. Thread-safe.
/// </summary>
public sealed class UpdateNotices
{
    public const string Format = "infopanel.updates/1";

    /// <summary>The most the file may hold (it holds one version).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// The longest version kept, written and read: a longer one is never notified (<see cref="ShouldNotify"/>), refused by
    /// <see cref="Remember"/>, and a file holding one is a bad file (the schema's versions are at most 64 characters).
    /// </summary>
    public const int MaxVersionLength = 256;

    private readonly Lock _lock = new();
    private readonly DataPaths _paths;
    private readonly ILogger _logger;
    private string? _notified;

    private UpdateNotices(DataPaths paths, ILogger logger, string? notified)
    {
        _paths = paths;
        _logger = logger;
        _notified = notified;
    }

    /// <summary>The highest version notified, or null.</summary>
    public string? Notified
    {
        get
        {
            lock (_lock)
                return _notified;
        }
    }

    /// <summary>Reads <paramref name="paths"/>' <c>updates.json</c>. Never throws and never writes.</summary>
    public static UpdateNotices Open(DataPaths paths, ILogger? logger = null)
    {
        logger ??= Log.ForContext<UpdateNotices>();
        return new UpdateNotices(paths, logger, Read(paths.UpdatesFile, logger));
    }

    /// <summary>
    /// Whether <paramref name="version"/> is above the highest version notified so far, by SemVer 2 precedence
    /// (<see cref="ReleaseFeed.Compare"/>), or nothing was notified yet: a high-water mark, so a lower version (a newer
    /// release withdrawn) isn't notified again. Never for a string that isn't a version, or one longer than
    /// <see cref="MaxVersionLength"/>. Writes nothing.
    /// </summary>
    public bool ShouldNotify(string version)
    {
        if (!Keepable(version))
            return false;
        lock (_lock)
            return _notified is null || ReleaseFeed.Compare(version, _notified) > 0;
    }

    /// <summary>
    /// Records <paramref name="version"/> (a SemVer 2 version, at most <see cref="MaxVersionLength"/> characters) as
    /// notified: call it before the notification is shown, and show it only when this answers true, so a version is
    /// notified at most once, ever (a crash between the two loses that notice; the menu's update line still says it). The
    /// highest version is kept: one not above it changes nothing and answers false. A write that fails is logged and
    /// answers false: the version is still kept for this run, so it isn't notified, or tried again, before the next start.
    /// </summary>
    public bool Remember(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (!Keepable(version))
            throw new ArgumentException($"Not a SemVer 2 version of at most {MaxVersionLength} characters.", nameof(version));
        lock (_lock)
        {
            if (_notified is not null && ReleaseFeed.Compare(version, _notified) <= 0)
                return false;
            _notified = version;
            try
            {
                AtomicFile.Write(_paths.UpdatesFile, Serialize(version), _paths.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "{File} couldn't be written: InfoPanel {Version} isn't notified, and may be at the next start", DataPaths.UpdatesFileName, version);
                return false;
            }

            return true;
        }
    }

    /// <summary>A SemVer 2 version this file can hold: at most <see cref="MaxVersionLength"/> characters.</summary>
    private static bool Keepable(string version) => version.Length <= MaxVersionLength && ReleaseFeed.IsVersion(version);

    private static byte[] Serialize(string version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteString("notified", version);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string? Read(string path, ILogger logger)
    {
        byte[] bytes;
        try
        {
            bytes = RegularFile.ReadAtMost(path, MaxBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return Bad(path, $"it is larger than {MaxBytes} bytes", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Bad(path, ex.Message, logger);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Bad(path, "it isn't a JSON object", logger);
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                return Bad(path, $"its format isn't {Format}", logger);
            if (!root.TryGetProperty("notified", out var notified) || notified.ValueKind != JsonValueKind.String
                || notified.GetString() is not { Length: > 0 and <= MaxVersionLength } version || !ReleaseFeed.IsVersion(version))
                return Bad(path, "it names no version", logger);
            return version;
        }
        catch (JsonException)
        {
            return Bad(path, "it isn't JSON", logger);
        }
        catch (InvalidOperationException)
        {
            // JSON that parses but holds a string .NET can't (a lone surrogate, "\uD800"): GetString throws this.
            return Bad(path, "it holds text that isn't valid Unicode", logger);
        }
    }

    private static string? Bad(string path, string why, ILogger logger)
    {
        logger.Warning("{Path} can't be read ({Why}): treated as no update notified yet", path, why);
        return null;
    }
}
