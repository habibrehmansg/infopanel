using System.Diagnostics;
using InfoPanel.Sensors.NowPlaying;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The media helper's real process over a stand-in (<c>perl -e</c>, never the helper): it ends when the engine does,
/// however the engine ends. The engine's death closes the helper's standard input, its lifetime pipe, with no signal
/// sent; an orderly stop sends SIGTERM; a kill ends the helper with what it started.
/// </summary>
public sealed class MediaHelperProcessTests
{
    private const string Perl = "/usr/bin/perl";
    private const string Skip = "Unix with /usr/bin/perl: the media helper's process.";

    /// <summary>A stand-in that prints its process id, then waits (or does <paramref name="then"/>).</summary>
    private static MediaHelperStart StandIn(string then = "sleep 600") => new(Perl, ["-e", "$| = 1; print \"$$\\n\"; " + then], []);

    private static int ReadPid(IMediaHelperRun run)
    {
        using var reader = new StreamReader(run.Output, leaveOpen: true);
        var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        return int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void EndIfAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [Fact]
    public void Closing_its_standard_input_as_the_engines_death_does_ends_the_helper()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Perl), Skip);
        var run = new MediaHelperProcess().Start(StandIn());
        var pid = ReadPid(run);
        try
        {
            Assert.True(Alive(pid));

            // No signal: only the pipe the engine held closes, as when the engine is killed.
            run.Dispose();

            Assert.True(SpinWait.SpinUntil(() => !Alive(pid), TimeSpan.FromSeconds(10)), "the helper ended when its lifetime pipe closed");
        }
        finally
        {
            EndIfAlive(pid);
        }
    }

    [Fact]
    public async Task SIGTERM_ends_the_helper_and_what_it_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Perl), Skip);
        using var run = new MediaHelperProcess().Start(StandIn());
        var pid = ReadPid(run);
        try
        {
            run.Terminate();

            await run.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => !Alive(pid), TimeSpan.FromSeconds(10)));
        }
        finally
        {
            EndIfAlive(pid);
        }
    }

    [Fact]
    public async Task A_kill_ends_the_helper_and_what_it_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Perl), Skip);
        using var run = new MediaHelperProcess().Start(StandIn());
        var pid = ReadPid(run);
        try
        {
            run.Kill();

            await run.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => !Alive(pid), TimeSpan.FromSeconds(10)));
        }
        finally
        {
            EndIfAlive(pid);
        }
    }

    /// <summary>Reads <paramref name="stream"/> to its end; its end is quiet (EOF, or the stream closed under it), anything else is returned.</summary>
    private static Exception? ReadToEnd(Stream stream, ManualResetEventSlim reading)
    {
        try
        {
            var buffer = new byte[4096];
            reading.Set();
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }

            return null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [Fact]
    public async Task Disposing_a_run_mid_read_ends_its_reads_quietly_and_its_streams_stay_those_taken_at_its_start()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Perl), Skip);
        var run = new MediaHelperProcess().Start(StandIn());
        var pid = ReadPid(run);
        try
        {
            var output = run.Output;
            var error = run.Error;
            using var outputReading = new ManualResetEventSlim();
            using var errorReading = new ManualResetEventSlim();
            var outputRead = Task.Factory.StartNew(() => ReadToEnd(output, outputReading), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var errorRead = Task.Factory.StartNew(() => ReadToEnd(error, errorReading), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(outputReading.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(errorReading.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            // Disposed while both are in a read (or about to start one): neither read may end any other way than quietly.
            run.Dispose();

            Assert.Same(output, run.Output);
            Assert.Same(error, run.Error);
            await Task.WhenAll(outputRead, errorRead).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(await outputRead);
            Assert.Null(await errorRead);
            Assert.True(SpinWait.SpinUntil(() => !Alive(pid), TimeSpan.FromSeconds(10)), "the helper ended with its run");
        }
        finally
        {
            EndIfAlive(pid);
        }
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 3", 3)]
    public async Task The_helpers_own_exit_code_comes_through(string then, int code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists(Perl), Skip);
        using var run = new MediaHelperProcess().Start(StandIn(then));
        ReadPid(run);

        Assert.Equal(code, await run.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
