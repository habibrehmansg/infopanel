using System.Diagnostics;
using System.IO.Pipes;
using InfoPanel.Plugins;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace InfoPanel.Tests.Plugins;

public class PluginProcessTests
{
    // A host that exits on its own while a process it started still holds its stderr: the host's reads of its output
    // never reach the end. Disposing the host must still let go of its pipes, not keep them (and the host) until that
    // process exits.
    [Fact]
    public async Task A_disposed_host_lets_go_of_its_pipes_while_a_process_it_started_still_holds_them()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: the test's host is /bin/sh, which leaves a background process holding its stderr.");

        const int hosts = 10;
        var pids = new List<int>();
        var handles = new List<WeakReference<SafePipeHandle>>();
        var fdsBefore = OpenDescriptors();
        try
        {
            for (var i = 0; i < hosts; i++)
            {
                var start = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("-c");
                // The process it starts outlives the test's assertions; it is killed below.
                start.ArgumentList.Add("sleep 600 >/dev/null & echo $! 1>&2; exit 0");
                var process = Process.Start(start)!;
                Track(handles, process);
                var host = new PluginProcessLauncher.HostProcess(process);
                try
                {
                    await host.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                    pids.Add(int.Parse(host.ErrorTail.Trim(), System.Globalization.CultureInfo.InvariantCulture));
                }
                finally
                {
                    host.Dispose();
                }
                await host.Drained.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            var fdsAfter = OpenDescriptors();

            var open = handles.Count(h => h.TryGetTarget(out var handle) && !handle.IsClosed);
            Assert.True(open == 0, $"{hosts} disposed hosts keep {open} pipe handles open; descriptors {fdsBefore} -> {fdsAfter}");
        }
        finally
        {
            foreach (var pid in pids)
                KillQuietly(pid);
        }
    }

    // On Windows a host's output and errors are synchronous file streams over anonymous pipes: a read already waiting on
    // one ends neither by a token nor by Dispose while a process the host started still holds the pipe open. Disposing
    // the host must still end both reads (by cancelling their I/O) and then close both handles.
    [Fact]
    public async Task A_disposed_host_on_Windows_ends_its_reads_while_a_process_it_started_still_holds_its_pipes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: elsewhere a host's output streams are not synchronous file streams.");

        // The process cmd starts inherits its output and errors, writes its id to its errors and stays for 10 minutes.
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "/c start \"\" /b powershell.exe -NoProfile -NonInteractive -Command \"[Console]::Error.WriteLine($PID); Start-Sleep -Seconds 600\"",
        };
        int? pid = null;
        try
        {
            var process = Process.Start(start)!;
            var stdout = ((FileStream)process.StandardOutput.BaseStream).SafeFileHandle;
            var stderr = ((FileStream)process.StandardError.BaseStream).SafeFileHandle;
            var host = new PluginProcessLauncher.HostProcess(process);
            try
            {
                await host.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await Eventually.Until(() => int.TryParse(host.ErrorTail.Trim(), System.Globalization.CultureInfo.InvariantCulture, out _), "the process it started writes its id");
                pid = int.Parse(host.ErrorTail.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                host.Dispose();
            }

            // Both reads end while that process still holds the pipes; the handles are closed once they have (by disposal,
            // or by its background release when the reads took longer than disposal's wait).
            await host.Drained.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Eventually.Until(() => stdout.IsClosed && stderr.IsClosed, "both pipe handles are closed");
            using (var holder = Process.GetProcessById(pid.Value))
                Assert.False(holder.HasExited, "the process holding the pipes is still running");
        }
        finally
        {
            if (pid is { } id)
                KillQuietly(id);
        }
    }

    // A host disposed just as its drain has let a read in, before that read waits on its pipe: a cancel then finds
    // nothing waiting, and the read waits after it, where disposing its stream doesn't end it (Windows' synchronous pipe
    // reads). Disposal must end that read too, then the stream is disposed, and nothing is logged.
    [Fact]
    public async Task A_read_that_starts_waiting_after_its_host_is_disposed_is_ended_too()
    {
        using var child = StartIdle();
        var childId = child.Id;
        var (stdout, stderr) = (new GatedPipeStream(held: true), new GatedPipeStream(held: false));
        var log = new LinesLogger();
        var host = new PluginProcessLauncher.HostProcess(child, stdout, stderr, () => { stdout.Cancel(); stderr.Cancel(); }, log);
        try
        {
            await stdout.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Eventually.Until(() => stderr.Waiting, "the errors' read waits");

            // Disposal cancels while the output's read is let in but not yet waiting; then that read waits.
            var disposing = Task.Run(host.Dispose, TestContext.Current.CancellationToken);
            await Eventually.Until(() => stdout.Cancels > 0, "disposal has cancelled the reads");
            stdout.Release();
            await disposing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(host.Drained.IsCompletedSuccessfully, $"both reads ended: output waiting {stdout.Waiting}, errors waiting {stderr.Waiting}");
            Assert.True(stdout.Disposed && stderr.Disposed, $"streams disposed: output {stdout.Disposed}, errors {stderr.Disposed}");
            Assert.Empty(log.Lines);
        }
        finally
        {
            stdout.End();
            stderr.End();
            KillQuietly(childId);
        }
    }

    // A read that ends neither by its cancel nor by its stream's dispose holds disposal at most its wait, and is logged;
    // its streams stay open (their reads can still be cancelled) until it ends, then are disposed.
    [Fact]
    public async Task A_read_that_never_ends_on_dispose_is_logged()
    {
        using var child = StartIdle();
        var childId = child.Id;
        var (stdout, stderr) = (new GatedPipeStream(held: false) { IgnoresCancel = true }, new GatedPipeStream(held: false));
        var log = new LinesLogger();
        var host = new PluginProcessLauncher.HostProcess(child, stdout, stderr, () => { stdout.Cancel(); stderr.Cancel(); }, log);
        try
        {
            await Eventually.Until(() => stdout.Waiting && stderr.Waiting, "both reads wait");
            var took = Stopwatch.StartNew();
            await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"disposal took {took.Elapsed}");
            Assert.False(host.Drained.IsCompleted);
            Assert.False(stdout.Disposed || stderr.Disposed, $"streams kept while a read goes on: output {stdout.Disposed}, errors {stderr.Disposed}");
            Assert.Contains(log.Lines, l => l.Contains(childId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // The read ends by itself (the process holding the pipe closes it): then both streams are disposed.
            stdout.End();
            await DrainedWithin(host, TimeSpan.FromSeconds(10), stdout, stderr);
            await Eventually.Until(() => stdout.Disposed && stderr.Disposed, "both streams are disposed");
            Assert.Single(log.Lines);
        }
        finally
        {
            stdout.End();
            stderr.End();
            KillQuietly(childId);
        }
    }

    // A host disposed while its drain has let a read in whose wait only starts after disposal's bounded wait has passed
    // (Windows' synchronous pipe reads: the pool thread that runs ReadFile may start late): no cancel during that wait
    // finds it. Disposal still returns after its wait and logs, keeps the streams open, and goes on ending the reads, so
    // the late read is ended once it waits; then both streams are disposed.
    [Fact]
    public async Task A_read_that_starts_waiting_after_disposal_has_stopped_waiting_is_ended_too()
    {
        using var child = StartIdle();
        var childId = child.Id;
        var (stdout, stderr) = (new GatedPipeStream(held: true), new GatedPipeStream(held: false));
        var log = new LinesLogger();
        var host = new PluginProcessLauncher.HostProcess(child, stdout, stderr, () => { stdout.Cancel(); stderr.Cancel(); }, log);
        try
        {
            await stdout.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Eventually.Until(() => stderr.Waiting, "the errors' read waits");

            // Disposal waits for the reads, cancelling, and returns with the output's read let in but not yet waiting.
            var took = Stopwatch.StartNew();
            await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"disposal took {took.Elapsed}");
            Assert.False(host.Drained.IsCompleted);
            Assert.Contains(log.Lines, l => l.Contains(childId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Only now does the output's read wait: it is ended, both reads have ended and both streams are disposed.
            stdout.Release();
            await DrainedWithin(host, TimeSpan.FromSeconds(10), stdout, stderr);
            Assert.Equal(1, stdout.Waits);
            await Eventually.Until(() => stdout.Disposed && stderr.Disposed, "both streams are disposed");
            Assert.Single(log.Lines);
        }
        finally
        {
            stdout.End();
            stderr.End();
            KillQuietly(childId);
        }
    }

    private static async Task DrainedWithin(PluginProcessLauncher.HostProcess host, TimeSpan within, GatedPipeStream stdout, GatedPipeStream stderr)
    {
        await Task.WhenAny(host.Drained, Task.Delay(within, TestContext.Current.CancellationToken));
        Assert.True(host.Drained.IsCompletedSuccessfully, $"both reads ended within {within}: output waiting {stdout.Waiting}, errors waiting {stderr.Waiting}");
    }

    // A process that stays (killed by the test), standing in for the host; its own output isn't read.
    private static Process StartIdle()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sleep", "600");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return Process.Start(start)!;
    }

    // The host disposes its process object; the process itself is killed by its id.
    private static void KillQuietly(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    // A pipe's stream as Windows' synchronous file streams behave: a read runs on a pool thread; once it waits, its
    // cancel (CancelIoEx) ends it and its stream's dispose doesn't; a cancel ends only a read already waiting, and does
    // nothing once the stream is disposed (its handle is closed to new calls). While held, a read stops at its entry:
    // let in by the drain, not yet waiting.
    private sealed class GatedPipeStream(bool held) : Stream
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _entry = new(!held);
        private TaskCompletionSource<int>? _waiting;
        private bool _ended;
        private int _cancels;
        private int _waits;

        public bool IgnoresCancel { get; init; }

        /// <summary>Completes when a read has reached its entry.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Waiting
        {
            get
            {
                lock (_gate)
                    return _waiting != null;
            }
        }

        public int Cancels => Volatile.Read(ref _cancels);

        /// <summary>How many reads have started waiting.</summary>
        public int Waits => Volatile.Read(ref _waits);

        public bool Disposed { get; private set; }

        public void Release() => _entry.Set();

        public void Cancel()
        {
            Interlocked.Increment(ref _cancels);
            lock (_gate)
            {
                if (IgnoresCancel || Disposed || _waiting == null)
                    return;
                _waiting.TrySetCanceled();
                _waiting = null;
            }
        }

        /// <summary>The test's clean-up: every read, waiting or to come, ends at once.</summary>
        public void End()
        {
            lock (_gate)
            {
                _ended = true;
                _waiting?.TrySetResult(0);
                _waiting = null;
            }
            _entry.Set();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Task.Run(Wait, CancellationToken.None));

        public override int Read(byte[] buffer, int offset, int count) => Wait();

        private int Wait()
        {
            Entered.TrySetResult();
            _entry.Wait();
            TaskCompletionSource<int> waiting;
            lock (_gate)
            {
                if (_ended)
                    return 0;
                waiting = _waiting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits++;
            }
            return waiting.Task.GetAwaiter().GetResult();
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Every line logged, formatted.
    private sealed class LinesLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
                _lines.Add(formatter(state, exception));
        }
    }

    // Kept apart so the test itself holds no strong reference to the streams or their handles.
    private static void Track(List<WeakReference<SafePipeHandle>> handles, Process process)
    {
        handles.Add(new WeakReference<SafePipeHandle>(((PipeStream)process.StandardOutput.BaseStream).SafePipeHandle));
        handles.Add(new WeakReference<SafePipeHandle>(((PipeStream)process.StandardError.BaseStream).SafePipeHandle));
    }

    // This process's open file descriptors (Unix), for the report; other tests running alongside move it too.
    private static int OpenDescriptors() => Directory.Exists("/dev/fd") ? Directory.GetFileSystemEntries("/dev/fd").Length : -1;
}
