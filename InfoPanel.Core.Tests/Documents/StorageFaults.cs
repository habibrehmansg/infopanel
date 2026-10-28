using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>The process dying at a storage step: thrown at that step and at every later one, so nothing after it runs.</summary>
internal sealed class SimulatedCrash : Exception
{
    public SimulatedCrash() : base("simulated crash") { }
}

/// <summary>
/// Fails the <see cref="FailAt"/>-th storage step (1-based) after <see cref="Armed"/> is set: a crash (that step and
/// every later one throw <see cref="SimulatedCrash"/>), or an <see cref="IOException"/> at that step only. Records the
/// steps in order, so tests can check that each flush comes before what depends on it.
/// </summary>
internal sealed class StepFaults(int failAt, bool crash) : IStorageFaults
{
    private bool _crashed;
    private int _failed = -1;

    public int FailAt { get; } = failAt;

    public bool Armed { get; set; }

    /// <summary>The steps reached while armed, the failed one included.</summary>
    public List<(StorageStep Step, string Path)> Steps { get; } = [];

    public bool Fired { get; private set; }

    public void Before(StorageStep step, string path)
    {
        if (_crashed)
            throw new SimulatedCrash();
        if (!Armed)
            return;
        Steps.Add((step, path));
        if (Steps.Count != FailAt)
            return;

        Fired = true;
        _failed = Steps.Count - 1;
        if (crash)
        {
            _crashed = true;
            throw new SimulatedCrash();
        }

        throw new IOException($"injected failure at {step} {path}");
    }

    /// <summary>Whether a step that was taken (any but the failed one) matches.</summary>
    public bool Completed(StorageStep step, Func<string, bool> path) =>
        Steps.Where((s, i) => i != _failed).Any(s => s.Step == step && path(s.Path));

    /// <summary>
    /// Checks a whole run's steps: each new folder, replaced file and renamed folder is followed at once by flushing the
    /// folder that holds it, so nothing written in a new folder, no blob id returned and no cleanup of a deleted folder
    /// comes before the change it depends on is on disk.
    /// </summary>
    public static void AssertEachChangeFlushedFirst(IReadOnlyList<(StorageStep Step, string Path)> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var (step, path) = steps[i];
            if (step is not (StorageStep.CreateFolder or StorageStep.ReplaceFile or StorageStep.MoveFolder))
                continue;
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            Assert.True(i + 1 < steps.Count && steps[i + 1].Step == StorageStep.SyncFolder && steps[i + 1].Path == parent,
                $"{step} {path} is not followed by flushing {parent}: {string.Join(", ", steps.Skip(i + 1).Take(3))}");
        }
    }
}

/// <summary>
/// Fails every step <see cref="Fail"/> matches with an <see cref="IOException"/>, and has every folder flush report
/// <see cref="FolderSyncErrno"/> when it is set, and every full flush (macOS) <see cref="FullSyncErrno"/>; records every
/// step, the failed ones included, and every folder a full flush was asked for.
/// </summary>
internal sealed class MatchFaults : IStorageFaults
{
    public Func<StorageStep, string, bool>? Fail { get; set; }

    public int FolderSyncErrno { get; set; }

    public List<(StorageStep Step, string Path)> Steps { get; } = [];

    public void Before(StorageStep step, string path)
    {
        Steps.Add((step, path));
        if (Fail?.Invoke(step, path) == true)
            throw new IOException($"injected failure at {step} {path}");
    }

    public int FolderSyncError(string folder) => FolderSyncErrno;

    public int FullSyncErrno { get; set; }

    /// <summary>The folders a full flush (<c>F_FULLFSYNC</c>, macOS) was asked for, in order.</summary>
    public List<string> FullSyncs { get; } = [];

    public int FullSyncError(string folder)
    {
        lock (FullSyncs)
            FullSyncs.Add(folder);
        return FullSyncErrno;
    }
}

/// <summary>A document store and a blob store on one data folder.</summary>
internal sealed class Stores
{
    public Stores(DataPaths paths, IStorageFaults? faults = null)
    {
        Documents = new DocumentStore(paths, null, faults);
        Blobs = new BlobStore(paths, null, faults);
    }

    public DocumentStore Documents { get; }

    public BlobStore Blobs { get; }

    /// <summary>New stores on the same folder, opened: the next start.</summary>
    public Stores Reopen() => new Stores(Documents.Paths).Open();

    /// <summary>Start-up: reconciles the documents and removes the blobs' temp files.</summary>
    public Stores Open()
    {
        Documents.Open();
        Blobs.Open();
        return this;
    }
}

internal static class StorageTestData
{
    public static string NewId() => Guid.NewGuid().ToString();

    public static string BlobId(string content) => BlobStore.IdOf(Encoding.UTF8.GetBytes(content));

    /// <summary>A small document (not schema-valid; the store doesn't parse) whose items refer to <paramref name="blobs"/>.</summary>
    public static byte[] Document(string id, string label, params string[] blobs)
    {
        var items = string.Join(",", blobs.Select((b, i) =>
            $"{{\"kind\":\"scene\",\"id\":\"i{i}\",\"scene\":{{\"root\":{{\"type\":\"media\",\"source\":{{\"asset\":\"{b}\"}}}}}}}}"));
        return Encoding.UTF8.GetBytes(
            $"{{\"format\":\"infopanel.document/1\",\"id\":\"{id}\",\"profile\":{{\"name\":\"{label}\"}},\"items\":[{items}]}}");
    }

    /// <summary>Every file and folder under <paramref name="root"/>, relative, sorted.</summary>
    public static string[] Tree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Runs <paramref name="act"/> once per storage step it takes, failing at that step (as a crash or an I/O error),
    /// each time on a fresh data folder prepared by <paramref name="arrange"/>; then reopens the folder with new stores and
    /// hands both to <paramref name="check"/> (completed is whether <paramref name="act"/> returned). Also checks what
    /// holds after every reopen: no temp file, staging or deleted folder is left, and every stored blob's bytes hash to its
    /// name. On the run that completes, checks that each change is flushed before what depends on it
    /// (<see cref="StepFaults.AssertEachChangeFlushedFirst"/>). Returns the number of steps the operation takes. With <paramref name="openFirst"/> false the stores
    /// <paramref name="act"/> gets are not opened yet, so it can fail start-up itself.
    /// </summary>
    public static int ForEveryStep(bool crash, Action<Stores> arrange, Action<Stores> act,
        Action<StepFaults, Stores, Stores, bool> check, bool openFirst = true)
    {
        for (var failAt = 1; ; failAt++)
        {
            using var temp = new TempDirectory();
            var paths = new DataPaths(temp.Path);
            arrange(new Stores(paths).Open());

            var faults = new StepFaults(failAt, crash);
            var live = new Stores(paths, faults);
            if (openFirst)
                live.Open();
            faults.Armed = true;
            var completed = false;
            try
            {
                act(live);
                completed = true;
            }
            catch (SimulatedCrash) when (crash)
            {
            }
            catch (IOException) when (!crash)
            {
            }

            Assert.True(completed || faults.Fired, $"step {failAt}: the operation failed without an injected fault");
            var reopened = new Stores(paths).Open();
            Assert.DoesNotContain(Tree(temp.Path), p => p.EndsWith(".tmp", StringComparison.Ordinal) || p.Contains(".deleted-", StringComparison.Ordinal));
            foreach (var id in reopened.Blobs.List())
            {
                using var stream = reopened.Blobs.OpenRead(id)!;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                Assert.Equal(id, BlobStore.IdOf(buffer.ToArray()));
            }

            check(faults, crash ? reopened : live, reopened, completed);
            if (!faults.Fired && faults.Steps.Count < failAt)
            {
                StepFaults.AssertEachChangeFlushedFirst(faults.Steps);
                return failAt - 1;
            }
        }
    }
}
