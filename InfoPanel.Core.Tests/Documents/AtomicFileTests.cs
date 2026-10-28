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

    /// <summary>
    /// A private file's temp file is owner read and write only from its creation, at every step while it is written,
    /// and so is the file it becomes, even where the target had wider permissions.
    /// </summary>
    [Fact]
    public void WritePrivate_TempFileIsOwnerOnlyWhileItIsWritten()
    {
        if (OperatingSystem.IsWindows())
            return; // no Unix modes there
        using var temp = new TempDirectory();
        var path = temp.Combine("tokens.json");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var seen = new List<(StorageStep Step, UnixFileMode Mode)>();
        var faults = new MatchFaults
        {
            Fail = (step, _) =>
            {
                foreach (var file in Directory.EnumerateFiles(temp.Path, "tokens.json.*" + AtomicFile.TempSuffix))
                    seen.Add((step, File.GetUnixFileMode(file)));
                return false;
            },
        };

        AtomicFile.WritePrivate(path, "{\"tokens\":[]}"u8.ToArray(), root: null, faults);

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal([StorageStep.WriteTemp, StorageStep.FlushTemp, StorageStep.ReplaceFile], seen.Select(s => s.Step));
        Assert.All(seen, s => Assert.Equal(ownerOnly, s.Mode));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
        Assert.Equal("{\"tokens\":[]}", File.ReadAllText(path));
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

    /// <summary>
    /// On macOS every folder flush is a full flush (<c>F_FULLFSYNC</c>), so a write that answered survives a power loss:
    /// each folder a write, a new folder or a profile's save flushes is asked for one, in the same order, and the real
    /// call succeeds here (no fallback warning).
    /// </summary>
    [Fact]
    public void SyncFolder_OnMacOS_IsAlwaysAFullFlush()
    {
        if (!OperatingSystem.IsMacOS())
            return; // a plain fsync on Linux; no folder flush on Windows
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            var faults = new MatchFaults();
            var root = temp.Combine("data");
            AtomicFile.Write(Path.Combine(root, "a", "b", "file.json"), "first"u8.ToArray(), root, faults);
            AtomicFile.Write(Path.Combine(root, "a", "b", "file.json"), "second"u8.ToArray(), root, faults);
            AtomicFile.SyncFolder(root, faults);
            var stores = new Stores(new InfoPanel.Persistence.DataPaths(temp.Combine("store")), faults).Open();
            var id = StorageTestData.NewId();
            stores.Documents.Create(id, "{}"u8.ToArray());
            stores.Documents.Save(id, "{ }"u8.ToArray());
            stores.Blobs.Put("blob"u8.ToArray());

            var flushed = faults.Steps.Where(s => s.Step == StorageStep.SyncFolder).Select(s => s.Path).ToList();
            Assert.True(flushed.Count >= 10, string.Join(", ", flushed));
            Assert.Equal(flushed, faults.FullSyncs);
            Assert.Empty(sink.Warnings("F_FULLFSYNC"));
            Assert.Empty(sink.Warnings());
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// A file system that can't take a full flush (macOS: <c>F_FULLFSYNC</c> fails, whatever the error) gets a plain
    /// <c>fsync</c>: the write succeeds, and one warning is logged per process, however many folders and writes.
    /// </summary>
    [Fact]
    public void SyncFolder_FullFlushFails_FallsBackToFsync_AndWarnsOncePerProcess()
    {
        if (!OperatingSystem.IsMacOS())
            return; // only macOS asks for a full flush
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            foreach (var (name, errno) in new[] { ("enotsup", 45), ("einval", 22), ("eio", 5) })
            {
                var path = temp.Combine(name, "file.json");
                var faults = new MatchFaults { FullSyncErrno = errno };

                AtomicFile.Write(path, "first"u8.ToArray(), temp.Path, faults);
                AtomicFile.Write(path, "second"u8.ToArray(), temp.Path, faults);

                Assert.Equal("second", File.ReadAllText(path));
                Assert.NotEmpty(faults.FullSyncs);
            }

            Assert.Single(sink.Warnings("F_FULLFSYNC"));
            Assert.Empty(sink.Warnings());
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
        public LogEvent[] Warnings() => Warnings("can't flush folders");

        /// <summary>The warnings whose message template contains <paramref name="text"/>.</summary>
        public LogEvent[] Warnings(string text)
        {
            lock (_events)
            {
                return _events.Where(e => e.Level == LogEventLevel.Warning
                    && e.MessageTemplate.Text.Contains(text)).ToArray();
            }
        }
    }
}
