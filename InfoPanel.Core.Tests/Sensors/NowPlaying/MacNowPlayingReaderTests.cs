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
/// no helper <c>helperMissing</c>), app names resolved once per id, and no title in the log. No real helper runs here.
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

        public IMediaHelperRun Start(MediaHelperStart start)
        {
            if (Throws is { } e)
                throw e;
            Starts.Add(start);
            var run = new FakeRun { Broken = Broken };
            Runs.Add(run);
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
            _output = new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle);
            _error = new AnonymousPipeClientStream(PipeDirection.In, _err.ClientSafePipeHandle);
        }

        public Stream Output => _disposed || Broken ? throw new InvalidOperationException("The run is disposed") : _output;
        public Stream Error => _disposed || Broken ? throw new InvalidOperationException("The run is disposed") : _error;

        /// <summary>Its streams throw what a reader doesn't expect.</summary>
        public volatile bool Broken;
        public Task<int> Exited => _exited.Task;
        public bool Disposed => _disposed;
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

        public void Kill()
        {
            Kills++;
            End(137);
        }

        public void Dispose() => _disposed = true;
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
}
