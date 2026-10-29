using System.ComponentModel;
using System.Diagnostics;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The engine's folder opener: the system's file manager on each platform (1.4.x: <c>explorer.exe</c>), started on a
/// folder that is there and never on one that isn't; a folder asked for again within a second, or past four file managers
/// still starting, starts none. Its process start is replaced, so nothing opens.
/// </summary>
public sealed class FolderOpenerTests
{
    [Theory]
    [InlineData(FolderOpener.Platform.MacOS, "open")]
    [InlineData(FolderOpener.Platform.Windows, "explorer.exe")]
    [InlineData(FolderOpener.Platform.Linux, "xdg-open")]
    public void TheCommand_IsTheSystemsFileManager_WithTheFolderAsItsOneArgument(FolderOpener.Platform platform, string program)
    {
        var folder = Path.Combine(Path.GetTempPath(), "a folder; with \"quotes\" & spaces");

        var start = FolderOpener.CommandFor(platform, folder);

        Assert.Equal(program, start.FileName);
        Assert.Equal([folder], start.ArgumentList);
        Assert.Equal("", start.Arguments);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void AFolderThatIsThere_IsHandedToTheFileManager()
    {
        using var temp = new TempFolder();
        var started = new List<ProcessStartInfo>();
        var opener = new FolderOpener(info => { started.Add(info); return Task.CompletedTask; }, TimeProvider.System);

        opener.Open(temp.FullName);

        var start = Assert.Single(started);
        Assert.Equal([temp.FullName], start.ArgumentList);
        Assert.Equal(FolderOpener.CommandFor(FolderOpener.Current, temp.FullName).FileName, start.FileName);
    }

    [Fact]
    public void AFolderThatIsntThere_Throws_AndStartsNothing()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.FullName, "a-file");
        File.WriteAllText(file, "not a folder");
        var started = new List<ProcessStartInfo>();
        var opener = new FolderOpener(info => { started.Add(info); return Task.CompletedTask; }, TimeProvider.System);

        Assert.Throws<DirectoryNotFoundException>(() => opener.Open(Path.Combine(temp.FullName, "missing")));
        Assert.Throws<DirectoryNotFoundException>(() => opener.Open(file));
        Assert.Empty(started);
    }

    [Fact]
    public void AFileManagerThatCantBeStarted_Throws()
    {
        using var temp = new TempFolder();
        var opener = new FolderOpener(_ => throw new Win32Exception(2, "No such file or directory"), TimeProvider.System);

        Assert.Throws<Win32Exception>(() => opener.Open(temp.FullName));
    }

    [Fact]
    public void AFolderAskedForAgainWithinASecond_StartsNothingMore()
    {
        using var data = new TempFolder();
        using var logs = new TempFolder();
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var started = new List<string>();
        var opener = new FolderOpener(info => { started.Add(info.ArgumentList[0]); return Task.CompletedTask; }, time);

        opener.Open(data.FullName);
        opener.Open(data.FullName);
        time.Advance(TimeSpan.FromMilliseconds(999));
        opener.Open(data.FullName);
        // Another folder is its own.
        opener.Open(logs.FullName);
        Assert.Equal([data.FullName, logs.FullName], started);

        // A second after the start that counted, it starts again.
        time.Advance(TimeSpan.FromMilliseconds(1));
        opener.Open(data.FullName);
        Assert.Equal([data.FullName, logs.FullName, data.FullName], started);
    }

    [Fact]
    public void AtMostFourFileManagersStart_UntilOneExits_AndOpenNeverWaitsForThem()
    {
        var folders = Enumerable.Range(0, 6).Select(_ => new TempFolder()).ToList();
        try
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var logger = new ListLogger();
            var running = new List<TaskCompletionSource>();
            var opener = new FolderOpener(_ =>
            {
                var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                running.Add(exit);
                return exit.Task;
            }, time, logger);

            // None of them exits: each Open returns at once all the same.
            foreach (var folder in folders.Take(5))
                opener.Open(folder.FullName);
            Assert.Equal(FolderOpener.MaxStarting, running.Count);
            Assert.Equal(4, FolderOpener.MaxStarting);
            Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains(folders[4].FullName));

            // One exits: the next starts.
            running[0].SetResult();
            Assert.True(SpinWait.SpinUntil(() => { opener.Open(folders[5].FullName); return running.Count == 5; }, TimeSpan.FromSeconds(5)));
            // An exit that fails frees its place too.
            running[1].SetException(new InvalidOperationException("gone"));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(SpinWait.SpinUntil(() => { opener.Open(folders[4].FullName); return running.Count == 6; }, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            folders.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void AStartThatFails_HoldsNoPlace_AndTheNextTryStartsAgain()
    {
        using var temp = new TempFolder();
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var tries = 0;
        var fail = true;
        var opener = new FolderOpener(_ =>
        {
            tries++;
            if (fail)
                throw new Win32Exception(2, "No such file or directory");
            return new TaskCompletionSource().Task;
        }, time);

        // More failures than there are places, each within the second: each one tries, none holds a place.
        for (var i = 0; i < FolderOpener.MaxStarting + 2; i++)
            Assert.Throws<Win32Exception>(() => opener.Open(temp.FullName));
        Assert.Equal(FolderOpener.MaxStarting + 2, tries);

        fail = false;
        opener.Open(temp.FullName);
        Assert.Equal(FolderOpener.MaxStarting + 3, tries);
    }

    [Fact]
    public void AStartThatFailsLate_DoesNotEraseANewerStartsTime()
    {
        using var temp = new TempFolder();
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var calls = 0;
        var aInside = new ManualResetEventSlim();
        var releaseA = new ManualResetEventSlim();
        var opener = new FolderOpener(_ =>
        {
            // A is the first call: it stalls inside the start and then fails. B is the second and succeeds.
            if (Interlocked.Increment(ref calls) == 1)
            {
                aInside.Set();
                Assert.True(releaseA.Wait(TimeSpan.FromSeconds(10)));
                throw new Win32Exception(2, "No such file or directory");
            }

            return new TaskCompletionSource().Task;
        }, time);

        // A reserves the folder and stalls; a second later B starts the same folder; then A fails.
        var a = Task.Run(() => Assert.Throws<Win32Exception>(() => opener.Open(temp.FullName)), TestContext.Current.CancellationToken);
        Assert.True(aInside.Wait(TimeSpan.FromSeconds(10)));
        time.Advance(FolderOpener.Repeat);
        opener.Open(temp.FullName);
        Assert.Equal(2, Volatile.Read(ref calls));
        releaseA.Set();
        a.GetAwaiter().GetResult();

        // B is less than a second old: C, straight after, starts nothing.
        opener.Open(temp.FullName);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>A logger that keeps each line it is given.</summary>
    private sealed class ListLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<(LogLevel Level, string Message)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Lines
        {
            get
            {
                lock (_lock)
                    return [.. _lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
                _lines.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>A new empty folder under the temp folder, removed with what it holds.</summary>
    private sealed class TempFolder : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("infopanel-opener-tests-");

        public string FullName => _folder.FullName;

        public void Dispose()
        {
            try { _folder.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
