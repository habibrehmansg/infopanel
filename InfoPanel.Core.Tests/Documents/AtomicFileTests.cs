using System.Reflection;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using Microsoft.Win32.SafeHandles;
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

    /// <summary><c>MOVEFILE_REPLACE_EXISTING</c>.</summary>
    private const int MoveFileReplaceExisting = 0x1;

    /// <summary><c>MOVEFILE_WRITE_THROUGH</c>.</summary>
    private const int MoveFileWriteThrough = 0x8;

    /// <summary>
    /// On Windows every rename of a temp file over its target asks for write-through (<c>MoveFileExW</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH</c>), from the temp file beside it: a plain write, a private
    /// one, and a profile's create and save and a blob's upload through the stores.
    /// </summary>
    [Fact]
    public void Write_OnWindows_RenamesWithWriteThrough()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MoveFileExW is Windows' rename.");
        using var temp = new TempDirectory();
        var faults = new WindowsFaults();
        var root = temp.Combine("data");
        var path = Path.Combine(root, "a", "file.json");

        AtomicFile.Write(path, "first"u8.ToArray(), root, faults);
        AtomicFile.Write(path, "second"u8.ToArray(), root, faults);
        AtomicFile.WritePrivate(Path.Combine(root, "tokens.json"), "{}"u8.ToArray(), root, faults);
        var stores = new Stores(new InfoPanel.Persistence.DataPaths(temp.Combine("store")), faults).Open();
        var id = StorageTestData.NewId();
        stores.Documents.Create(id, "{}"u8.ToArray());
        stores.Documents.Save(id, "{ }"u8.ToArray());
        stores.Blobs.Put("blob"u8.ToArray());

        Assert.Equal("second", File.ReadAllText(path));
        var replaced = faults.StepsOf(StorageStep.ReplaceFile).Select(Path.GetFullPath).ToList();
        Assert.True(replaced.Count >= 6, string.Join(", ", replaced));
        var replaces = faults.ReplacesSeen();
        Assert.Equal(replaced, replaces.Select(r => r.Path));
        Assert.All(replaces, r =>
        {
            Assert.Equal(MoveFileReplaceExisting | MoveFileWriteThrough, r.Flags);
            Assert.Equal(Path.GetDirectoryName(r.Path), Path.GetDirectoryName(r.Temp));
            Assert.True(AtomicFile.IsTempFile(Path.GetFileName(r.Temp), name => name == Path.GetFileName(r.Path)), r.Temp);
        });
    }

    /// <summary>
    /// A rename Windows refuses fails the write, which keeps the target and removes its temp file: access denied as
    /// <see cref="UnauthorizedAccessException"/> and an aborted operation as <see cref="OperationCanceledException"/> (as
    /// <see cref="File.Move(string, string, bool)"/> reports them), any other error as an <see cref="IOException"/>.
    /// </summary>
    [Fact]
    public void Write_OnWindows_ARefusedRename_KeepsTheTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MoveFileExW is Windows' rename.");
        using var temp = new TempDirectory();
        var path = temp.Combine("file.json");
        AtomicFile.Write(path, "kept"u8.ToArray());

        foreach (var (error, type) in new[]
                 {
                     (32, typeof(IOException)), (5, typeof(UnauthorizedAccessException)),
                     (995, typeof(OperationCanceledException)), // ERROR_OPERATION_ABORTED
                 })
        {
            var faults = new WindowsFaults { ReplaceErrorCode = error };

            var thrown = Assert.ThrowsAny<Exception>(() => AtomicFile.Write(path, "lost"u8.ToArray(), root: null, faults));

            Assert.IsType(type, thrown);
            Assert.Contains($"error {error}", thrown.Message);
            Assert.Single(faults.ReplacesSeen());
            Assert.Equal("kept", File.ReadAllText(path));
            Assert.Equal(["file.json"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
        }
    }

    /// <summary>
    /// On Windows every folder flush opens the folder and flushes it (<c>FlushFileBuffers</c> on its handle), which
    /// succeeds on NTFS: each folder a write, a new folder or a profile's save flushes is flushed, in order, by the
    /// native call itself (recorded where the seam hands it over), with no warning (the real file system is NTFS); a
    /// folder that can't be opened is an error.
    /// </summary>
    [Fact]
    public void SyncFolder_OnWindows_FlushesEveryFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' folder flush.");
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            var faults = new WindowsFaults();
            var root = temp.Combine("data");
            AtomicFile.Write(Path.Combine(root, "a", "b", "file.json"), "first"u8.ToArray(), root, faults);
            AtomicFile.Write(Path.Combine(root, "a", "b", "file.json"), "second"u8.ToArray(), root, faults);
            AtomicFile.SyncFolder(root, faults);
            var stores = new Stores(new InfoPanel.Persistence.DataPaths(temp.Combine("store")), faults).Open();
            var id = StorageTestData.NewId();
            stores.Documents.Create(id, "{}"u8.ToArray());
            stores.Documents.Save(id, "{ }"u8.ToArray());
            stores.Blobs.Put("blob"u8.ToArray());
            AtomicFile.SyncFolder(temp.Path);

            var flushed = faults.StepsOf(StorageStep.SyncFolder);
            Assert.True(flushed.Count >= 10, string.Join(", ", flushed));
            Assert.Equal(flushed, faults.FlushesSeen());
            Assert.All(faults.NativeFlushes(), f =>
            {
                Assert.Equal("FlushFileBuffers", f.Method.Name);
                Assert.True(f.Method.Attributes.HasFlag(MethodAttributes.PinvokeImpl), $"{f.Method} is not a native call");
                Assert.Equal(0, f.Result);
            });
            Assert.Empty(sink.Warnings());
            Assert.Throws<IOException>(() => AtomicFile.SyncFolder(temp.Combine("missing"), faults));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// A file system that can't flush folders on Windows (<c>ERROR_INVALID_FUNCTION</c>, <c>ERROR_NOT_SUPPORTED</c>) is
    /// not an error: the write succeeds and one warning is logged per process, however many folders and writes. Any
    /// other flush failure fails the write, after its rename.
    /// </summary>
    [Fact]
    public void SyncFolder_OnWindows_UnsupportedByTheFileSystem_WritesAndWarnsOncePerProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' folder flush.");
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            foreach (var (name, error) in new[] { ("invalid-function", 1), ("not-supported", 50) })
            {
                var path = temp.Combine(name, "file.json");
                var faults = new WindowsFaults { FolderError = error };

                AtomicFile.Write(path, "first"u8.ToArray(), temp.Path, faults);
                AtomicFile.Write(path, "second"u8.ToArray(), temp.Path, faults);

                Assert.Equal("second", File.ReadAllText(path));
                Assert.NotEmpty(faults.FlushesSeen());
            }

            Assert.Single(sink.Warnings());

            foreach (var error in new[] { 5, 1117 }) // access denied; an I/O device error
            {
                var folder = temp.Combine($"error-{error}");
                Directory.CreateDirectory(folder);
                var faults = new WindowsFaults { FolderError = error };

                var thrown = Assert.Throws<IOException>(() => AtomicFile.Write(Path.Combine(folder, "file.json"), "x"u8.ToArray(), root: null, faults));

                Assert.Contains($"error {error}", thrown.Message);
                Assert.Equal("x", File.ReadAllText(Path.Combine(folder, "file.json")));
            }

            Assert.Single(sink.Warnings());
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// Only NTFS is known to persist a folder's entries on <c>FlushFileBuffers</c>; on any other file system (ReFS, FAT,
    /// exFAT) a flush can succeed without doing so. There a successful flush still logs the warning of a file system that
    /// can't flush folders, once per process, and the write succeeds; on NTFS nothing is logged.
    /// </summary>
    [Fact]
    public void SyncFolder_OnWindows_ASuccessfulFlushOffNtfs_WritesAndWarnsOncePerProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' folder flush.");
        using var temp = new TempDirectory();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        AtomicFile.ResetFolderSyncWarning();
        try
        {
            var ntfs = new WindowsFaults { FileSystem = "NTFS" };
            AtomicFile.Write(temp.Combine("ntfs", "file.json"), "x"u8.ToArray(), temp.Path, ntfs);
            Assert.Empty(sink.Warnings());

            var others = new List<WindowsFaults>();
            foreach (var fileSystem in new[] { "FAT32", "ReFS", "exFAT" })
            {
                var path = temp.Combine(fileSystem, "file.json");
                var faults = new WindowsFaults { FileSystem = fileSystem };
                others.Add(faults);

                AtomicFile.Write(path, "first"u8.ToArray(), temp.Path, faults);
                AtomicFile.Write(path, "second"u8.ToArray(), temp.Path, faults);

                Assert.Equal("second", File.ReadAllText(path));
            }

            var warning = Assert.Single(sink.Warnings());
            Assert.Equal("\"FAT32\"", warning.Properties["FileSystem"].ToString());
            Assert.All(others.Prepend(ntfs), faults =>
            {
                Assert.NotEmpty(faults.NativeFlushes());
                Assert.All(faults.NativeFlushes(), f => Assert.Equal(0, f.Result)); // the real flush succeeded
            });
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// Windows: records every step, every folder flush with the native call it was handed and its result (reporting
    /// <see cref="FolderError"/> instead of calling it when it is set), every rename with its flags (reporting
    /// <see cref="ReplaceErrorCode"/> when it is set), and reports <see cref="FileSystem"/> as the folders' file system
    /// when it is set.
    /// </summary>
    private sealed class WindowsFaults : IStorageFaults
    {
        private readonly List<(StorageStep Step, string Path)> _steps = [];
        private readonly List<string> _flushes = [];
        private readonly List<(MethodInfo Method, int Result)> _nativeFlushes = [];
        private readonly List<(string Temp, string Path, int Flags)> _replaces = [];

        public int FolderError { get; init; }

        public int ReplaceErrorCode { get; init; }

        public string? FileSystem { get; init; }

        public void Before(StorageStep step, string path)
        {
            lock (_steps)
                _steps.Add((step, path));
        }

        public int FlushFolder(string folder, SafeFileHandle handle, Func<SafeFileHandle, bool> flush)
        {
            lock (_steps)
                _flushes.Add(folder);
            if (FolderError != 0)
                return FolderError;
            var result = AtomicFile.CallNative(flush, handle);
            lock (_steps)
                _nativeFlushes.Add((flush.Method, result));
            return result;
        }

        public string? FileSystemName(string folder) => FileSystem;

        /// <summary>Each native flush called, with its result: what the seam was handed, so a flush that isn't the native call shows.</summary>
        public List<(MethodInfo Method, int Result)> NativeFlushes()
        {
            lock (_steps)
                return [.. _nativeFlushes];
        }

        public int ReplaceError(string temp, string path, int flags)
        {
            lock (_steps)
                _replaces.Add((temp, path, flags));
            return ReplaceErrorCode;
        }

        public List<string> StepsOf(StorageStep step)
        {
            lock (_steps)
                return _steps.Where(s => s.Step == step).Select(s => s.Path).ToList();
        }

        public List<string> FlushesSeen()
        {
            lock (_steps)
                return [.. _flushes];
        }

        public List<(string Temp, string Path, int Flags)> ReplacesSeen()
        {
            lock (_steps)
                return [.. _replaces];
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
