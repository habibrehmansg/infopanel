using System;
using System.Collections.Generic;
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
}

public sealed class ProfileDocumentOptions
{
    /// <summary>This engine run's instance id (<c>GET /api/session</c>): a commit naming another conflicts (<c>reason: "instance"</c>).</summary>
    public string Instance { get; init; } = Guid.NewGuid().ToString("N");

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
public sealed record DocumentState(Generation Generation, Document Document, bool Dirty, bool CanUndo, bool CanRedo);

/// <summary>A document's revision and state, read together (the <c>/ws</c> <c>doc</c> event, the profile list).</summary>
public readonly record struct DocumentStatus(long Rev, bool Dirty, bool CanUndo, bool CanRedo);

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

    /// <summary>Raised after every new revision and every save, outside the lock.</summary>
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
            lock (_lock) return new DocumentStatus(_current.Rev, DirtyCore, _history.CanUndo, _history.CanRedo);
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
            return new DocumentState(generation, document, DirtyCore, _history.CanUndo, _history.CanRedo);
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
    /// <c>expired</c>), and the ops against a tentative copy, all or none (409 listing every conflict, else 422 listing
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
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ops.TryGet(request.OpId, out var hash, out var stored))
            {
                return hash.AsSpan().SequenceEqual(prepared.Hash)
                    ? new CommitOutcome { Result = stored with { Replayed = true } }
                    : Invalid([new SpecError("/opId", "opId.reused", $"The opId '{request.OpId}' was already applied with a different body.")], "Invalid commit");
            }

            var current = _current;
            if (request.Instance != _options.Instance) return InstanceConflict(current.Rev);
            if (_ops.Expired(request.BaseRev))
            {
                return new CommitOutcome
                {
                    Expired = new ExpiredProblem
                    {
                        Title = "Expired",
                        Detail = $"The commit is based on revision {request.BaseRev}, older than the oldest commit whose opId is remembered: read the document and commit again with a new opId.",
                        Status = 409,
                        Code = "expired",
                        Rev = current.Rev,
                    },
                };
            }

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
            _ops.Add(request.OpId, prepared.Hash, result);
        }
        if (published is not null)
        {
            Raise(published, DocumentChange.Commit);
            _options.HistoryBudget.NotifyEvicted();
        }
        return new CommitOutcome { Result = result };
    }

    /// <summary>
    /// Goes back one step, as a new revision (<c>POST /api/profiles/{id}/undo</c>), when <paramref name="body"/>
    /// (<c>historyRequest</c>) names this instance and the document's current revision; see <see cref="Step"/>. With
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
    /// Hands the current generation to the persistence seam and makes it the saved one. Always writes, even when clean.
    /// The write runs outside the document's lock (commits go on meanwhile); saves are serialised, and with disposal
    /// too: a disposed document never saves.
    /// </summary>
    public CommitResult Save()
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
            }
            persistence.Save(generation);
            lock (_lock)
            {
                _saved = generation;
                _dirty = null;
                result = Result(_current.Rev, null, null);
            }
        }
        Raise(generation, DocumentChange.Saved);
        return result;
    }

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
    /// <c>instance</c> (409, <c>reason: "instance"</c>), the revision (409 when the document isn't at <c>baseRev</c>,
    /// listing what changed after it); then it acts, ending any merge. Nothing is done when a check fails.
    /// </summary>
    private CommitOutcome Step(JsonElement body, DocumentChange change)
    {
        var request = ReadHistoryRequest(body, out var errors);
        if (request is null) return Invalid(errors, "Invalid request");

        CommitResult result;
        Generation? published = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _current;
            if (request.Instance != _options.Instance) return InstanceConflict(current.Rev);
            if (current.Rev != request.BaseRev)
                return Conflict(current.Rev, ChangedAfter(current, request.BaseRev), $"The document changed after revision {request.BaseRev}.");

            _history.EndMerge();
            if (change == DocumentChange.Revert)
            {
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
        }
        if (published is not null)
        {
            Raise(published, change);
            _options.HistoryBudget.NotifyEvicted();
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

    private static CommitOutcome InstanceConflict(long rev) =>
        Conflict(rev, [new Conflict { Reason = "instance" }], "The engine restarted: start over from a fresh read.");

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
