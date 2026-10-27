using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Documents;

public class AtomicFileTests
{
    [Fact]
    public void Write_CreatesTheFolderAndReplacesTheTarget()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a", "b", "file.json");

        AtomicFile.Write(path, "first"u8.ToArray());
        AtomicFile.Write(path, "second"u8.ToArray());

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(["file.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_FailingWriterKeepsTheTargetAndRemovesTheTemp()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("file.json");
        AtomicFile.Write(path, "kept"u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write("lost"u8);
            throw new InvalidOperationException("writer failed");
        }));

        Assert.Equal("kept", File.ReadAllText(path));
        Assert.Equal(["file.json"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }

    /// <summary>A crash at each step leaves the target old or new, never partial; the temp file holds whatever was written.</summary>
    [Fact]
    public void Write_CrashAtEveryStep_TargetIsOldOrNew()
    {
        var steps = new List<StorageStep>();
        for (var failAt = 1; ; failAt++)
        {
            using var temp = new TempDirectory();
            var path = temp.Combine("file.json");
            AtomicFile.Write(path, "old content"u8.ToArray());
            var faults = new StepFaults(failAt, crash: true) { Armed = true };

            try
            {
                AtomicFile.Write(path, "new content, longer"u8.ToArray(), root: null, faults);
            }
            catch (SimulatedCrash)
            {
            }

            Assert.Contains(File.ReadAllText(path), new[] { "old content", "new content, longer" });
            if (!faults.Fired)
            {
                steps = faults.Steps.Select(s => s.Step).ToList();
                break;
            }
        }

        Assert.Equal([StorageStep.CreateTemp, StorageStep.WriteTemp, StorageStep.FlushTemp, StorageStep.ReplaceFile, StorageStep.SyncFolder], steps);
    }

    [Fact]
    public void SyncFolder_FailureIsAnError()
    {
        using var temp = new TempDirectory();
        AtomicFile.SyncFolder(temp.Path);
        if (OperatingSystem.IsWindows())
            AtomicFile.SyncFolder(temp.Combine("missing")); // folders aren't flushed there
        else
            Assert.Throws<IOException>(() => AtomicFile.SyncFolder(temp.Combine("missing")));
    }

    /// <summary>
    /// Each missing folder is created and its parent flushed at once, top down; below the root an existing one has its
    /// parent flushed again, above it (and with no root) it is left alone.
    /// </summary>
    [Fact]
    public void CreateFolder_FlushesEachNewFoldersParent()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("root");
        var faults = new StepFaults(int.MaxValue, crash: false) { Armed = true };

        AtomicFile.CreateFolder(Path.Combine(root, "a", "b"), root, faults);
        AtomicFile.CreateFolder(Path.Combine(root, "a", "b"), root, faults);
        AtomicFile.CreateFolder(Path.Combine(root, "a", "b"), root: null, faults);

        Assert.True(Directory.Exists(Path.Combine(root, "a", "b")));
        Assert.Equal(
        [
            (StorageStep.CreateFolder, root), (StorageStep.SyncFolder, temp.Path),
            (StorageStep.CreateFolder, Path.Combine(root, "a")), (StorageStep.SyncFolder, root),
            (StorageStep.CreateFolder, Path.Combine(root, "a", "b")), (StorageStep.SyncFolder, Path.Combine(root, "a")),
            (StorageStep.SyncFolder, root), (StorageStep.SyncFolder, Path.Combine(root, "a")),
        ], faults.Steps);
    }

    /// <summary>A folder created but whose parent's flush failed has that flush done again by the next call.</summary>
    [Fact]
    public void CreateFolder_ParentFlushFailed_RetryFlushesIt()
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("blobs");
        var faults = new MatchFaults { Fail = (step, path) => step == StorageStep.SyncFolder && path == temp.Path };

        Assert.Throws<IOException>(() => AtomicFile.CreateFolder(folder, temp.Path, faults));
        Assert.True(Directory.Exists(folder));

        faults.Fail = null;
        faults.Steps.Clear();
        AtomicFile.CreateFolder(folder, temp.Path, faults);
        Assert.Equal([(StorageStep.SyncFolder, temp.Path)], faults.Steps);
    }

    /// <summary>
    /// A file system that can't flush folders (<c>EINVAL</c>, <c>ENOTSUP</c>) is not an error: the write succeeds and one
    /// warning is logged per process, however many folders and writes. Any other flush failure still throws.
    /// </summary>
    [Fact]
    public void SyncFolder_UnsupportedByTheFileSystem_WritesAndWarnsOncePerProcess()
    {
        if (OperatingSystem.IsWindows())
            return; // folders aren't flushed there
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            var enotsup = OperatingSystem.IsMacOS() ? 45 : 95;
            foreach (var (name, errno) in new[] { ("einval", 22), ("enotsup", enotsup) })
            {
                var path = temp.Combine(name, "file.json");
                Directory.CreateDirectory(temp.Combine(name));
                var faults = new MatchFaults { FolderSyncErrno = errno };

                AtomicFile.Write(path, "first"u8.ToArray(), root: null, faults);
                AtomicFile.Write(path, "second"u8.ToArray(), root: null, faults);

                Assert.Equal("second", File.ReadAllText(path));
            }

            Assert.Single(sink.Warnings());

            var eio = new MatchFaults { FolderSyncErrno = 5 };
            Directory.CreateDirectory(temp.Combine("eio"));
            Assert.Throws<IOException>(() => AtomicFile.Write(temp.Combine("eio", "file.json"), "x"u8.ToArray(), root: null, eio));
            Assert.Single(sink.Warnings());
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }

        /// <summary>The warnings about a file system that can't flush folders (other tests' warnings reach the global logger too).</summary>
        public LogEvent[] Warnings()
        {
            lock (_events)
            {
                return _events.Where(e => e.Level == LogEventLevel.Warning
                    && e.MessageTemplate.Text.Contains("can't flush folders")).ToArray();
            }
        }
    }
}
