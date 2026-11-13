using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.CrashReporting;
using InfoPanel.Persistence;
using InfoPanel.Settings;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace InfoPanel.Tests.CrashReports;

/// <summary>
/// The crash reports' tests share the SDK, which is one per process: they run alone, after the parallel tests, so no
/// other test engine's log reaches them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrashReportsCollection
{
    public const string Name = "CrashReports";
}

/// <summary>Every envelope the SDK sends, kept as its text, in place of the network.</summary>
public sealed class CapturingTransport : ITransport
{
    private readonly List<string> _envelopes = [];
    private int _failed;

    /// <summary>While true, every send fails as the network does (an <see cref="IOException"/>): the SDK keeps the envelope on disk to send later.</summary>
    public volatile bool Failing;

    /// <summary>How many sends failed.</summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>
    /// While true, a send reaches the network (it is kept, as sent) and then never ends, whatever its cancellation says,
    /// until <see cref="Release"/>: a connection that hangs.
    /// </summary>
    public volatile bool Hanging;

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends every hanging send, and every later one at once.</summary>
    public void Release() => _released.TrySetResult();

    public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (Failing)
        {
            Interlocked.Increment(ref _failed);
            throw new IOException("The network is down (a test).");
        }

        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null, cancellationToken);
        lock (_envelopes)
            _envelopes.Add(Encoding.UTF8.GetString(stream.ToArray()));
        if (Hanging)
            await _released.Task;
    }

    public IReadOnlyList<string> Envelopes
    {
        get
        {
            lock (_envelopes)
                return [.. _envelopes];
        }
    }

    /// <summary>The items of every envelope as JSON, with their item header's type.</summary>
    public IReadOnlyList<(string Type, JsonNode Item)> Items()
    {
        var items = new List<(string, JsonNode)>();
        foreach (var envelope in Envelopes)
        {
            var lines = envelope.Split('\n');
            for (var i = 1; i + 1 < lines.Length; i += 2)
            {
                if (lines[i].Length == 0)
                    break;
                var header = JsonNode.Parse(lines[i])!;
                items.Add((header["type"]!.GetValue<string>(), JsonNode.Parse(lines[i + 1])!));
            }
        }

        return items;
    }

    public IReadOnlyList<JsonNode> Events() => [.. Items().Where(i => i.Type == "event").Select(i => i.Item)];

    /// <summary>Everything sent, raw and with every JSON string unescaped, so a value is found however it was written.</summary>
    public string AllText()
    {
        var text = new StringBuilder();
        foreach (var envelope in Envelopes)
        {
            text.AppendLine(envelope);
            foreach (var line in envelope.Split('\n'))
            {
                try
                {
                    Strings(JsonNode.Parse(line), text);
                }
                catch (JsonException)
                {
                }
            }
        }

        return text.ToString();
    }

    private static void Strings(JsonNode? node, StringBuilder into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    into.AppendLine(key);
                    Strings(value, into);
                }

                break;
            case JsonArray a:
                foreach (var value in a)
                    Strings(value, into);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                into.AppendLine(s);
                break;
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds of what was sent, or 10 s pass.</summary>
    public async Task<bool> Until(Func<CapturingTransport, bool> done)
    {
        for (var waited = 0; waited < 10_000; waited += 50)
        {
            if (done(this))
                return true;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return done(this);
    }
}

/// <summary>The network as always there: the SDK never pings the crash-report address's host to find out.</summary>
public sealed class AlwaysOnline : INetworkStatusListener
{
    public bool Online => true;

    public Task WaitForNetworkOnlineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A data folder of its own, its settings, and crash reports started on it with a capturing transport.</summary>
public sealed class CrashReportsKit : IDisposable
{
    /// <summary>A crash-report address of the right shape on a name that can't resolve; the transport never sends anywhere.</summary>
    public const string Dsn = "https://0123456789abcdef0123456789abcdef@o1.ingest.example.invalid/1";

    public CrashReportsKit()
    {
        Root = Directory.CreateTempSubdirectory("infopanel-crash-").FullName;
        Paths = new DataPaths(Root);
        Settings = SettingsStore.Open(Paths);
    }

    public string Root { get; }

    public DataPaths Paths { get; }

    public SettingsStore Settings { get; }

    public CapturingTransport Transport { get; } = new();

    public TemplateBreadcrumbSink Sink { get; } = new();

    public CrashReporting.CrashReports? Reports { get; private set; }

    public CrashReporting.CrashReports? Start(string? dsn = Dsn, IReadOnlyList<KnownValue>? known = null, Func<IEnumerable<KnownValue>>? names = null, TimeSpan? stopDeadline = null) =>
        Reports = CrashReporting.CrashReports.Start(new CrashReportsOptions
        {
            Dsn = dsn,
            Paths = Paths,
            Release = "infopanel-engine@2.0.0-beta.1+abc1234",
            Environment = "beta",
            Fixed = known ?? [],
            KnownValues = names,
            Transport = Transport,
            NetworkStatus = new AlwaysOnline(),
            StopDeadline = stopDeadline ?? CrashReportsOptions.DefaultStopDeadline,
        }, Settings, Sink);

    /// <summary>The reports' folder in the data folder (<see cref="CrashReporting.CrashReports.FolderName"/>).</summary>
    public string ReportsFolder => Path.Combine(Root, CrashReporting.CrashReports.FolderName);

    /// <summary>
    /// Every folder the SDK keeps a queue in under the reports' folder, whichever start made it: each <c>Sentry/{hash}</c>
    /// (its <c>*.envelope</c>, <c>__processing/</c>, <c>.session</c> and <c>.installation</c>).
    /// </summary>
    public IReadOnlyList<string> SdkFolders() =>
        !Directory.Exists(ReportsFolder)
            ? []
            : [.. Directory.EnumerateDirectories(ReportsFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 })
                .Where(d => Path.GetFileName(Path.GetDirectoryName(d)) == "Sentry")];

    /// <summary>Every SDK folder and its <c>__processing</c>, as they are now.</summary>
    public IReadOnlyList<string> QueueFolders() =>
        [.. SdkFolders().SelectMany(f => new[] { f, Path.Combine(f, "__processing") }).Where(Directory.Exists)];

    /// <summary>The reports the SDK keeps on disk to send, in any of its folders: queued, or taken out to send and not sent.</summary>
    public IReadOnlyList<string> Queued() => [.. QueueFolders().SelectMany(d => Directory.EnumerateFiles(d, "*.envelope"))];

    /// <summary>Every session the SDK keeps on disk (<c>.session</c>), in any of its folders.</summary>
    public IReadOnlyList<string> SessionFiles() => [.. SdkFolders().Select(f => Path.Combine(f, ".session")).Where(File.Exists)];

    private const UnixFileMode Writable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Gives <paramref name="folders"/> the mode <paramref name="mode"/> until the answer is disposed, then makes them the
    /// owner's to read, write and list again.
    /// </summary>
    public static IDisposable Mode(IReadOnlyList<string> folders, UnixFileMode mode)
    {
        foreach (var folder in folders)
            File.SetUnixFileMode(folder, mode);
        return new Restore(folders);
    }

    private sealed class Restore(IReadOnlyList<string> folders) : IDisposable
    {
        public void Dispose()
        {
            foreach (var folder in folders.Reverse())
                File.SetUnixFileMode(folder, Writable);
        }
    }

    /// <summary>Makes every SDK folder and its <c>__processing</c> read-only until disposed: nothing in them can be deleted.</summary>
    public IDisposable ReadOnlyQueue() => Mode(QueueFolders(), UnixFileMode.UserRead | UnixFileMode.UserExecute);

    public void Set(bool? crashReports) =>
        Settings.Apply(JsonDocument.Parse("""{"telemetry":{"crashReports":""" + (crashReports is { } on ? (on ? "true" : "false") : "null") + "}}").RootElement);

    public Serilog.ILogger Logger(string source) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Sink).CreateLogger().ForContext(Serilog.Core.Constants.SourceContextPropertyName, source);

    public void Flush() => Reports?.Flush(TimeSpan.FromSeconds(5));

    public void Dispose()
    {
        Transport.Release();
        Reports?.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
