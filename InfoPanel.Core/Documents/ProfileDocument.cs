using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>Where a document's saved generation goes: the engine's document store, or memory in tests.</summary>
public interface IDocumentPersistence
{
    /// <summary>Writes <paramref name="generation"/> as the document's saved version. Throws when it can't; the document then stays dirty.</summary>
    void Save(Generation generation);

    /// <summary>
    /// Keeps <paramref name="generation"/>, a dirty one, as the document's unsaved changes, made against the version
    /// last saved (or opened), so they survive a restart; replaces what was kept before. Throws when it can't.
    /// </summary>
    void Checkpoint(Generation generation);

    /// <summary>Removes what <see cref="Checkpoint"/> kept, when anything is; on disk before it returns. Throws when it can't.</summary>
    void ClearCheckpoint();
}

/// <summary>
/// A document's lifetime tag, <c>instance</c> (spec/docs/api.md, "A document's tag"; commit.md): opaque, compared for
/// equality only, and new for every document the engine creates or opens.
/// </summary>
public static class DocumentTag
{
    /// <summary>A new tag: 32 lower-case hex digits, random, never the same as another document's or a run's.</summary>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The 409 to a request naming a tag that isn't the profile's: one conflict, the whole request's,
    /// <c>reason: "instance"</c>. <paramref name="rev"/> is the document's current revision; null for a profile that
    /// can't be opened, which has none.
    /// </summary>
    public static ConflictProblem Conflict(long? rev) => new()
    {
        Title = "Conflict",
        Detail = "The request names another document: the engine restarted, or the profile was deleted and created again. Read it again and start over.",
        Status = 409,
        Code = "conflict",
        Rev = rev,
        Conflicts = [new Conflict { Reason = "instance" }],
    };
}

public sealed class ProfileDocumentOptions
{
    /// <summary>
    /// The document's lifetime tag (spec/docs/api.md, "A document's tag"): new for every document the engine creates or
    /// opens, so a profile deleted and created again under the same id, or opened again by a later run, has another. A
    /// commit, undo, redo or revert naming another conflicts (<c>reason: "instance"</c>).
    /// </summary>
    public string Instance { get; init; } = DocumentTag.New();

    /// <summary>The clock for undo coalescing.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public HistoryBudget HistoryBudget { get; init; } = HistoryBudget.Shared;

    /// <summary>Where <see cref="ProfileDocument.Save"/> writes; null: saving throws.</summary>
    public IDocumentPersistence? Persistence { get; init; }

    public SpecLimits Limits { get; init; } = SpecLimits.Default;

    public EngineSupport Support { get; init; } = EngineSupport.All;

    /// <summary>Undo steps kept per document (spec/docs/limits.md: the last 100).</summary>
    public int HistorySteps { get; init; } = 100;

    /// <summary>Bytes of undo steps kept per document (64 MiB); the byte cap wins over the count.</summary>
    public long HistoryBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Applied <c>opId</c>s remembered per document (spec/docs/commit.md: at least 1,000), the oldest forgotten first;
    /// the oldest remembered one's revision starts the retry window.
    /// </summary>
    public int RememberedOps { get; init; } = 1_000;
}

/// <summary>
/// The answer to a commit, undo, redo or revert: <see cref="Result"/> (200), <see cref="Conflict"/> or
/// <see cref="Expired"/> (409, the latter only for a commit) or <see cref="Invalid"/> (422).
/// </summary>
public sealed record CommitOutcome
{
    public CommitResult? Result { get; init; }
    public ConflictProblem? Conflict { get; init; }
    public ExpiredProblem? Expired { get; init; }
    public InvalidProblem? Invalid { get; init; }

    public int Status => Result is not null ? 200 : Conflict is not null || Expired is not null ? 409 : 422;
}

/// <summary>A consistent read of a document: its generation, the document with every <c>rev</c> member set, and its state.</summary>
public sealed record DocumentState(Generation Generation, Document Document, bool Dirty, bool CanUndo, bool CanRedo, DateTimeOffset? Recovered = null)
{
    /// <summary><see cref="Recovered"/> as the API writes it (<c>recovered</c>: UTC to the second); null when not set.</summary>
    public string? RecoveredText => RecoveryFile.TextOf(Recovered);
}

/// <summary>A document's revision and state, read together (the <c>/ws</c> <c>doc</c> event, the profile list).</summary>
public readonly record struct DocumentStatus(long Rev, bool Dirty, bool CanUndo, bool CanRedo, DateTimeOffset? Recovered = null)
{
    /// <summary><see cref="Recovered"/> as the API writes it (<c>recovered</c>: UTC to the second); null when not set.</summary>
    public string? RecoveredText => RecoveryFile.TextOf(Recovered);
}

public enum DocumentChange
{
    Commit,
    Undo,
    Redo,
    Revert,

    /// <summary>The document was saved: the revision is unchanged.</summary>
    Saved,

    /// <summary>
    /// The process's history budget evicted steps of this document: the revision is unchanged, and <c>canUndo</c> or
    /// <c>canRedo</c> may now be false.
    /// </summary>
    History,
}

public sealed class DocumentChangedEventArgs(Generation generation, DocumentChange change) : EventArgs
{
    /// <summary>The generation the change made. Events may arrive out of order across threads: read <see cref="ProfileDocument.Current"/> for the latest.</summary>
    public Generation Generation { get; } = generation;

    public DocumentChange Change { get; } = change;
}

/// <summary>
/// One open document (spec/docs/document.md, commit.md): the engine's in-memory copy, which owns revisions, undo, redo,
/// dirty state, save and revert.
/// <list type="bullet">
/// <item><see cref="Current"/> is an immutable <see cref="Generation"/>, read without a lock; every change (commit,
/// undo, redo, revert) runs under the document's lock as one transaction and publishes a new generation with the next
/// revision. A commit that changes nothing makes none.</item>
/// <item>Field revisions (<see cref="FieldRevisions"/>) record when each field group last changed; commits stamp what
/// their ops changed (and the position of every sibling a reorder passed), and undo, redo and revert stamp every field
/// group that differs between the two generations.</item>
/// <item>Dirty is exact: the saved and current generations are the same content, or equal item by item.</item>
/// <item><see cref="Changed"/> is raised after the lock is released.</item>
/// <item>Unsaved changes are kept across a restart through the persistence seam (<see cref="Checkpoint()"/>, called off
/// every request path), under the save lock, so a checkpoint is serialised with saves and disposal. Whenever a change
/// leaves the document clean, what was kept is removed before that change answers, so a change undone, reverted or saved
/// never comes back after a crash; a save rewrites it at once against the new saved version when commits came during
/// it. <see cref="Restore"/> applies kept changes at open.</item>
/// </list>
/// Lock order: save, then the document, then the history budget.
/// </summary>
public sealed class ProfileDocument : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ProfileDocument>();

    private const string HistoryRequestSchema = SpecSchemas.Api + "#/$defs/historyRequest";

    /// <summary>
    /// Reads a commit body at any depth, so that content nested too deeply is the validator's <c>schema</c> error, not
    /// unreadable JSON; reading is not recursive, and the size is checked first.
    /// </summary>
    private static readonly JsonDocumentOptions BodyOptions = new() { MaxDepth = int.MaxValue };

    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly ProfileDocumentOptions _options;
    private readonly FieldRevisions _revisions;
    private readonly History _history;
    private readonly OpLog _ops;
    private volatile Generation _current;
    private Generation _saved;
    private bool? _dirty;
    private bool _disposed;

    /// <summary>When the changes <see cref="Restore"/> applied were kept; null once saved or reverted. Under the lock.</summary>
    private DateTimeOffset? _recovered;

    /// <summary>
    /// Whether a checkpoint may be on disk: one restored or found at open, or one written or being written. Set under the
    /// save lock and the lock before a checkpoint is written, cleared under the save lock once one is removed; a change
    /// that leaves the document clean reads it under both, once a save or checkpoint under way is done, so neither is missed.
    /// </summary>
    private bool _checkpointOnDisk;

    /// <summary>
    /// The revision the checkpoint on disk holds, against the saved version, or the oldest it may hold (a replacement that
    /// failed leaves it or a newer one); 0 when none is known. Under the lock.
    /// </summary>
    private long _keptRev;

    /// <summary>
    /// A removal of what was kept failed (the change that asked for it still answered): <see cref="Checkpoint()"/> tries
    /// it again while the document is clean, and a checkpoint that replaces the file ends it. Under the lock.
    /// </summary>
    private bool _removalPending;

    /// <summary>
    /// The revisions of commits that left the document clean whose removal of what was kept hasn't finished: a retry of
    /// one waits for it before it answers. Under the lock.
    /// </summary>
    private readonly HashSet<long> _ending = [];

    /// <summary>A document whose saved version is <paramref name="saved"/> (also the current one).</summary>
    public ProfileDocument(Generation saved, ProfileDocumentOptions? options = null)
    {
        _options = options ?? new ProfileDocumentOptions();
        _current = saved;
        _saved = saved;
        _revisions = new FieldRevisions(saved);
        _history = new History(_options.Time, _options.HistoryBudget, _options.HistorySteps, _options.HistoryBytes, OnHistoryEvicted);
        _ops = new OpLog(_options.RememberedOps);
    }

    /// <summary>Opens a stored document at revision 1; no document when it is unsupported or invalid as a whole (the check says why).</summary>
    public static (ProfileDocument? Document, DocumentCheck Check) Open(JsonElement document, ProfileDocumentOptions? options = null)
    {
        options ??= new ProfileDocumentOptions();
        var load = Generation.Load(document, 1, options.Limits, options.Support);
        return (load.Generation is { } g ? new ProfileDocument(g, options) : null, load.Check);
    }

    /// <summary>
    /// Raised after every new revision and every save, outside the lock; and after a revert that takes no step but ends
    /// <see cref="Recovered"/> (<see cref="DocumentChange.Revert"/> with the current generation).
    /// </summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public string Id => _saved.Document.Id;

    public string Instance => _options.Instance;

    /// <summary>The latest generation; safe to read from any thread without a lock.</summary>
    public Generation Current => _current;

    /// <summary>The generation last saved (or opened).</summary>
    public Generation Saved
    {
        get { lock (_lock) return _saved; }
    }

    public long Rev => _current.Rev;

    /// <summary>
    /// When the unsaved changes restored at open (<see cref="Restore"/>) were kept, from the restore until the first
    /// save or revert; null otherwise. A client shows it only while the document is dirty.
    /// </summary>
    public DateTimeOffset? Recovered
    {
        get { lock (_lock) return _recovered; }
    }

    /// <summary>
    /// Applies unsaved changes kept before the engine stopped (<paramref name="recovered"/>, made against this document's
    /// saved version and kept at <paramref name="written"/>), as the document opens and before anything reads it: one
    /// step on top of the saved version, published as revision 2 with every field group it changed stamped, recorded as
    /// one undo step; the document is then dirty and <see cref="Recovered"/> is <paramref name="written"/>. The persistence
    /// holds them already, so nothing is written again for that revision. Content equal to the saved version makes no step
    /// and removes what was kept; false then.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document has changed since it opened.</exception>
    public bool Restore(Generation recovered, DateTimeOffset written)
    {
        bool restored;
        lock (_saveLock)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!ReferenceEquals(_current, _saved) || _history.CanUndo || _history.CanRedo)
                    throw new InvalidOperationException("Kept changes are restored only as the document opens.");
                _checkpointOnDisk = true;
                if (!ChangeSet.SameContent(_saved.Content, recovered.Content))
                {
                    var before = _current.Content;
                    var changes = ChangeSet.Diff(before, recovered.Content);
                    var published = Publish(recovered.Content, changes);
                    _history.Record(before, recovered.Content, changes.Bytes, null);
                    _history.EndMerge();
                    _recovered = written;
                    _keptRev = published.Rev;
                }
                restored = _keptRev != 0;
            }

            if (!restored)
            {
                ClearCheckpointCore();
                return false;
            }
        }
        _options.HistoryBudget.NotifyEvicted();
        return true;
    }

    /// <summary>
    /// Keeps the current generation through the persistence seam when the document is dirty and that revision isn't
    /// kept yet, under the save lock: never during a save or after disposal. A clean document is left alone (the change
    /// that made it clean removed what was kept; a file found at open that couldn't be used stays), unless that removal
    /// failed (<see cref="RemovalPending"/>): it is tried again. True when it wrote or removed one. Called off every
    /// request path (the workspace's recovery scheduler, and its disposal).
    /// </summary>
    /// <exception cref="IOException">The persistence couldn't keep it, or remove it; it is tried again at the next call.</exception>
    public bool Checkpoint()
    {
        if (_options.Persistence is not { } persistence)
            return false;
        lock (_saveLock)
        {
            Generation? generation = null;
            lock (_lock)
            {
                if (_disposed)
                    return false;
                if (DirtyCore)
                {
                    if (_keptRev == _current.Rev)
                        return false;
                    generation = _current;
                    // Before the write: a change that leaves the document clean from now on removes what this writes.
                    // _keptRev changes only once the write succeeds: one that fails leaves the file it replaces, or a
                    // newer one, never an older one.
                    _checkpointOnDisk = true;
                }
                else if (!_removalPending)
                    return false;
            }

            if (generation is null)
            {
                RemoveCheckpoint();
                return true;
            }
            persistence.Checkpoint(generation);
            lock (_lock)
            {
                _keptRev = generation.Rev;
                _removalPending = false;
            }
            return true;
        }
    }

    /// <summary>A removal of what was kept failed, and is tried again (<see cref="Checkpoint()"/>) while the document is clean.</summary>
    internal bool RemovalPending
    {
        get { lock (_lock) return _removalPending; }
    }

    /// <summary>
    /// After a change that left the document clean at revision <paramref name="cleanRev"/> (0: none): removes what was
    /// kept before that revision, before the change answers. A checkpoint of that revision or a later one (made since,
    /// or by a save that made it dirty) is left: it holds changes the clean revision didn't discard. It waits for
    /// a save or checkpoint under way even when nothing was kept as the change was made: a save that started before it
    /// keeps the change's content against the version it saved, and the change answers only once that is on disk.
    /// </summary>
    private void EndCheckpoint(long cleanRev)
    {
        if (cleanRev == 0)
            return;
        if (!_saveLock.TryEnter())
        {
            WaitingForSave?.Invoke();
            _saveLock.Enter();
        }
        try
        {
            lock (_lock)
            {
                if (_disposed || !_checkpointOnDisk || _keptRev >= cleanRev)
                    return;
            }
            ClearCheckpointCore();
        }
        finally
        {
            lock (_lock)
                _ending.Remove(cleanRev);
            _saveLock.Exit();
        }
    }

    /// <summary>Tests: called on a thread that finds the save lock held by another as it ends a change that left the document clean, before it waits for it.</summary>
    internal Action? WaitingForSave;

    /// <summary>
    /// Removes what was kept, under the save lock; a failure is logged, and the removal is pending: tried again on its own
    /// (<see cref="Checkpoint()"/>) until it succeeds or a checkpoint replaces the file.
    /// </summary>
    private void ClearCheckpointCore()
    {
        try
        {
            RemoveCheckpoint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not remove the unsaved changes kept for document {DocumentId}; tried again on its own, and at the orderly stop", Id);
        }
    }

    /// <summary>Removes what was kept, under the save lock; when it can't, the removal is pending and the failure thrown.</summary>
    private void RemoveCheckpoint()
    {
        if (_options.Persistence is not { } persistence)
            return;
        try
        {
            persistence.ClearCheckpoint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
                _removalPending = true;
            throw;
        }

        lock (_lock)
        {
            _checkpointOnDisk = false;
            _keptRev = 0;
            _removalPending = false;
        }
    }

    /// <summary>
    /// Tells the document that kept changes it didn't restore are on disk (a file that couldn't be used, or one whose
    /// removal failed at open): a save, or a change that leaves the document clean, removes them.
    /// </summary>
    internal void CheckpointFound()
    {
        lock (_lock)
            _checkpointOnDisk = true;
    }

    public bool Dirty
    {
        get { lock (_lock) return DirtyCore; }
    }

    public bool CanUndo
    {
        get { lock (_lock) return _history.CanUndo; }
    }

    public bool CanRedo
    {
        get { lock (_lock) return _history.CanRedo; }
    }

    /// <summary>The revision, dirty, canUndo and canRedo of one moment, without building the document as <see cref="Read"/> does.</summary>
    public DocumentStatus Status
    {
        get
        {
            lock (_lock) return new DocumentStatus(_current.Rev, DirtyCore, _history.CanUndo, _history.CanRedo, _recovered);
        }
    }

    /// <summary>Undo and redo steps held, and the bytes charged to them (for tests and diagnostics).</summary>
    public (int Undo, int Redo, long Bytes) HistorySize
    {
        get
        {
            lock (_lock) return (_history.UndoCount, _history.RedoCount, _history.Bytes);
        }
    }

    /// <summary>The revision at which each field group last changed (read under the lock; for tests and diagnostics).</summary>
    public T ReadRevisions<T>(Func<FieldRevisions, T> read)
    {
        lock (_lock) return read(_revisions);
    }

    /// <summary>The current document with every <c>rev</c> member set (the document's, the profile's and each item's), and its state.</summary>
    public DocumentState Read()
    {
        lock (_lock)
        {
            var generation = _current;
            var doc = generation.Document;
            Item WithRev(Item item) => item is GroupItem g
                ? g with { Rev = _revisions.ItemRev(g.Id), Children = g.Children.Select(WithRev).ToArray() }
                : item with { Rev = _revisions.ItemRev(item.Id) };
            var document = doc with
            {
                Rev = generation.Rev,
                Profile = doc.Profile with { Rev = _revisions.ProfileRev },
                Items = doc.Items.Select(WithRev).ToArray(),
            };
            return new DocumentState(generation, document, DirtyCore, _history.CanUndo, _history.CanRedo, _recovered);
        }
    }

    /// <summary>Applies a commit request built in code: serialized and applied as <see cref="Commit(ReadOnlyMemory{byte})"/>.</summary>
    public CommitOutcome Commit(CommitRequest request) => Commit(JsonSerializer.SerializeToUtf8Bytes(request, SpecJson.Options));

    /// <summary>
    /// Applies a commit body as received (<c>POST /api/profiles/{id}/commit</c>: the endpoint passes the request's
    /// bytes here): its size, then the JSON (<c>json</c> when it isn't), then <see cref="Commit(JsonElement)"/>'s
    /// checks, with the <c>opId</c> digest over every byte of <paramref name="body"/>, whitespace around the value
    /// included, so a retry is the same body byte for byte.
    /// </summary>
    public CommitOutcome Commit(ReadOnlyMemory<byte> body)
    {
        var limits = _options.Limits;
        if (body.Length > limits.CommitBytes) return Invalid([DocumentValidator.Over("", "commitBytes", body.Length, limits.CommitBytes)], "Invalid commit");
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body, BodyOptions);
        }
        catch (JsonException ex)
        {
            return Invalid([new SpecError("", "json", $"The body isn't JSON: {ex.Message}")], "Invalid commit");
        }
        // The request read from the body holds copies of its JSON values, so nothing refers to the document afterwards.
        using (json) return Commit(json.RootElement, body.Span);
    }

    /// <summary>
    /// Applies a commit body already parsed (tests, and code that builds one), as spec/docs/commit.md says: checked on
    /// its own (422) outside the lock, then under it the <c>opId</c> (a retry returns the stored result with
    /// <c>replayed: true</c>; another body is 422 <c>opId.reused</c>), the <c>instance</c> (409), the retry window (409
    /// <c>expired</c>), every <c>baseRev</c> newer than the document (422 <c>baseRev.future</c>), and the ops against a
    /// tentative copy, all or none (409 listing every conflict, else 422 listing
    /// every error). Applied: one new revision and one undo step (or merged into the previous one by its
    /// <c>coalesceKey</c>); a commit that changes nothing records nothing. The <c>opId</c> digest is over the value's
    /// own text, without whitespace around it: an endpoint uses <see cref="Commit(ReadOnlyMemory{byte})"/>.
    /// </summary>
    public CommitOutcome Commit(JsonElement body) => Commit(body, JsonMarshal.GetRawUtf8Value(body));

    private CommitOutcome Commit(JsonElement body, ReadOnlySpan<byte> bytes)
    {
        var prepared = CommitApplier.Prepare(body, bytes, _options.Limits, _options.Support, out var errors);
        if (prepared is null) return Invalid(errors, "Invalid commit");
        var request = prepared.Request;

        CommitResult result;
        Generation? published;
        long endCheckpoint;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ops.TryGet(request.OpId, out var hash, out var stored))
            {
                if (!hash.AsSpan().SequenceEqual(prepared.Hash))
                    return Invalid([new SpecError("/opId", "opId.reused", $"The opId '{request.OpId}' was already applied with a different body.")], "Invalid commit");
                // A retry answers as the commit did, and only once what that commit removes is gone: it waits for the
                // removal, or makes it.
                published = null;
                result = stored with { Replayed = true };
                endCheckpoint = _ending.Contains(stored.Rev) ? stored.Rev : 0;
            }
            else
            {
                var current = _current;
                if (request.Instance != _options.Instance) return InstanceConflict(current.Rev);
                if (_ops.Expired(request.BaseRev, current.Rev))
                {
                    return new CommitOutcome
                    {
                        Expired = new ExpiredProblem
                        {
                            Title = "Expired",
                            Detail = $"The commit is based on revision {request.BaseRev}, and the engine has forgotten commits that old: read the document and commit again with a new opId.",
                            Status = 409,
                            Code = "expired",
                            Rev = current.Rev,
                        },
                    };
                }
                // After the opId (a retry still replays) and the window (which a baseRev newer than the document never meets).
                if (CommitApplier.FutureBaseRevs(request, current.Rev) is { Count: > 0 } future) return Invalid(future, "Invalid commit");

                var applied = CommitApplier.Apply(current.Content, _revisions, prepared);
                if (applied.Conflicts.Count > 0) return Conflict(current.Rev, applied.Conflicts, null);
                if (applied.Errors.Count > 0) return Invalid(applied.Errors, "Invalid commit");

                var warnings = prepared.Warnings.Count > 0 ? prepared.Warnings : null;
                if (applied.Content is not { } content)
                {
                    published = null;
                    result = Result(current.Rev, null, warnings);
                }
                else
                {
                    published = Publish(content, applied.Changes!);
                    _history.Record(current.Content, content, applied.Changes!.Bytes, request.CoalesceKey);
                    result = Result(published.Rev, applied.Changes, warnings);
                }
                _ops.Add(request.OpId, prepared.Hash, request.BaseRev, result);
                // Whether or not anything is kept now: a save under way may keep this content's predecessor.
                endCheckpoint = published is not null && !result.Dirty ? published.Rev : 0;
                if (endCheckpoint != 0)
                    _ending.Add(endCheckpoint);
            }
        }
        EndCheckpoint(endCheckpoint);
        if (published is not null)
        {
            Raise(published, DocumentChange.Commit);
            _options.HistoryBudget.NotifyEvicted();
        }
        return new CommitOutcome { Result = result };
    }

    /// <summary>
    /// Goes back one step, as a new revision (<c>POST /api/profiles/{id}/undo</c>), when <paramref name="body"/>
    /// (<c>historyRequest</c>) names this document's tag and its current revision; see <see cref="Step"/>. With
    /// nothing to undo, answers with the unchanged revision.
    /// </summary>
    public CommitOutcome Undo(JsonElement body) => Step(body, DocumentChange.Undo);

    public CommitOutcome Undo(HistoryRequest request) => Undo(SpecJson.Write(request));

    /// <summary>Goes forward one step, as a new revision, guarded as <see cref="Undo(JsonElement)"/>. With nothing to redo, answers with the unchanged revision.</summary>
    public CommitOutcome Redo(JsonElement body) => Step(body, DocumentChange.Redo);

    public CommitOutcome Redo(HistoryRequest request) => Redo(SpecJson.Write(request));

    /// <summary>Goes back to the saved version as one undoable step, guarded as <see cref="Undo(JsonElement)"/>. A clean document is left alone.</summary>
    public CommitOutcome Revert(JsonElement body) => Step(body, DocumentChange.Revert);

    public CommitOutcome Revert(HistoryRequest request) => Revert(SpecJson.Write(request));

    /// <summary>
    /// Saves the document as it is when the save starts (spec/docs/commit.md, "Undo, redo, revert and save"), unguarded:
    /// hands the current generation to the persistence seam and makes it the saved one. Always writes, even when clean.
    /// The write runs outside the document's lock (commits go on meanwhile, and aren't part of what it saves); saves
    /// are serialised, and with disposal too: a disposed document never saves. The answer is the state once the write
    /// is done: the current revision, and dirty when the document changed during the write.
    /// </summary>
    public CommitResult Save() => SaveCore(null).Result!;

    /// <summary>
    /// A save that names the document's tag and the revision it saves (<paramref name="body"/>, <c>historyRequest</c>):
    /// checked as an undo is (the body, 422; the <c>instance</c>, 409; a <c>baseRev</c> newer than the document, 422
    /// <c>baseRev.future</c>; the revision, 409 listing what changed after it), under the document's lock at the moment
    /// the generation to save is taken, so it saves exactly <c>baseRev</c> of this tag, or nothing. It then saves as
    /// <see cref="Save()"/> does.
    /// </summary>
    public CommitOutcome Save(JsonElement body)
    {
        var request = ReadHistoryRequest(body, out var errors);
        return request is null ? Invalid(errors, "Invalid request") : SaveCore(request);
    }

    private CommitOutcome SaveCore(HistoryRequest? guard)
    {
        var persistence = _options.Persistence ?? throw new InvalidOperationException("This document has nowhere to save to.");
        CommitResult result;
        Generation generation;
        lock (_saveLock)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = _current;
                if (guard is not null)
                {
                    if (guard.Instance != _options.Instance) return InstanceConflict(generation.Rev);
                    if (guard.BaseRev > generation.Rev)
                        return Invalid([CommitApplier.FutureBaseRev("/baseRev", guard.BaseRev, generation.Rev)], "Invalid request");
                    if (generation.Rev != guard.BaseRev)
                        return Conflict(generation.Rev, ChangedAfter(generation, guard.BaseRev), $"The document changed after revision {guard.BaseRev}.");
                }
            }
            persistence.Save(generation);
            Generation current;
            bool clear;
            lock (_lock)
            {
                _saved = generation;
                _dirty = null;
                _recovered = null;
                current = _current;
                result = Result(current.Rev, null, null);
                clear = !result.Dirty && _checkpointOnDisk;
                if (result.Dirty)
                {
                    // What was kept was made against the version this save replaced: kept again at once, against this one.
                    _checkpointOnDisk = true;
                    _keptRev = 0;
                }
            }

            if (clear)
                ClearCheckpointCore();
            else if (result.Dirty)
            {
                try
                {
                    persistence.Checkpoint(current);
                    lock (_lock)
                    {
                        _keptRev = current.Rev;
                        _removalPending = false;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Kept again at the next checkpoint; until then what is on disk was made against the earlier save,
                    // which is never restored over this one.
                    Logger.Warning(ex, "Saved document {DocumentId}, but could not keep the changes made during the save; they are kept at its next checkpoint", Id);
                }
            }
        }
        Raise(generation, DocumentChange.Saved);
        return new CommitOutcome { Result = result };
    }

    /// <summary>A guarded save built in code: serialized and checked as <see cref="Save(JsonElement)"/>.</summary>
    public CommitOutcome Save(HistoryRequest request) => Save(SpecJson.Write(request));

    /// <summary>
    /// Releases the undo history from the process budget. The document can't change or save afterwards; a save under
    /// way finishes first.
    /// </summary>
    public void Dispose()
    {
        lock (_saveLock)
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _history.Clear();
        }
    }

    /// <summary>
    /// Undo, redo or revert (spec/docs/commit.md, "Undo, redo, revert and save"), checked in order: the body (422), the
    /// <c>instance</c> (409, <c>reason: "instance"</c>), a <c>baseRev</c> newer than the document (422
    /// <c>baseRev.future</c>), the revision (409 when the document isn't at <c>baseRev</c>, listing what changed after
    /// it); then it acts, ending any merge even when it takes no step. Nothing is done when a check fails.
    /// </summary>
    private CommitOutcome Step(JsonElement body, DocumentChange change)
    {
        var request = ReadHistoryRequest(body, out var errors);
        if (request is null) return Invalid(errors, "Invalid request");

        CommitResult result;
        Generation? published = null;
        long endCheckpoint;
        var recoveredEnded = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _current;
            if (request.Instance != _options.Instance) return InstanceConflict(current.Rev);
            if (request.BaseRev > current.Rev)
                return Invalid([CommitApplier.FutureBaseRev("/baseRev", request.BaseRev, current.Rev)], "Invalid request");
            if (current.Rev != request.BaseRev)
                return Conflict(current.Rev, ChangedAfter(current, request.BaseRev), $"The document changed after revision {request.BaseRev}.");

            _history.EndMerge();
            if (change == DocumentChange.Revert)
            {
                recoveredEnded = _recovered is not null;
                _recovered = null;
                if (!DirtyCore) result = Result(current.Rev, null, null);
                else
                {
                    var changes = ChangeSet.Diff(current.Content, _saved.Content);
                    published = Publish(_saved.Content, changes);
                    _history.Record(current.Content, _saved.Content, changes.Bytes, null);
                    result = Result(published.Rev, changes, null);
                }
            }
            else
            {
                var target = change == DocumentChange.Undo ? _history.PeekUndo() : _history.PeekRedo();
                if (target is null) result = Result(current.Rev, null, null);
                else
                {
                    var changes = ChangeSet.Diff(current.Content, target);
                    if (change == DocumentChange.Undo) _history.Undo(current.Content, changes.Bytes);
                    else _history.Redo(current.Content, changes.Bytes);
                    published = Publish(target, changes);
                    result = Result(published.Rev, changes, null);
                }
            }
            // Whether or not anything is kept now: a save under way may keep this content's predecessor.
            endCheckpoint = published is not null && !result.Dirty ? published.Rev : 0;
        }
        EndCheckpoint(endCheckpoint);
        if (published is not null)
        {
            Raise(published, change);
            _options.HistoryBudget.NotifyEvicted();
        }
        else if (recoveredEnded)
        {
            // No step, but the state changed (recovered ended): told as a revert of the current revision.
            Raise(_current, change);
        }
        return new CommitOutcome { Result = result };
    }

    private static HistoryRequest? ReadHistoryRequest(JsonElement body, out IReadOnlyList<SpecError> errors)
    {
        var schema = SpecSchemas.StructureOnly.Validate(body, HistoryRequestSchema);
        if (!schema.IsValid)
        {
            errors = ValidationText.FromSchema(schema).ToList();
            return null;
        }
        errors = [];
        return SpecJson.Read<HistoryRequest>(body);
    }

    /// <summary>
    /// A <c>changed</c> conflict, without <c>op</c>, for each item in <paramref name="current"/> (document order) and for
    /// the profile (no <c>id</c>) with a field group whose last change is after <paramref name="baseRev"/>. Under the lock.
    /// </summary>
    private List<Conflict> ChangedAfter(Generation current, long baseRev)
    {
        var conflicts = new List<Conflict>();
        var pending = new Stack<Item>();
        for (var i = current.Document.Items.Count - 1; i >= 0; i--) pending.Push(current.Document.Items[i]);
        while (pending.TryPop(out var item))
        {
            var changed = _revisions.ChangedAfter(item.Id, FieldRevisions.Tracked(item), baseRev);
            if (changed != ItemField.None)
                conflicts.Add(new Conflict { Id = item.Id, Reason = "changed", Rev = _revisions.ItemRev(item.Id), Fields = FieldRevisions.Names(changed) });
            if (item is GroupItem g)
                for (var i = g.Children.Count - 1; i >= 0; i--) pending.Push(g.Children[i]);
        }
        var profile = _revisions.ChangedAfter(ProfileField.All, baseRev);
        if (profile != ProfileField.None)
            conflicts.Add(new Conflict { Reason = "changed", Rev = _revisions.ProfileRev, Fields = FieldRevisions.Names(profile) });
        return conflicts;
    }

    /// <summary>The next revision over <paramref name="content"/>, with <paramref name="changes"/> stamped. Under the lock.</summary>
    private Generation Publish(GenerationContent content, ChangeSet changes)
    {
        var generation = new Generation(_current.Rev + 1, content);
        _revisions.Apply(changes, generation.Rev);
        _dirty = null;
        _current = generation;
        return generation;
    }

    private bool DirtyCore => _dirty ??= !ChangeSet.SameContent(_saved.Content, _current.Content);

    private CommitResult Result(long rev, ChangeSet? changes, IReadOnlyList<CommitWarning>? warnings)
    {
        var items = new Dictionary<string, long>(StringComparer.Ordinal);
        if (changes is not null)
            foreach (var id in changes.Items.Keys) items[id] = rev;
        return new CommitResult
        {
            Rev = rev,
            Items = items,
            Removed = changes is { Removed.Count: > 0 } ? changes.Removed.ToArray() : null,
            ProfileRev = changes is { Profile: not ProfileField.None } ? rev : null,
            Dirty = DirtyCore,
            CanUndo = _history.CanUndo,
            CanRedo = _history.CanRedo,
            Warnings = warnings,
        };
    }

    private static CommitOutcome Invalid(IReadOnlyList<SpecError> errors, string title) => new()
    {
        Invalid = new InvalidProblem
        {
            Title = title,
            Status = 422,
            Code = "invalid",
            Errors = ValidationText.Cap(errors).Select(e => new ValidationErrorBody { Path = e.Path, Code = e.Code, Message = e.Message }).ToArray(),
        },
    };

    private static CommitOutcome InstanceConflict(long rev) => new() { Conflict = DocumentTag.Conflict(rev) };

    private static CommitOutcome Conflict(long rev, IReadOnlyList<Conflict> conflicts, string? detail) => new()
    {
        Conflict = new ConflictProblem { Title = "Conflict", Detail = detail, Status = 409, Code = "conflict", Rev = rev, Conflicts = conflicts.ToArray() },
    };

    /// <summary>
    /// The budget evicted steps of this document (told outside every lock): its state may have changed. A document
    /// disposed since isn't told (its notice is cancelled on disposal, and one already being told is skipped here).
    /// </summary>
    private void OnHistoryEvicted()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }
        Raise(_current, DocumentChange.History);
    }

    private void Raise(Generation generation, DocumentChange change)
    {
        var handlers = Changed;
        if (handlers is null) return;
        var args = new DocumentChangedEventArgs(generation, change);
        foreach (EventHandler<DocumentChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "A Changed handler of document {DocumentId} failed", Id);
            }
        }
    }
}
