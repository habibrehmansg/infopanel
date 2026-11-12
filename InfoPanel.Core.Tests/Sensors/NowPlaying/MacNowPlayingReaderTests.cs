using System.IO.Pipes;
using System.Text;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The macOS reader of the Now Playing source over a fake helper process: the helper's stream lines (full payloads,
/// nothing playing, missing and null keys, base64 art and its type, art over the bound left out, malformed lines skipped
/// with one warning, a line over the bound dropped), its process (started on use with absolute paths and Perl's own
/// variables removed, SIGTERM then kill, an exit of 0 started again, any other <c>helperFailed</c> and not started again,
/// no helper <c>helperMissing</c>), app names resolved once per id, the helper's get every 2 s while in use (the readings
/// follow a stream gone quiet, a cover only with its own track, one get at a time, bounded, never waited for), and no
/// title in the log. No real helper runs here.
/// </summary>
public sealed class MacNowPlayingReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-np-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeProcess _process = new();
    private readonly List<LogEvent> _events = [];
    private readonly List<string> _resolved = [];
    private MacNowPlayingReader? _reader;

    public MacNowPlayingReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, MacNowPlayingReader.FrameworkName));
        File.WriteAllText(Path.Combine(_folder, MacNowPlayingReader.ScriptName), "# the script");
        File.WriteAllText(Path.Combine(_folder, MacNowPlayingReader.FrameworkName, MacNowPlayingReader.LibraryName), "library");
    }

    public void Dispose()
    {
        foreach (var run in _process.Runs)
            run.End(0);
        _reader?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeProcess : IMediaHelperProcess
    {
        public readonly List<FakeRun> Runs = [];
        public readonly List<MediaHelperStart> Starts = [];
        public Exception? Throws;

        public bool Broken;

        /// <summary>When set, a get's start blocks until it is set (after setting <see cref="GetEntered"/>).</summary>
        public ManualResetEventSlim? GetGate;

        public readonly ManualResetEventSlim GetEntered = new();

        /// <summary>How many gets' starts began, those still blocked by <see cref="GetGate"/> included.</summary>
        public int GetAttempts;

        public IMediaHelperRun Start(MediaHelperStart start)
        {
            if (Throws is { } e)
                throw e;
            if (start.Arguments.Contains("get"))
                Interlocked.Increment(ref GetAttempts);
            if (GetGate is { } gate && start.Arguments.Contains("get"))
            {
                GetEntered.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            var run = new FakeRun { Broken = Broken };
            // Under a lock: a get starts on a reader's thread.
            lock (Starts)
            {
                Starts.Add(start);
                Runs.Add(run);
            }
            return run;
        }
    }

    /// <summary>
    /// A run as a <see cref="System.Diagnostics.Process"/> is: once disposed, asking for its streams throws.
    /// </summary>
    private sealed class FakeRun : IMediaHelperRun
    {
        private readonly AnonymousPipeServerStream _out = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _err = new(PipeDirection.Out);
        private readonly TaskCompletionSource<int> _exited = new();
        private readonly Stream _output;
        private readonly Stream _error;
        private volatile bool _disposed;

        public FakeRun()
        {
            _output = new EndSignalingStream(new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle), OutputRead);
            _error = new AnonymousPipeClientStream(PipeDirection.In, _err.ClientSafePipeHandle);
        }

        public Stream Output => _disposed || Broken ? throw new InvalidOperationException("The run is disposed") : _output;
        public Stream Error => _disposed || Broken ? throw new InvalidOperationException("The run is disposed") : _error;

        /// <summary>Set once its reader has read its standard output to its end.</summary>
        public readonly ManualResetEventSlim OutputRead = new();

        /// <summary>Its streams throw what a reader doesn't expect.</summary>
        public volatile bool Broken;
        public Task<int> Exited => _exited.Task;
        public bool Disposed => _disposed;

        /// <summary>How many times it was disposed.</summary>
        public int Disposals => Volatile.Read(ref _disposals);

        private int _disposals;
        public int Terminates;
        public int Kills;

        /// <summary>Whether SIGTERM ends it (exit 0), as the helper does.</summary>
        public bool EndsOnTerminate = true;

        public void Print(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _out.Write(bytes);
            _out.Flush();
        }

        public void PrintError(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _err.Write(bytes);
            _err.Flush();
        }

        public void End(int code)
        {
            ClosePipes();
            _exited.TrySetResult(code);
        }

        /// <summary>It exits while what it started still holds its pipes open: its readers read on.</summary>
        public void Exit(int code) => _exited.TrySetResult(code);

        /// <summary>Its standard output's last writer is gone, its standard error still open.</summary>
        public void CloseOutput() => _out.Dispose();

        /// <summary>The pipes' last writer is gone: its readers reach their end.</summary>
        public void ClosePipes()
        {
            try
            {
                _out.Dispose();
                _err.Dispose();
            }
            catch (IOException)
            {
            }
        }

        public void Terminate()
        {
            Terminates++;
            if (EndsOnTerminate)
                End(0);
        }

        /// <summary>Whether a kill ends it at once; when not, it runs on until <see cref="End"/>.</summary>
        public bool EndsOnKill = true;

        public void Kill()
        {
            Kills++;
            if (EndsOnKill)
                End(137);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            _disposed = true;
        }
    }

    /// <summary>A stream that sets <paramref name="ended"/> once a read of it has met its end: its reader read it whole.</summary>
    private sealed class EndSignalingStream(Stream inner, ManualResetEventSlim ended) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Ended(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Ended(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Ended(int read)
        {
            if (read == 0)
                ended.Set();
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private MacNowPlayingReader Reader(params string[] folders) => Reader(_process, folders);

    private MacNowPlayingReader Reader(IMediaHelperProcess process, params string[] folders)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _reader = new MacNowPlayingReader(process, folders.Length == 0 ? [_folder] : folders, id =>
        {
            lock (_resolved)
                _resolved.Add(id);
            return id switch { "com.apple.Music" => "Music", "pid:4242" => "Player 4242", _ => null };
        }, _time, log);
        return _reader;
    }

    /// <summary>Prints <paramref name="line"/> on the latest run and waits until the reader has taken it.</summary>
    private void Print(string line)
    {
        var taken = new TaskCompletionSource();
        _reader!.Parsed = () => taken.TrySetResult();
        _process.Runs[^1].Print(line);
        Assert.True(taken.Task.Wait(TimeSpan.FromSeconds(10)), "the reader took the line");
        _reader.Parsed = null;
    }

    private int Warnings(string contains)
    {
        lock (_events)
            return _events.Count(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(contains, StringComparison.Ordinal));
    }

    private const string Full = """
        {"type":"data","diff":false,"payload":{"bundleIdentifier":"com.apple.Music","parentApplicationBundleIdentifier":null,"playing":true,"title":"A Secret Song","artist":"Secret Band","album":"Secret Album","durationMicros":200000000,"elapsedTimeMicros":12500000,"timestampEpochMicros":1794474000000000,"playbackRate":1,"trackNumber":1,"totalTrackCount":5,"genre":"Secret Genre","composer":"Someone","shuffleMode":3,"repeatMode":2,"artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png","processIdentifier":4242}}
        """;

    private static NowPlayingSnapshot? Parse(string line, out bool parsed, out bool artTooBig)
    {
        parsed = MacNowPlayingReader.TryParse(Encoding.UTF8.GetBytes(line), out var snapshot, out artTooBig);
        return snapshot;
    }

    // ---- The stream's lines ----

    [Fact]
    public void A_full_payload_reads_every_field_the_source_needs()
    {
        var s = Parse(Full, out var parsed, out var artTooBig);

        Assert.True(parsed);
        Assert.False(artTooBig);
        Assert.NotNull(s);
        Assert.Equal("A Secret Song", s.Title);
        Assert.Equal("Secret Band", s.Artist);
        Assert.Equal("Secret Album", s.Album);
        Assert.Equal("com.apple.Music", s.App);
        Assert.Equal(NowPlayingState.Playing, s.State);
        Assert.Equal(200, s.Duration);
        Assert.Equal(12.5, s.Position);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794474000), s.PositionAt);
        Assert.Equal(1, s.Rate);
        Assert.Equal(1, s.TrackNumber);
        Assert.Equal("Secret Genre", s.Genre);
        Assert.True(s.Shuffle);
        Assert.Equal(NowPlayingRepeat.One, s.Repeat);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s.Art!.Value.ToArray());
        Assert.Equal("image/png", s.ArtType);
    }

    [Fact]
    public void The_parent_apps_id_is_the_app_and_paused_is_not_playing()
    {
        var s = Parse("""{"type":"data","diff":false,"payload":{"bundleIdentifier":"com.example.web-helper","parentApplicationBundleIdentifier":"com.example.browser","playing":false,"title":"T","shuffleMode":1,"repeatMode":3}}""", out _, out _);

        Assert.Equal("com.example.browser", s!.App);
        Assert.Equal(NowPlayingState.Paused, s.State);
        Assert.False(s.Shuffle);
        Assert.Equal(NowPlayingRepeat.All, s.Repeat);
    }

    [Fact]
    public void Without_a_bundle_id_the_app_is_its_process()
    {
        var s = Parse("""{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"T"}}""", out _, out _);
        Assert.Equal("pid:4242", s!.App);
        Assert.Null(Parse("""{"type":"data","diff":false,"payload":{"processIdentifier":0,"playing":true,"title":"T"}}""", out _, out _)!.App);
    }

    [Fact]
    public void An_empty_payload_is_nothing_playing()
    {
        Assert.Null(Parse("""{"type":"data","diff":false,"payload":{}}""", out var parsed, out _));
        Assert.True(parsed);
    }

    [Fact]
    public void Missing_and_null_keys_read_as_none()
    {
        var s = Parse("""{"type":"data","diff":false,"payload":{"bundleIdentifier":"com.apple.Music","playing":true,"title":"T","artist":null,"album":"","durationMicros":null,"trackNumber":0,"shuffleMode":0,"repeatMode":null,"artworkData":null}}""", out var parsed, out _);

        Assert.True(parsed);
        Assert.Null(s!.Artist);
        Assert.Null(s.Album);
        Assert.Null(s.Duration);
        Assert.Null(s.Position);
        Assert.Null(s.PositionAt);
        Assert.Null(s.Rate);
        Assert.Null(s.TrackNumber);
        Assert.Null(s.Genre);
        Assert.Null(s.Shuffle);
        Assert.Null(s.Repeat);
        Assert.Null(s.Art);
        Assert.Null(s.ArtType);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"type":"data","diff":false}""")]
    [InlineData("""{"type":"other","diff":false,"payload":{}}""")]
    [InlineData("""{"type":"data","diff":true,"payload":{"title":"T"}}""")]
    [InlineData("""{"type":"data","diff":false,"payload":[]}""")]
    // Text that isn't Unicode: an unpaired high surrogate, and a low one alone.
    [InlineData("""{"type":"data","diff":false,"payload":{"title":"\uD800"}}""")]
    [InlineData("""{"type":"data","diff":false,"payload":{"title":"T","artist":"x\uDC00y"}}""")]
    public void A_line_that_isnt_the_stream_expected_is_not_taken(string line)
    {
        Parse(line, out var parsed, out _);
        Assert.False(parsed);
    }

    [Fact]
    public void A_null_payload_is_nothing_playing()
    {
        Assert.Null(Parse("""{"type":"data","diff":false,"payload":null}""", out var parsed, out _));
        Assert.True(parsed);
    }

    [Fact]
    public void A_session_without_a_title_reads_the_rest_with_the_title_unavailable()
    {
        var s = Parse("""{"type":"data","diff":false,"payload":{"bundleIdentifier":"com.apple.Music","playing":true,"artist":"Secret Band","durationMicros":100000000,"elapsedTimeMicros":5000000}}""", out var parsed, out _);

        Assert.True(parsed);
        Assert.NotNull(s);
        Assert.Null(s.Title);
        Assert.Equal("Secret Band", s.Artist);
        Assert.Equal("com.apple.Music", s.App);
        Assert.Equal(NowPlayingState.Playing, s.State);
        Assert.Equal(5, s.Position);
        Assert.Equal(100, s.Duration);
    }

    [Theory]
    [InlineData(NowPlayingSource.MaxArtBytes - 1)]
    [InlineData(NowPlayingSource.MaxArtBytes)]
    [InlineData(NowPlayingSource.MaxArtBytes + 1)]
    public void Art_is_read_up_to_exactly_8_MiB(int size)
    {
        var base64 = Convert.ToBase64String(new byte[size]);
        var s = Parse("""{"type":"data","diff":false,"payload":{"playing":true,"title":"T","artworkData":""" + "\"" + base64 + "\"" + ""","artworkMimeType":"image/jpeg"}}""", out var parsed, out var artTooBig);

        Assert.True(parsed);
        Assert.Equal(size > NowPlayingSource.MaxArtBytes, artTooBig);
        if (size > NowPlayingSource.MaxArtBytes)
        {
            Assert.Null(s!.Art);
        }
        else
        {
            Assert.Equal(size, s!.Art!.Value.Length);
            Assert.Equal("image/jpeg", s.ArtType);
        }
    }

    [Fact]
    public void Every_file_pins_txt_names_is_in_the_source_tree()
    {
        var folder = Path.Combine(InfoPanel.Core.Tests.Editing.KindManifestTests.RepoRoot(), "native", "mediaremote-adapter");
        var files = File.ReadAllLines(Path.Combine(folder, "pins.txt"))
            .Where(l => l.StartsWith("file=", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf("  ", StringComparison.Ordinal) + 2)..])
            .ToList();

        Assert.Contains("bin/mediaremote-adapter.pl", files);
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(folder, f)), f + " is missing from the source tree"));
    }

    [Fact]
    public void Art_over_8_MiB_is_left_out_and_the_rest_read()
    {
        var big = new string('A', (NowPlayingSource.MaxArtBytes / 3 + 1) * 4);
        var s = Parse("""{"type":"data","diff":false,"payload":{"playing":true,"title":"T","artworkData":""" + "\"" + big + "\"" + ""","artworkMimeType":"image/jpeg"}}""", out var parsed, out var artTooBig);

        Assert.True(parsed);
        Assert.True(artTooBig);
        Assert.Equal("T", s!.Title);
        Assert.Null(s.Art);
        Assert.Null(s.ArtType);
    }

    [Fact]
    public void Lines_are_split_a_line_over_the_bound_dropped_whole_and_the_next_read()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("one\r\n0123456789ABCDEF\ntwo\n\nthree"));
        var lines = new List<string>();
        var tooLong = 0;

        MacNowPlayingReader.ReadLines(input, 10, line => lines.Add(Encoding.UTF8.GetString(line)), () => tooLong++);

        Assert.Equal(["one", "two", "", "three"], lines);
        Assert.Equal(1, tooLong);
    }

    // ---- The helper's process ----

    [Fact]
    public void Nothing_starts_before_Start_and_Start_runs_Apples_Perl_on_the_helper_with_absolute_paths_and_Perls_variables_removed()
    {
        var reader = Reader();
        Assert.Empty(_process.Starts);
        Assert.Null(reader.State);

        reader.Start();

        var start = Assert.Single(_process.Starts);
        Assert.Equal("/usr/bin/perl", start.FileName);
        Assert.Equal([Path.Combine(_folder, "mediaremote-adapter.pl"), Path.Combine(_folder, "MediaRemoteAdapter.framework"), "stream", "--no-diff", "--debounce=200", "--micros", "--allow-missing-title"], start.Arguments);
        Assert.All(start.Arguments.Take(2), a => Assert.True(Path.IsPathFullyQualified(a)));
        Assert.Equal(["PERL5LIB", "PERL5OPT", "PERLLIB", "PERL5DB"], start.RemovedVariables);

        // Started once while it runs.
        reader.Start();
        Assert.Single(_process.Starts);
    }

    [Fact]
    public void The_stream_feeds_the_latest_snapshot_with_the_apps_name_resolved_once_per_id()
    {
        var reader = Reader();
        reader.Start();
        Assert.False(reader.TryRead(out _));

        Print(Full);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("A Secret Song", s!.Title);
        Assert.Equal("Music", s.App);

        Print(Full.Replace("A Secret Song", "Another Secret Song"));
        Print("""{"type":"data","diff":false,"payload":{"bundleIdentifier":"com.example.unknown","playing":true,"title":"T"}}""");
        Assert.True(reader.TryRead(out s));
        Assert.Equal("com.example.unknown", s!.App);

        // Named by its process alone (the helper may not get the bundle id): asked at each line, none when it can't be named.
        Print("""{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"T"}}""");
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Player 4242", s!.App);
        Print("""{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"T2"}}""");
        Print("""{"type":"data","diff":false,"payload":{"processIdentifier":7,"playing":true,"title":"T"}}""");
        Assert.True(reader.TryRead(out s));
        Assert.Null(s!.App);
        Assert.Equal(["com.apple.Music", "com.example.unknown", "pid:4242", "pid:4242", "pid:7"], _resolved);

        Print("""{"type":"data","diff":false,"payload":{}}""");
        Assert.True(reader.TryRead(out s));
        Assert.Null(s);
    }

    [Fact]
    public void Malformed_lines_are_skipped_with_one_warning_and_the_last_snapshot_kept()
    {
        var reader = Reader();
        reader.Start();
        Print(Full);
        Print("{ broken");
        Print("not json either");
        // Text that isn't Unicode fails only when it is read: skipped as the rest, and the reader reads on.
        Print("""{"type":"data","diff":false,"payload":{"playing":true,"title":"\uD800"}}""");
        Print("""{"type":"data","diff":false,"payload":{"playing":true,"title":"T","album":"\uDC00"}}""");

        Assert.True(reader.TryRead(out var s));
        Assert.Equal("A Secret Song", s!.Title);
        Assert.Equal(1, Warnings("wasn't the JSON expected"));

        Print(Full.Replace("A Secret Song", "Another Secret Song"));
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Another Secret Song", s!.Title);
    }

    [Fact]
    public async Task Stop_sends_SIGTERM_and_kills_a_helper_that_hasnt_ended_after_3_s()
    {
        var reader = Reader();
        reader.Start();
        var run = _process.Runs[0];
        run.EndsOnTerminate = false;
        Print(Full);

        var stopped = reader.StopAsync();
        Assert.Equal(1, run.Terminates);
        Assert.Equal(0, run.Kills);
        Assert.False(reader.TryRead(out _));

        _time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Equal(0, run.Kills);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, run.Kills);

        // Stopped on purpose: not started again, not failed.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_process.Starts);
        Assert.Null(reader.State);
    }

    [Fact]
    public async Task A_helper_that_ends_on_SIGTERM_isnt_killed_and_Start_runs_a_new_one()
    {
        var reader = Reader();
        reader.Start();
        await reader.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _process.Runs[0].Terminates);
        Assert.Equal(0, _process.Runs[0].Kills);

        reader.Start();
        Assert.Equal(2, _process.Starts.Count);
    }

    [Fact]
    public void An_exit_of_0_is_started_again_after_2_s_while_wanted()
    {
        var reader = Reader();
        reader.Start();
        Print(Full);

        _process.Runs[0].End(0);
        Assert.False(reader.TryRead(out _));
        Assert.Null(reader.State);
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Single(_process.Starts);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(2, _process.Starts.Count);
    }

    [Fact]
    public void Any_other_exit_is_helperFailed_and_it_isnt_started_again_in_this_run()
    {
        var reader = Reader();
        reader.Start();
        _process.Runs[0].End(1);

        Assert.Equal(SourceStateKind.Unavailable, reader.State!.Kind);
        Assert.Equal("helperFailed", reader.State.Reason!.Code);
        _time.Advance(TimeSpan.FromSeconds(10));
        reader.Stop();
        reader.Start();
        Assert.Single(_process.Starts);
        Assert.Equal(1, Warnings("exit code"));
    }

    [Fact]
    public void A_helper_that_cant_be_started_is_helperFailed()
    {
        _process.Throws = new System.ComponentModel.Win32Exception(2);
        var reader = Reader();
        reader.Start();

        Assert.Equal("helperFailed", reader.State!.Reason!.Code);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Without_the_helpers_files_it_is_helperMissing_and_starts_nothing()
    {
        var empty = Path.Combine(_folder, "empty");
        Directory.CreateDirectory(empty);
        var reader = Reader(empty, "relative/folder");

        Assert.Equal(SourceStateKind.Unavailable, reader.State!.Kind);
        Assert.Equal("helperMissing", reader.State.Reason!.Code);
        reader.Start();
        Assert.Empty(_process.Starts);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void The_helper_is_looked_for_beside_the_engine_then_in_the_variables_folder()
    {
        Assert.Equal([Path.Combine("/engine", "mediaremote-adapter")], MacNowPlayingReader.Candidates("/engine", null));
        Assert.Equal([Path.Combine("/engine", "mediaremote-adapter"), "/helper"], MacNowPlayingReader.Candidates("/engine", "/helper"));

        var beside = Path.Combine(_folder, "missing");
        Assert.Equal((Path.Combine(_folder, "mediaremote-adapter.pl"), Path.Combine(_folder, "MediaRemoteAdapter.framework")), MacNowPlayingReader.Locate([beside, _folder]));
    }

    [Fact]
    public void Its_standard_error_is_never_logged_only_its_byte_count_and_exit_code_and_its_output_never()
    {
        var reader = Reader();
        reader.Start();
        // A helper's error can name what plays: 22 and 11 bytes with their line ends.
        _process.Runs[0].PrintError("Sentinel Secret Title");
        _process.Runs[0].PrintError("Hidden 2nd");
        Print(Full);
        _process.Runs[0].End(3);
        // The error thread reads on its own: wait for its diagnostic.
        SpinWait.SpinUntil(() => Warnings("standard error") > 0, TimeSpan.FromSeconds(10));

        var said = _events.Where(e => e.RenderMessage().Contains("standard error", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(said).RenderMessage();
        Assert.Contains("33 bytes", line, StringComparison.Ordinal);
        Assert.Contains("exit code 3", line, StringComparison.Ordinal);
        foreach (var e in _events)
        {
            Assert.DoesNotContain("Sentinel", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_run_is_disposed_only_once_both_its_readers_have_ended_so_reading_on_after_its_exit_is_quiet()
    {
        var reader = Reader();
        reader.Start();
        var run = _process.Runs[0];
        run.PrintError("Sentinel 1");
        Print(Full);

        // It exits while what it started still holds its pipes open: its readers read on, so it isn't disposed yet.
        run.Exit(3);
        Assert.Equal("helperFailed", reader.State!.Reason!.Code);
        Assert.False(run.Disposed);

        // A read after its exit, then the pipes' end: the readers end, and only then is it disposed.
        run.PrintError("Hidden 2");
        run.ClosePipes();
        Assert.True(SpinWait.SpinUntil(() => run.Disposed, TimeSpan.FromSeconds(10)), "disposed once both readers ended");
        Assert.True(SpinWait.SpinUntil(() => Warnings("standard error") > 0, TimeSpan.FromSeconds(10)));
        var line = Assert.Single(_events, e => e.RenderMessage().Contains("standard error", StringComparison.Ordinal)).RenderMessage();
        Assert.Contains("20 bytes", line, StringComparison.Ordinal);
        Assert.Contains("exit code 3", line, StringComparison.Ordinal);
        Assert.Equal(0, reader.ReaderFailures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void A_line_read_after_the_helpers_exit_is_never_taken_as_a_reading(int code)
    {
        var reader = Reader();
        reader.Start();
        var run = _process.Runs[0];
        Print(Full);

        // It exits while what it started still holds its pipes open; a line still buffered is read after its exit.
        run.Exit(code);
        Assert.False(reader.TryRead(out _));
        Print(Full.Replace("A Secret Song", "A Stale Song"));

        Assert.False(reader.TryRead(out var snapshot));
        Assert.Null(snapshot);
        if (code == 0)
        {
            Assert.Null(reader.State);
            // The restart still comes, with readings of its own only.
            _time.Advance(TimeSpan.FromSeconds(2.1));
            Assert.Equal(2, _process.Starts.Count);
            Assert.False(reader.TryRead(out _));
        }
        else
        {
            Assert.Equal("helperFailed", reader.State!.Reason!.Code);
        }

        run.ClosePipes();
    }

    [Fact]
    public void An_error_a_reader_doesnt_expect_ends_that_reader_with_one_warning_and_never_the_engine()
    {
        _process.Broken = true;
        var reader = Reader();

        reader.Start();

        Assert.True(SpinWait.SpinUntil(() => reader.ReaderFailures == 2, TimeSpan.FromSeconds(10)), "both readers ended on it");
        Assert.True(SpinWait.SpinUntil(() => Warnings("ended on an error (InvalidOperationException)") == 2, TimeSpan.FromSeconds(10)));
        Assert.All(_events, e => Assert.DoesNotContain("The run is disposed", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal));

        // Its exit is handled as ever, and with both readers gone the run is disposed.
        var run = _process.Runs[0];
        run.End(3);
        Assert.Equal("helperFailed", reader.State!.Reason!.Code);
        Assert.True(run.Disposed);
    }

    [Fact]
    public void A_helper_that_writes_to_its_standard_error_and_exits_is_handled_through_the_real_process()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(MacNowPlayingReader.Perl), "Unix with /usr/bin/perl: the media helper's process.");
        var folder = Path.Combine(_folder, "stand-in");
        Directory.CreateDirectory(Path.Combine(folder, MacNowPlayingReader.FrameworkName));
        File.WriteAllText(Path.Combine(folder, MacNowPlayingReader.FrameworkName, MacNowPlayingReader.LibraryName), "library");
        // A stand-in, never the helper: it writes to its standard error and exits at once, leaving a process of its own
        // that holds that pipe a second longer and writes again, so the reader reads on after the exit.
        File.WriteAllText(Path.Combine(folder, MacNowPlayingReader.ScriptName), """
            print STDERR "Sentinel Secret Title\n";
            if (!fork) { sleep 1; print STDERR "Hidden 2nd\n"; exit 0 }
            exit 3;
            """);
        var reader = Reader(new MediaHelperProcess(), folder);

        reader.Start();

        Assert.True(SpinWait.SpinUntil(() => reader.State?.Reason?.Code == "helperFailed", TimeSpan.FromSeconds(10)), "the exit was handled");
        Assert.True(SpinWait.SpinUntil(() => Warnings("standard error") > 0, TimeSpan.FromSeconds(10)), "the error reader reached its end");
        var said = Assert.Single(_events, e => e.RenderMessage().Contains("standard error", StringComparison.Ordinal)).RenderMessage();
        Assert.Contains("33 bytes", said, StringComparison.Ordinal);
        Assert.Contains("exit code 3", said, StringComparison.Ordinal);
        // Nothing reached a reader thread's top (there it would have ended the engine before; now it is counted).
        Assert.Equal(0, reader.ReaderFailures);
        foreach (var e in _events)
        {
            Assert.DoesNotContain("Sentinel", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
        }
    }

    // ---- A cover the stream leaves out: from the helper's get, run every 2 s ----

    /// <summary>A track as the stream gives it without its cover (the helper's stream can, while its get has it).</summary>
    private const string NoArt = """
        {"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"A Secret Song","artist":"Secret Band","album":"Secret Album","uniqueIdentifier":"u-1","contentItemIdentifier":"c-1","durationMicros":200000000,"elapsedTimeMicros":1000000}}
        """;

    /// <summary>The same track as the helper's get gives it: the payload alone, with its cover.</summary>
    private const string GetWithArt = """
        {"processIdentifier":4242,"playing":true,"title":"A Secret Song","artist":"Secret Band","album":"Secret Album","uniqueIdentifier":"u-1","contentItemIdentifier":"c-1","durationMicros":200000000,"elapsedTimeMicros":3000000,"artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png"}
        """;

    /// <summary>That track as the helper's get gives it without its cover.</summary>
    private const string GetNoArt = """
        {"processIdentifier":4242,"playing":true,"title":"A Secret Song","artist":"Secret Band","album":"Secret Album","uniqueIdentifier":"u-1","contentItemIdentifier":"c-1","durationMicros":200000000,"elapsedTimeMicros":3000000}
        """;

    /// <summary>Prints <paramref name="line"/> on <paramref name="run"/>, a stream's, and waits until the reader has taken it.</summary>
    private void PrintOn(FakeRun run, string line)
    {
        var taken = new TaskCompletionSource();
        _reader!.Parsed = () => taken.TrySetResult();
        run.Print(line);
        Assert.True(taken.Task.Wait(TimeSpan.FromSeconds(10)), "the reader took the line");
        _reader.Parsed = null;
    }

    /// <summary>Ends a get's <paramref name="run"/> (exit 0) and waits until the reader has let go of it.</summary>
    private static void Answered(FakeRun run)
    {
        run.End(0);
        Assert.True(SpinWait.SpinUntil(() => run.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the get");
    }

    private int StartCount
    {
        get
        {
            lock (_process.Starts)
                return _process.Starts.Count;
        }
    }

    [Fact]
    public void A_track_the_stream_gives_without_its_cover_takes_the_cover_the_helpers_get_gives_for_that_same_track()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, NoArt);
        Assert.True(reader.TryRead(out var s));
        Assert.Null(s!.Art);

        // 2 s after the line, one get, run as the stream is: Apple's Perl, absolute paths, Perl's variables removed.
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(1, StartCount);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(2, StartCount);
        var get = _process.Starts[1];
        Assert.Equal("/usr/bin/perl", get.FileName);
        Assert.Equal([Path.Combine(_folder, "mediaremote-adapter.pl"), Path.Combine(_folder, "MediaRemoteAdapter.framework"), "get", "--micros", "--allow-missing-title"], get.Arguments);
        Assert.Equal(["PERL5LIB", "PERL5OPT", "PERLLIB", "PERL5DB"], get.RemovedVariables);

        var run = _process.Runs[1];
        run.PrintError("Sentinel Secret Title");
        run.Print(GetWithArt);
        Answered(run);

        // Its cover, with the rest as the get gave it (no stream line came while it ran, so its answer is the reading).
        Assert.True(reader.TryRead(out s));
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s!.Art!.Value.ToArray());
        Assert.Equal("image/png", s.ArtType);
        Assert.Equal("A Secret Song", s.Title);
        Assert.Equal(3, s.Position);

        // The stream's next lines for that track, still without it, keep it.
        PrintOn(stream, NoArt.Replace("1000000", "4000000", StringComparison.Ordinal));
        Assert.True(reader.TryRead(out s));
        Assert.Equal(4, s!.Position);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s.Art!.Value.ToArray());

        // So does the next poll's get when it gives that track without it.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(GetNoArt);
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal(3, s!.Position);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s.Art!.Value.ToArray());

        // Neither what plays nor the get's standard error reaches the log.
        foreach (var e in _events)
        {
            Assert.DoesNotContain("Secret", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("Sentinel", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_gets_cover_goes_only_with_its_own_track_and_a_get_that_answers_nothing_changes_nothing()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, NoArt);

        // The player moved on: the get gives another track with its cover. That track is the reading, with its own
        // cover; the cover never goes to the track the stream gave.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        _process.Runs[1].Print(GetWithArt.Replace("A Secret Song", "Another Secret Song", StringComparison.Ordinal));
        Answered(_process.Runs[1]);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("Another Secret Song", s!.Title);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s.Art!.Value.ToArray());
        PrintOn(stream, NoArt);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("A Secret Song", s!.Title);
        Assert.Null(s.Art);

        // A get that ends without a line, or with one that isn't a payload, changes nothing (it is never "nothing plays").
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        Answered(_process.Runs[2]);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(4, StartCount);
        _process.Runs[3].Print("{ broken");
        Answered(_process.Runs[3]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("A Secret Song", s!.Title);
        Assert.Equal(1, Warnings("wasn't the JSON expected"));

        // Nothing plays, the next one says.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(5, StartCount);
        _process.Runs[4].Print("null");
        Answered(_process.Runs[4]);
        Assert.True(reader.TryRead(out s));
        Assert.Null(s);
    }

    [Fact]
    public async Task A_get_is_killed_5_s_on_and_the_streams_end_or_Stop_kill_the_get_under_way_never_waiting()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, NoArt);

        // A get that doesn't answer is killed 5 s after it started; the next poll runs after it.
        _time.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(2, StartCount);
        var hung = _process.Runs[1];
        _time.Advance(TimeSpan.FromSeconds(4.8));
        Assert.Equal(0, hung.Kills);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, hung.Kills);
        Assert.True(SpinWait.SpinUntil(() => hung.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the killed get");
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, StartCount);
        var second = _process.Runs[2];

        // The stream ends (exit 0): the get under way is killed, and none runs while the stream waits its 2 s to start again.
        stream.End(0);
        Assert.Equal(1, second.Kills);
        // It holds its reservation until its readers have let go of it, on threads of their own: the next run's poll
        // below must find it retired.
        Assert.True(SpinWait.SpinUntil(() => second.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the killed get");
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(3, StartCount);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(4, StartCount);
        Assert.Contains("stream", _process.Starts[3].Arguments);
        stream = _process.Runs[3];
        PrintOn(stream, NoArt);

        // Stop: the get is killed at once, while the stream still has its 3 s to end.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(5, StartCount);
        var third = _process.Runs[4];
        stream.EndsOnTerminate = false;
        var stopped = reader.StopAsync();
        Assert.Equal(1, third.Kills);
        Assert.Equal(0, stream.Kills);
        Assert.False(stopped.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(3.1));
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(5, StartCount);
    }

    [Fact]
    public void Through_the_real_process_a_cover_the_stream_left_out_comes_from_the_helpers_get()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(MacNowPlayingReader.Perl), "Unix with /usr/bin/perl: the media helper's process.");
        var folder = Path.Combine(_folder, "stand-in");
        Directory.CreateDirectory(Path.Combine(folder, MacNowPlayingReader.FrameworkName));
        File.WriteAllText(Path.Combine(folder, MacNowPlayingReader.FrameworkName, MacNowPlayingReader.LibraryName), "library");
        // A stand-in, never the helper: its stream gives a track without its cover and runs on; its get gives the cover.
        File.WriteAllText(Path.Combine(folder, MacNowPlayingReader.ScriptName), """
            $| = 1;
            if ($ARGV[1] eq 'get') {
              print '{"processIdentifier":4242,"playing":true,"title":"Secret Title","artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png"}', "\n";
              exit 0;
            }
            print '{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"Secret Title"}}', "\n";
            sleep 60;
            """);
        var reader = Reader(new MediaHelperProcess(), folder);

        reader.Start();
        Assert.True(SpinWait.SpinUntil(() => reader.TryRead(out var s) && s is not null, TimeSpan.FromSeconds(10)), "the stream's line was taken");
        Assert.True(reader.TryRead(out var first));
        Assert.Null(first!.Art);

        _time.Advance(TimeSpan.FromSeconds(2.1));
        Assert.True(SpinWait.SpinUntil(() => reader.TryRead(out var s) && s?.Art is not null, TimeSpan.FromSeconds(10)), "the get's cover was taken");
        Assert.True(reader.TryRead(out var withArt));
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), withArt!.Art!.Value.ToArray());
        Assert.Equal("Secret Title", withArt.Title);
    }

    [Fact]
    public async Task A_gets_start_that_blocks_never_holds_Stop_and_the_run_it_returns_is_killed_at_once()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, NoArt);
        var gate = new ManualResetEventSlim();
        _process.GetGate = gate;
        Task advancing;
        try
        {
            // The try 2 s after the line: its start blocks, on the timer's thread.
            advancing = Task.Run(() => _time.Advance(TimeSpan.FromSeconds(2.1)));
            Assert.True(_process.GetEntered.Wait(TimeSpan.FromSeconds(10)), "the get's start began");

            // Stop doesn't wait on it: the stream ends at SIGTERM and Stop completes while the start still blocks.
            var stopped = Task.Run(() => reader.StopAsync());
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(5)), "Stop completed while the get's start blocked");
            Assert.Equal(1, stream.Terminates);
        }
        finally
        {
            gate.Set();
        }

        await advancing.WaitAsync(TimeSpan.FromSeconds(10));

        // The run the start returned is killed at once (its track was stopped meanwhile) and let go of; no more tries.
        Assert.Equal(2, StartCount);
        var late = _process.Runs[1];
        Assert.Equal(1, late.Kills);
        Assert.True(SpinWait.SpinUntil(() => late.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the late get");
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, StartCount);
    }

    /// <summary>An untagged clip as the stream gives it: its app's process and its duration alone, without its cover.</summary>
    private static string Clip(long durationMicros) =>
        $$$"""{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"durationMicros":{{{durationMicros}}},"elapsedTimeMicros":1000000}}""";

    /// <summary>An untagged clip as the helper's get gives it, with its cover.</summary>
    private static string ClipGet(long durationMicros) =>
        $$"""{"processIdentifier":4242,"playing":true,"durationMicros":{{durationMicros}},"elapsedTimeMicros":2000000,"artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png"}""";

    [Fact]
    public void A_track_without_enough_identity_keeps_no_cover_and_untagged_clips_of_one_app_keep_their_own_covers()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];

        // No title, no ids, no duration: nothing tells this clip from the app's next one. The get's answer is read with
        // the cover it gives, but that cover isn't kept: the stream's next line of it has none.
        PrintOn(stream, """{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"elapsedTimeMicros":1000000}}""");
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        _process.Runs[1].Print("""{"processIdentifier":4242,"playing":true,"elapsedTimeMicros":2000000,"artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png"}""");
        Answered(_process.Runs[1]);
        Assert.True(reader.TryRead(out var s));
        Assert.NotNull(s!.Art);
        PrintOn(stream, """{"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"elapsedTimeMicros":3000000}}""");
        Assert.True(reader.TryRead(out s));
        Assert.Null(s!.Art);

        // Two untagged clips, told apart by their durations: the get gives the other clip with its cover, and the
        // stream's line of this one doesn't take it.
        PrintOn(stream, Clip(30_000_000));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(ClipGet(45_000_000));
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal(45, s!.Duration);
        Assert.NotNull(s.Art);
        PrintOn(stream, Clip(30_000_000));
        Assert.True(reader.TryRead(out s));
        Assert.Null(s!.Art);

        // Its own cover comes with the next get, and isn't carried over to the next clip.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(4, StartCount);
        _process.Runs[3].Print(ClipGet(30_000_000));
        Answered(_process.Runs[3]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal(30, s!.Duration);
        Assert.NotNull(s.Art);
        PrintOn(stream, Clip(45_000_000));
        Assert.True(reader.TryRead(out s));
        Assert.Null(s!.Art);
    }

    [Fact]
    public void A_cover_the_stream_gives_is_kept_across_its_lines_and_the_gets_answers_of_that_track_without_one()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        var withArt = NoArt.Replace("\"elapsedTimeMicros\":1000000", "\"elapsedTimeMicros\":1000000,\"artworkData\":\"iVBORw0KGgo=\",\"artworkMimeType\":\"image/png\"", StringComparison.Ordinal);
        PrintOn(stream, NoArt);
        _time.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(2, StartCount);

        // The stream gives the cover while a get runs: the get isn't killed, and its answer, older than that line, isn't
        // the reading.
        PrintOn(stream, withArt);
        Assert.Equal(0, _process.Runs[1].Kills);
        _process.Runs[1].Print(GetNoArt);
        Answered(_process.Runs[1]);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal(1, s!.Position);

        // The stream drops it and gives it again, over and over, and the polls' gets give that track without it: the
        // cover stays.
        for (var i = 0; i < 3; i++)
        {
            PrintOn(stream, NoArt);
            Assert.True(reader.TryRead(out s));
            Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s!.Art!.Value.ToArray());
            Assert.Equal("image/png", s.ArtType);
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(3 + i, StartCount);
            _process.Runs[^1].Print(GetNoArt);
            Answered(_process.Runs[^1]);
            Assert.True(reader.TryRead(out s));
            Assert.Equal(3, s!.Position);
            Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), s.Art!.Value.ToArray());
            PrintOn(stream, withArt);
        }
    }

    // ---- The helper's get every 2 s while in use: the readings follow a stream that goes quiet ----

    /// <summary>The next track as the helper's get gives it: another title, artist, album, id, state, position and cover.</summary>
    private const string GetNext = """
        {"bundleIdentifier":"com.apple.Music","playing":false,"title":"Next Secret Song","artist":"Next Secret Band","album":"Next Secret Album","uniqueIdentifier":"u-2","durationMicros":180000000,"elapsedTimeMicros":7000000,"timestampEpochMicros":1794474100000000,"playbackRate":0,"artworkData":"R0lGODdh","artworkMimeType":"image/gif"}
        """;

    /// <summary>The track <see cref="Full"/> is, as the helper's get gives it, with its cover.</summary>
    private const string GetFull = """
        {"bundleIdentifier":"com.apple.Music","parentApplicationBundleIdentifier":null,"playing":true,"title":"A Secret Song","artist":"Secret Band","album":"Secret Album","durationMicros":200000000,"elapsedTimeMicros":14500000,"timestampEpochMicros":1794474002000000,"playbackRate":1,"trackNumber":1,"totalTrackCount":5,"genre":"Secret Genre","composer":"Someone","shuffleMode":3,"repeatMode":2,"artworkData":"iVBORw0KGgo=","artworkMimeType":"image/png","processIdentifier":4242}
        """;

    /// <summary>Another track as the stream gives it, without its cover.</summary>
    private const string StreamOther = """
        {"type":"data","diff":false,"payload":{"processIdentifier":4242,"playing":true,"title":"Other Secret Song","artist":"Other Secret Band","album":"Other Secret Album","uniqueIdentifier":"u-3","durationMicros":150000000,"elapsedTimeMicros":2000000}}
        """;

    /// <summary>The track <see cref="StreamOther"/> is, as the helper's get gives it, still without its cover.</summary>
    private const string GetOther = """
        {"processIdentifier":4242,"playing":true,"title":"Other Secret Song","artist":"Other Secret Band","album":"Other Secret Album","uniqueIdentifier":"u-3","durationMicros":150000000,"elapsedTimeMicros":4000000}
        """;

    [Fact]
    public void When_the_stream_goes_quiet_the_helpers_get_2_s_on_gives_the_next_track_with_its_cover()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, Full);

        // The stream says nothing more, as it does on this macOS. The poll 2 s after the start: one get, run as the stream is.
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(1, StartCount);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, StartCount);
        var get = _process.Starts[1];
        Assert.Equal("/usr/bin/perl", get.FileName);
        Assert.Equal([Path.Combine(_folder, "mediaremote-adapter.pl"), Path.Combine(_folder, "MediaRemoteAdapter.framework"), "get", "--micros", "--allow-missing-title"], get.Arguments);
        Assert.Equal(["PERL5LIB", "PERL5OPT", "PERLLIB", "PERL5DB"], get.RemovedVariables);

        // It answers the next track: every reading is that track's, its cover too.
        _process.Runs[1].PrintError("Sentinel Secret Title");
        _process.Runs[1].Print(GetNext);
        Answered(_process.Runs[1]);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("Next Secret Song", s!.Title);
        Assert.Equal("Next Secret Band", s.Artist);
        Assert.Equal("Next Secret Album", s.Album);
        Assert.Equal("Music", s.App);
        Assert.Equal(NowPlayingState.Paused, s.State);
        Assert.Equal(7, s.Position);
        Assert.Equal(180, s.Duration);
        Assert.Equal(Convert.FromBase64String("R0lGODdh"), s.Art!.Value.ToArray());
        Assert.Equal("image/gif", s.ArtType);

        // The player quits, and the stream still says nothing: the next poll's get says nothing plays.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print("null");
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Null(s);

        foreach (var e in _events)
        {
            Assert.DoesNotContain("Secret", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("Sentinel", e.RenderMessage() + " " + e.Exception, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_get_that_hangs_is_killed_5_s_on_none_overlaps_it_and_the_next_poll_still_runs()
    {
        var reader = Reader();
        reader.Start();
        PrintOn(_process.Runs[0], Full);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        var hung = _process.Runs[1];

        // One at a time: the polls 4 and 6 s on start nothing while it runs; it is killed 5 s after its start.
        _time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(2, StartCount);
        Assert.Equal(0, hung.Kills);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(1, hung.Kills);
        Assert.True(SpinWait.SpinUntil(() => hung.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the killed get");
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("A Secret Song", s!.Title);

        // The next poll runs, and its answer is taken.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(GetNext);
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Next Secret Song", s!.Title);
    }

    [Fact]
    public void Gets_run_only_while_in_use_and_Stop_with_one_under_way_returns_at_once_and_kills_it()
    {
        var reader = Reader();
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, StartCount);

        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, Full);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        var get = _process.Runs[1];
        // A get that doesn't end even when killed.
        get.EndsOnKill = false;

        var stopped = reader.StopAsync();
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(1)), "Stop returned while the get still ran");
        Assert.Equal(1, get.Kills);
        Assert.False(get.Exited.IsCompleted);
        Assert.Equal(1, stream.Terminates);

        // Not in use: no more gets.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, StartCount);
        get.End(137);
        Assert.True(SpinWait.SpinUntil(() => get.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the get");
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void A_cover_never_crosses_tracks_and_a_stream_line_taken_while_a_get_runs_wins_over_its_answer()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, Full);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);

        // While the get runs, the stream gives another track without its cover: the get's answer, the track before
        // with its cover, is older than that line and isn't taken.
        PrintOn(stream, StreamOther);
        _process.Runs[1].Print(GetFull);
        Answered(_process.Runs[1]);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("Other Secret Song", s!.Title);
        Assert.Null(s.Art);

        // The next poll gives that track, still without its cover: the cover before isn't carried over.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(GetOther);
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Other Secret Song", s!.Title);
        Assert.Equal(4, s.Position);
        Assert.Null(s.Art);

        // The next gives the next track with its own cover; a late stream line of the track before doesn't take it.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(4, StartCount);
        _process.Runs[3].Print(GetNext);
        Answered(_process.Runs[3]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal(Convert.FromBase64String("R0lGODdh"), s!.Art!.Value.ToArray());
        PrintOn(stream, StreamOther);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Other Secret Song", s!.Title);
        Assert.Null(s.Art);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_get_killed_at_its_limit_never_publishes_what_it_wrote_before_it_hung(bool aTrack)
    {
        var reader = Reader();
        reader.Start();
        PrintOn(_process.Runs[0], Full);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        var hung = _process.Runs[1];

        // It writes a whole answer (another track, or nothing playing), then hangs. Killed 5 s after its start, it ends
        // with 0: only its limit tells its answer apart.
        hung.EndsOnKill = false;
        hung.Print(aTrack ? GetNext : "null");
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hung.Kills);
        hung.End(0);
        Assert.True(SpinWait.SpinUntil(() => hung.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the killed get");
        Assert.True(reader.TryRead(out var s));
        Assert.NotNull(s);
        Assert.Equal("A Secret Song", s.Title);

        // The next poll runs, and its answer is taken.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(GetNext);
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Next Secret Song", s!.Title);
    }

    /// <summary>How a get says "nothing plays" while it failed.</summary>
    public enum FailedNull
    {
        /// <summary>The helper's own timeout (its MediaRemote callbacks over 2 s): a line on its standard error, <c>null</c>, exit 0.</summary>
        HelperTimeout,

        /// <summary>The same, its standard error written after its output has ended.</summary>
        ErrorAfterOutput,

        /// <summary><c>null</c> with no standard error and an exit other than 0.</summary>
        FailedExit,
    }

    [Theory]
    [InlineData(FailedNull.HelperTimeout)]
    [InlineData(FailedNull.ErrorAfterOutput)]
    [InlineData(FailedNull.FailedExit)]
    public void A_get_that_says_nothing_plays_with_an_error_or_a_failed_exit_changes_nothing(FailedNull how)
    {
        var reader = Reader();
        reader.Start();
        PrintOn(_process.Runs[0], Full);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        var get = _process.Runs[1];
        switch (how)
        {
            case FailedNull.HelperTimeout:
                get.PrintError("Reading now playing information timed out after 2000 milliseconds");
                get.Print("null");
                get.End(0);
                break;
            case FailedNull.ErrorAfterOutput:
                get.Print("null");
                get.CloseOutput();
                Assert.True(get.OutputRead.Wait(TimeSpan.FromSeconds(10)), "the reader read its output to its end first");
                get.PrintError("Reading now playing information timed out after 2000 milliseconds");
                get.End(0);
                break;
            default:
                get.Print("null");
                get.End(1);
                break;
        }

        Assert.True(SpinWait.SpinUntil(() => get.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the get");
        Assert.True(reader.TryRead(out var s));
        Assert.NotNull(s);
        Assert.Equal("A Secret Song", s.Title);

        // A get that says so with no error and exit 0 is still "nothing plays".
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print("null");
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Null(s);
    }

    [Fact]
    public void A_get_whose_standard_error_a_child_holds_open_is_let_go_of_3_s_on_and_the_next_poll_runs()
    {
        var reader = Reader();
        reader.Start();
        PrintOn(_process.Runs[0], Full);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, StartCount);
        var held = _process.Runs[1];

        // It says nothing plays, its output ends and it exits with 0, while what it started holds its standard error
        // open for good; a kill doesn't end that pipe either.
        held.EndsOnKill = false;
        var timers = _time.TimerCount;
        held.Print("null");
        held.CloseOutput();
        Assert.True(held.OutputRead.Wait(TimeSpan.FromSeconds(10)), "the reader read its output to its end");
        held.Exit(0);

        // Its pipes' deadline (3 s on) is installed once it has exited with its output ended; the clock moves only to it,
        // so no poll (6 s) runs before the reader has let go of the get.
        Assert.True(SpinWait.SpinUntil(() => _time.TimerCount == timers + 1, TimeSpan.FromSeconds(10)), "the pipes' deadline was installed");
        _time.Advance(TimeSpan.FromSeconds(3));

        // At that deadline it is let go of, once, and its answer, with its standard error unfinished, isn't taken.
        Assert.True(SpinWait.SpinUntil(() => held.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the held get");
        Assert.Equal(2, StartCount);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("A Secret Song", s!.Title);

        // The next poll (6 s) starts a new get, and its answer is taken.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, StartCount);
        _process.Runs[2].Print(GetNext);
        Answered(_process.Runs[2]);
        Assert.True(reader.TryRead(out s));
        Assert.Equal("Next Secret Song", s!.Title);

        // Its standard error ending at last retires nothing again.
        held.ClosePipes();
        Assert.Equal(1, held.Disposals);
        Assert.Equal(0, reader.ReaderFailures);
    }

    [Fact]
    public async Task A_gets_start_that_blocks_across_the_streams_restart_keeps_the_next_poll_from_starting_another()
    {
        var reader = Reader();
        reader.Start();
        var stream = _process.Runs[0];
        PrintOn(stream, Full);
        var gate = new ManualResetEventSlim();
        _process.GetGate = gate;
        Task advancing;
        try
        {
            // The poll 2 s on: its get's start blocks, on the timer's thread (which then runs on to 6.1 s).
            advancing = Task.Run(() => _time.Advance(TimeSpan.FromSeconds(6.1)));
            Assert.True(_process.GetEntered.Wait(TimeSpan.FromSeconds(10)), "the get's start began");
            // Only that start blocks.
            _process.GetGate = null;

            // The stream ends (exit 0) and starts again 2 s on; the new run's poll 2 s after that starts no get while the
            // first one's start is still under way.
            stream.End(0);
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(2, StartCount);
            Assert.Contains("stream", _process.Starts[1].Arguments);
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref _process.GetAttempts));
            Assert.Equal(2, StartCount);
        }
        finally
        {
            gate.Set();
        }

        await advancing.WaitAsync(TimeSpan.FromSeconds(10));

        // The run the first start returns is killed at once (its stream run has ended) and let go of; the next poll
        // starts the next get, whose answer is the reading.
        Assert.Equal(3, StartCount);
        var late = _process.Runs[2];
        Assert.Equal(1, late.Kills);
        Assert.True(SpinWait.SpinUntil(() => late.Disposed, TimeSpan.FromSeconds(10)), "the reader let go of the late get");
        Assert.Equal(1, Volatile.Read(ref _process.GetAttempts));
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(2, Volatile.Read(ref _process.GetAttempts));
        Assert.Equal(4, StartCount);
        _process.Runs[3].Print(GetNext);
        Answered(_process.Runs[3]);
        Assert.True(reader.TryRead(out var s));
        Assert.Equal("Next Secret Song", s!.Title);
    }
}
