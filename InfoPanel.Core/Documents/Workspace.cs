using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using InfoPanel.Images;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>What every document of a workspace shares.</summary>
public sealed class WorkspaceOptions
{
    /// <summary>
    /// This engine run's instance id (<c>GET /api/session</c>, the preview socket's <c>hello</c>). Each document has a
    /// lifetime tag of its own (<see cref="DocumentTag"/>).
    /// </summary>
    public string Instance { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The clock for undo coalescing.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public HistoryBudget HistoryBudget { get; init; } = HistoryBudget.Shared;

    /// <summary>The limits documents are checked with; the scene runtime that draws them must use the same.</summary>
    public SpecLimits Limits { get; init; } = SpecLimits.Default;

    /// <summary>
    /// What the engine reads beyond the base of each format; the scene runtime must use the same. The engine has no
    /// media providers configured, so it reads the base only.
    /// </summary>
    public EngineSupport Support { get; init; } = EngineSupport.Base;

    /// <summary>
    /// Where the workspace logs: the engine's own logger, so its lines reach the log of its own run even where a process
    /// runs more than one engine (the host's tests); the process's logger when null.
    /// </summary>
    public ILogger? Logger { get; init; }
}

/// <summary>Raised when profiles are created or deleted. <see cref="Version"/> goes up by one each time.</summary>
public sealed class ProfilesChangedEventArgs(long version) : EventArgs
{
    public long Version { get; } = version;
}

/// <summary>
/// What a request on a profile finds (spec/docs/api.md): no profile (<see cref="Found"/> false), a profile that can't
/// be opened (<see cref="Problem"/>, 409), or its open <see cref="Document"/>.
/// </summary>
public sealed record ProfileLookup(bool Found, ProfileDocument? Document, ProfileStatusProblem? Problem)
{
    public static ProfileLookup NotFound { get; } = new(false, null, null);
}

/// <summary>
/// The answer to a create or duplicate: <see cref="Entry"/> (201), <see cref="Exists"/> (409), <see cref="Invalid"/>
/// (422), or, for a duplicate, a source that isn't there (<see cref="NotFound"/>, 404) or can't be opened
/// (<see cref="StatusProblem"/>, 409).
/// </summary>
public sealed record CreateOutcome
{
    public ProfileEntry? Entry { get; init; }
    public ExistsProblem? Exists { get; init; }
    public InvalidProblem? Invalid { get; init; }
    public ProfileStatusProblem? StatusProblem { get; init; }
    public bool NotFound { get; init; }

    public int Status => Entry is not null ? 201 : NotFound ? 404 : Exists is not null || StatusProblem is not null ? 409 : 422;
}

/// <summary>
/// The answer to a <c>DELETE</c>: the profile deleted (<see cref="Deleted"/>, 204), no such profile (<see cref="Found"/>
/// false, 404), or a tag that isn't the profile's (<see cref="Conflict"/>, 409 <c>instance</c>), nothing deleted.
/// </summary>
public sealed record DeleteOutcome(bool Found, ConflictProblem? Conflict)
{
    public static DeleteOutcome NotFound { get; } = new(false, null);

    public bool Deleted => Found && Conflict is null;
}

/// <summary>A stored blob and what the engine found in it (<c>POST /api/blobs</c>); <see cref="Created"/> when it was new.</summary>
public sealed record StoredBlob(BlobInfo Info, bool Created);

/// <summary>
/// The engine's profiles (spec/docs/api.md): the 2.0 document store, the blob store, and the open documents.
/// <list type="bullet">
/// <item><b>Start-up</b> (<see cref="Start"/>): both stores opened, the blob sweep run, then the first change: the
/// example document on the first run (the store didn't exist before). Step-1 data in the folder is left untouched and
/// unread, with one warning.</item>
/// <item><b>Folders are ids.</b> A profile's id is its folder's name. A stored document whose own <c>id</c> differs is
/// opened under the folder's id (logged), and saved with it.</item>
/// <item><b>Opening.</b> A document is opened the first time anything reads it (the profile list included) and stays
/// open until it is deleted or the workspace is disposed, which disposes it. A profile that can't be opened
/// (<c>unsupported</c>, <c>unreadable</c>) is checked once per run and never rewritten.</item>
/// <item><b>Tags.</b> Every profile gets a new lifetime tag (<see cref="DocumentTag"/>, its entries' and its document's
/// <c>instance</c>) when it is created or first read in a run, one that can't be opened too, and keeps it until it is
/// deleted. Its changes and its delete name it, so a request built on a profile that was deleted and created again
/// under the same id is refused instead of acting on the new one.</item>
/// <item><b>One lifetime per document.</b> Everything that begins or ends a profile runs under the workspace lock, as
/// one step: storing a new profile with publishing its open document, opening a stored one, and deleting one from the
/// store with removing and disposing its document. So nothing opens a profile before its document is published, a
/// <see cref="ProfileDocument"/> is the profile exactly as long as it is in its slot, and it is disposed (any save of it
/// under way finished, none after) before a profile with the same id can be created. Whatever acts on a document later
/// (a render loop, a <c>/ws</c> subscription, a desktop window) holds the instance it found, and treats it as deleted
/// once <see cref="IsOpen"/> is false or it throws <see cref="ObjectDisposedException"/>, never acting on a newer
/// profile of the same id in its place.</item>
/// <item>Lock order: the workspace, then a document's save lock, then the stores'; <see cref="ProfilesChanged"/> and
/// <see cref="BlobStored"/> are raised outside the lock.</item>
/// </list>
/// </summary>
public sealed class Workspace : IDisposable
{

    /// <summary>The largest canvas side a document may have (the document format's limit for <c>width</c> and <c>height</c>).</summary>
    public const int MaxDimension = 16384;

    private const string CreateEmptySchema = SpecSchemas.Api + "#/$defs/createProfileEmpty";
    private const string IdOnlySchema = SpecSchemas.Api + "#/$defs/duplicateProfileRequest";
    private const string DocumentIdSchema = SpecSchemas.Common + "#/$defs/documentId";
    private const string ExampleResourcePrefix = "InfoPanel.Documents.Examples.";

    private readonly WorkspaceOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private long _version;
    private bool _disposed;

    public Workspace(DocumentStore documents, BlobStore blobs, WorkspaceOptions? options = null)
    {
        Documents = documents;
        Blobs = blobs;
        _options = options ?? new WorkspaceOptions();
        Logger = _options.Logger ?? Log.ForContext<Workspace>();
    }

    private ILogger Logger { get; }

    public DocumentStore Documents { get; }

    public BlobStore Blobs { get; }

    public string Instance => _options.Instance;

    public SpecLimits Limits => _options.Limits;

    public EngineSupport Support => _options.Support;

    /// <summary>How many times the profile list changed (<see cref="ProfilesChangedEventArgs.Version"/> of the latest).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after a profile is created, duplicated or deleted.</summary>
    public event EventHandler<ProfilesChangedEventArgs>? ProfilesChanged;

    /// <summary>Raised with a blob's id after <see cref="PutBlob"/> stored it (new or again), so a failed lookup of it can be retried.</summary>
    public event Action<string>? BlobStored;

    /// <summary>Tests: called with the profile's id while a create or delete holds the workspace lock, after the store changed.</summary>
    internal Action<string>? StoreChanged;

    /// <summary>Tests: called with the profile's id while an export holds the workspace lock, after it found the profile.</summary>
    internal Action<string>? ExportFound;

    /// <summary>Tests: called on a thread that finds the workspace lock held by another, before it waits for it.</summary>
    internal Action? WaitingForLock;

    /// <summary>
    /// Opens the stores in <paramref name="paths"/>, sweeps the blobs, and on the first run (no profile order file and no
    /// profile folder before) stores the example documents. Logs once when the folder holds step-1 data.
    /// </summary>
    /// <exception cref="IOException">A store can't be opened.</exception>
    public static Workspace Start(DataPaths paths, WorkspaceOptions? options, DateTimeOffset now)
    {
        options ??= new WorkspaceOptions();
        var logger = options.Logger ?? Log.ForContext<Workspace>();
        var firstRun = !File.Exists(paths.ProfileOrderFile) && !HasProfileFolders(paths);
        if (HasStep1Data(paths))
        {
            logger.Warning("The data folder {DataDir} holds step-1 profiles (profiles.xml, profiles/*.xml, assets/); this engine doesn't read them and leaves them untouched",
                paths.Root);
        }

        var documents = new DocumentStore(paths, options.Limits);
        var blobs = new BlobStore(paths, options.Limits);
        documents.Open();
        blobs.Open();
        var sweep = BlobSweep.Run(documents, blobs, now);
        if (sweep.SkippedBecause is { } skipped)
            logger.Warning("Kept every stored blob: {Reason}", skipped);
        else if (sweep.Deleted > 0)
            logger.Information("Deleted {Deleted} of {Blobs} stored blobs that no document refers to", sweep.Deleted, sweep.Blobs);

        var workspace = new Workspace(documents, blobs, options);
        if (firstRun)
            workspace.StoreExamples();
        return workspace;
    }

    private static bool HasProfileFolders(DataPaths paths) =>
        Directory.Exists(paths.ProfilesDirectory)
        && Directory.EnumerateDirectories(paths.ProfilesDirectory).Any(d => DocumentStore.IsValidId(Path.GetFileName(d)));

    private static bool HasStep1Data(DataPaths paths) =>
        File.Exists(paths.ProfilesFile) || Directory.Exists(paths.AssetsDirectory)
        || (Directory.Exists(paths.ProfilesDirectory) && Directory.EnumerateFiles(paths.ProfilesDirectory, "*.xml").Any());

    /// <summary>The example documents embedded in Core (<c>Documents/Examples/*.document.json</c>), by file name.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> ExampleDocuments()
    {
        var assembly = typeof(Workspace).Assembly;
        var result = new List<(string, byte[])>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ExampleResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            result.Add((name[ExampleResourcePrefix.Length..], copy.ToArray()));
        }
        return result;
    }

    private void StoreExamples()
    {
        foreach (var (name, bytes) in ExampleDocuments())
        {
            using var json = JsonDocument.Parse(bytes, SpecJson.DocumentOptions);
            var load = Generation.Load(json.RootElement, 1, Limits, Support);
            if (load.Generation is not { } generation)
            {
                Logger.Error("The example document {Name} doesn't load: {Errors}", name, string.Join("; ", load.Check.Errors.Select(e => $"{e.Path} {e.Code}")));
                continue;
            }

            var id = generation.Document.Id;
            try
            {
                Documents.Create(id, Serialize(generation));
                Logger.Information("First run: created the example profile {ProfileName} ({ProfileId})", generation.Document.Profile.Name, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Logger.Error(ex, "Could not create the example profile {ProfileId}", id);
            }
        }
    }

    // ---- reading ---------------------------------------------------------------------------------------------------

    /// <summary>Every profile, in display order.</summary>
    public IReadOnlyList<ProfileEntry> List()
    {
        var result = new List<ProfileEntry>();
        foreach (var id in Documents.List())
        {
            if (Entry(id) is { } entry)
                result.Add(entry);
        }
        return result;
    }

    /// <summary>The profile's list entry, or null when there is no such profile.</summary>
    public ProfileEntry? Entry(string id)
    {
        var slot = GetSlot(id);
        return slot switch
        {
            null => null,
            { Document: { } doc } => EntryOf(doc),
            _ => slot.Entry,
        };
    }

    /// <summary>What a request on the profile finds: nothing, a profile that can't be opened, or its open document.</summary>
    public ProfileLookup Find(string id)
    {
        var slot = GetSlot(id);
        if (slot is null)
            return ProfileLookup.NotFound;
        if (slot.Document is { } doc)
            return new ProfileLookup(true, doc, null);
        var entry = slot.Entry!;
        return new ProfileLookup(true, null, new ProfileStatusProblem
        {
            Title = "Profile can't be opened",
            Detail = $"Profile '{id}' is {entry.Status}: {entry.Problem!.Message}",
            Status = 409,
            Code = entry.Status,
            Problem = entry.Problem,
        });
    }

    /// <summary>The profile's open document, or null when there is no such profile or it can't be opened.</summary>
    public ProfileDocument? Open(string id) => Find(id).Document;

    /// <summary>True while <paramref name="document"/> is the open document of its profile (false once it is deleted).</summary>
    public bool IsOpen(ProfileDocument document)
    {
        using (EnterLock())
            return _slots.TryGetValue(document.Id, out var slot) && ReferenceEquals(slot.Document, document);
    }

    /// <summary>
    /// The document to export (spec/docs/api.md, "Exporting"): the current document without its <c>rev</c> members, in
    /// the canonical form it is stored in (<see cref="Serialize"/>). Null when there is no such profile; the profile's
    /// <see cref="ProfileStatusProblem"/> (409) when it isn't <c>ok</c>: a profile this engine can't open is never exported.
    /// </summary>
    public (byte[]? Document, ProfileStatusProblem? Problem) Export(string id)
    {
        ProfileDocument? doc;
        // The profile is found in one step, so a profile deleted and created again meanwhile is answered as the one found.
        using (EnterLock())
        {
            var found = Find(id);
            if (!found.Found)
                return (null, null);
            ExportFound?.Invoke(id);
            doc = found.Document;
            if (doc is null)
                return (null, found.Problem);
        }
        return (Serialize(doc.Current), null);
    }

    private Slot? GetSlot(string id)
    {
        if (!DocumentStore.IsValidId(id))
            return null;
        using (EnterLock())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_slots.TryGetValue(id, out var slot))
                return slot;
            if (!Documents.Contains(id))
                return null;
            slot = Load(id);
            if (slot is not null)
                _slots[id] = slot;
            return slot;
        }
    }

    /// <summary>Reads and checks a stored document: open when it can be, else its status entry. Under the lock.</summary>
    private Slot? Load(string id)
    {
        byte[] bytes;
        try
        {
            bytes = Documents.Read(id);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException ex)
        {
            return Unreadable(id, "", "limit.documentBytes", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not read the document of profile {ProfileId}", id);
            return Unreadable(id, "", "io", ex.Message);
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(bytes, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Unreadable(id, "", "json", $"The stored document isn't JSON: {ex.Message}");
        }

        using (json)
        {
            var root = json.RootElement;
            // The JSON-level pass on the file as stored, before the folder's id replaces the document's own.
            if (ValidationText.CheckJson(root, Limits.JsonDepth) is { } invalidJson)
            {
                var error = ValidationText.Cap(invalidJson)[0];
                Logger.Warning("Profile {ProfileId} can't be opened ({Code} at {Path}: {Message})", id, error.Code, error.Path, error.Message);
                return Unreadable(id, error.Path, error.Code, error.Message);
            }
            JsonDocument? renamed = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var ownId) && ownId.ValueKind == JsonValueKind.String
                && ownId.GetString() != id)
            {
                Logger.Warning("The document in profile folder {ProfileId} has the id {DocumentId}; it is opened, and saved, under its folder's id",
                    id, ownId.GetString());
                renamed = WithId(root, id);
                root = renamed.RootElement;
            }

            using (renamed)
            {
                var load = Generation.Load(root, 1, Limits, Support, null, jsonChecked: true);
                if (load.Generation is { } generation)
                {
                    if (load.Check.Items.Count > 0)
                        Logger.Warning("Profile {ProfileId} has {Count} item(s) drawn as placeholders (first: {ItemId}, {Problem})",
                            id, load.Check.Items.Count, load.Check.Items[0].Id, load.Check.Items[0].Problem);
                    return new Slot(OpenDocument(id, generation), null);
                }

                var first = load.Check.Errors.FirstOrDefault() ?? new SpecError("", "schema", "The document can't be opened.");
                var problem = Body(first);
                if (load.Check.Status == CheckStatus.Unsupported)
                {
                    Logger.Information("Profile {ProfileId} needs what this engine doesn't read ({Code} at {Path}); it is listed but not opened", id, first.Code, first.Path);
                    return new Slot(null, new ProfileEntry { Id = id, Status = "unsupported", Instance = DocumentTag.New(), Name = NameOf(root), Problem = problem });
                }

                Logger.Warning("Profile {ProfileId} can't be opened ({Code} at {Path}: {Message})", id, first.Code, first.Path, first.Message);
                return new Slot(null, new ProfileEntry { Id = id, Status = "unreadable", Instance = DocumentTag.New(), Problem = problem });
            }
        }
    }

    private static Slot Unreadable(string id, string path, string code, string message) =>
        new(null, new ProfileEntry { Id = id, Status = "unreadable", Instance = DocumentTag.New(), Problem = new ValidationErrorBody { Path = path, Code = code, Message = message } });

    /// <summary>The document's <c>profile.name</c> when it is a string of 1–256 characters (an unsupported profile's entry).</summary>
    private static string? NameOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: >= 1 and <= 256 } name
            ? name
            : null;

    private ProfileDocument OpenDocument(string id, Generation generation) =>
        new(generation, new ProfileDocumentOptions
        {
            Instance = DocumentTag.New(),
            Time = _options.Time,
            HistoryBudget = _options.HistoryBudget,
            Persistence = new StorePersistence(Documents, id),
            Limits = Limits,
            Support = Support,
        });

    private static ProfileEntry EntryOf(ProfileDocument doc)
    {
        var profile = doc.Current.Document.Profile;
        var status = doc.Status;
        return new ProfileEntry
        {
            Id = doc.Id,
            Status = "ok",
            Instance = doc.Instance,
            Name = profile.Name,
            Width = profile.Width,
            Height = profile.Height,
            Rev = status.Rev,
            Dirty = status.Dirty,
        };
    }

    // ---- changing --------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/profiles</c> with the request's bytes: an empty profile (<c>{id, name, width, height}</c>) or one from
    /// a document (<c>{id, document}</c>, checked as a stored document: scenes that can't be drawn become placeholders),
    /// stored and saved at once, last in the list. The body is checked whole (422) before the id (409 <c>exists</c>).
    /// </summary>
    public CreateOutcome Create(ReadOnlyMemory<byte> body)
    {
        if (Parse(body, out var errors) is not { } json)
            return Invalid(errors);
        using (json)
        {
            var root = json.RootElement;
            // The JSON-level pass on the body as received, every repeated member included, before any member is taken
            // out or replaced; the document's nesting is counted from it (limit.jsonDepth at /document).
            var checkedJson = Spec.Schema.SchemaSet.CheckJsonMembers(root, Limits.JsonDepth);
            if (!checkedJson.IsValid)
                return Invalid(ValidationText.FromSchema(checkedJson).ToList());
            string id;
            JsonDocument document;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("document", out var given))
            {
                // The envelope is {id, document}: everything but the document is the shape of a duplicate's body.
                using var envelope = Without(root, "document");
                if (Check(envelope.RootElement, IdOnlySchema, jsonChecked: true) is { } invalid)
                    return Invalid(invalid);
                id = root.GetProperty("id").GetString()!;
                // The document's own id is replaced by the request's.
                document = WithImportedId(given, id);
            }
            else
            {
                if (Check(root, CreateEmptySchema, jsonChecked: true) is { } invalid)
                    return Invalid(invalid);
                var request = SpecJson.Read<CreateProfileRequest>(root);
                id = request.Id;
                document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new Document
                {
                    Format = "infopanel.document/1",
                    Id = id,
                    Profile = new DocumentProfile { Name = request.Name!, Width = request.Width!.Value, Height = request.Height!.Value },
                    Items = [],
                }, SpecJson.Options));
            }

            using (document)
            {
                var load = Generation.Load(document.RootElement, 1, Limits, Support, null, jsonChecked: true);
                if (load.Generation is not { } generation)
                    return Invalid(load.Check.Errors.Select(e => e.Under("/document")).ToList());
                return Store(id, generation, "/document", source: null);
            }
        }
    }

    /// <summary>
    /// Checks <paramref name="document"/> as <see cref="Create"/> checks a <c>{id, document}</c> body's document (its
    /// own <c>id</c> replaced, since a create chooses the id) and admits it by its stored size as a create does
    /// (<see cref="OverDocumentBytes"/>), without storing anything: the errors under <c>/document</c>, or none when a
    /// create with it would store it. An unsupported document is an error here too.
    /// </summary>
    public IReadOnlyList<SpecError> CheckImportDocument(JsonElement document)
    {
        const string AnyId = "00000000-0000-4000-8000-000000000000";
        // The JSON-level pass on the document as received, before its id is replaced.
        if (ValidationText.CheckJson(document, Limits.JsonDepth, "/document") is { } invalidJson)
            return ValidationText.Cap(invalidJson);
        using var copy = WithImportedId(document, AnyId);
        var load = Generation.Load(copy.RootElement, 1, Limits, Support, null, jsonChecked: true);
        if (load.Generation is not { } generation)
            return ValidationText.Cap(load.Check.Errors.Select(e => e.Under("/document")).ToList());
        return OverDocumentBytes(generation, "/document", out _) is { } over ? [over] : [];
    }

    /// <summary>
    /// <c>POST /api/profiles/{id}/duplicate</c>: a copy of the profile's current document under the id in the body. The
    /// source is checked first (404, 409 <c>profileStatusProblem</c>), then the body.
    /// </summary>
    public CreateOutcome Duplicate(string sourceId, ReadOnlyMemory<byte> body)
    {
        var found = Find(sourceId);
        if (!found.Found)
            return new CreateOutcome { NotFound = true };
        return found.Document is { } source ? Duplicate(source, body) : new CreateOutcome { StatusProblem = found.Problem };
    }

    /// <summary>A copy of <paramref name="source"/>'s current document under the id in the body; 404 once it is deleted.</summary>
    public CreateOutcome Duplicate(ProfileDocument source, ReadOnlyMemory<byte> body)
    {
        if (Parse(body, out var errors) is not { } json)
            return Invalid(errors);
        string id;
        using (json)
        {
            if (Check(json.RootElement, IdOnlySchema) is { } invalid)
                return Invalid(invalid);
            id = json.RootElement.GetProperty("id").GetString()!;
        }

        var copy = source.Current.Document with { Id = id };
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(copy, SpecJson.Options), SpecJson.DocumentOptions);
        var load = Generation.Load(document.RootElement, 1, Limits, Support);
        return load.Generation is { } generation ? Store(id, generation, "", source) : Invalid(load.Check.Errors.ToList());
    }

    /// <summary>
    /// The stored-size admission of a new profile, a create's and an import check's alike: the bytes the store would
    /// write (<paramref name="bytes"/>, <see cref="Serialize"/>) within <c>documentBytes</c>, else
    /// <c>limit.documentBytes</c> at <paramref name="path"/>. They are the canonical form validation measured, without
    /// <c>rev</c> members, so a document that passed its check passes this too; it holds the store to never writing a
    /// document it won't read back.
    /// </summary>
    private SpecError? OverDocumentBytes(Generation generation, string path, out byte[] bytes)
    {
        bytes = Serialize(generation);
        return bytes.Length > Limits.DocumentBytes ? DocumentValidator.Over(path, "documentBytes", bytes.Length, Limits.DocumentBytes) : null;
    }

    /// <summary>
    /// Stores a checked generation as a new profile and publishes its open document in one step under the workspace
    /// lock (the class's lifetime rule): 409 <c>exists</c> when the id is taken, 404 when a duplicate's
    /// <paramref name="source"/> was deleted meanwhile, 422 <c>limit.documentBytes</c> at <paramref name="path"/> when
    /// the bytes to store are over the limit (<see cref="OverDocumentBytes"/>).
    /// </summary>
    private CreateOutcome Store(string id, Generation generation, string path, ProfileDocument? source)
    {
        if (OverDocumentBytes(generation, path, out var bytes) is { } over)
            return Invalid([over]);

        ProfileDocument doc;
        using (EnterLock())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (source is not null && !(_slots.TryGetValue(source.Id, out var held) && ReferenceEquals(held.Document, source)))
                return new CreateOutcome { NotFound = true };
            if (Documents.Contains(id))
                return Exists(id);
            try
            {
                Documents.Create(id, bytes);
            }
            catch (InvalidOperationException)
            {
                return Exists(id);
            }

            StoreChanged?.Invoke(id);
            doc = OpenDocument(id, generation);
            _slots[id] = new Slot(doc, null);
        }

        Logger.Information("Created profile {ProfileId} ({ProfileName}, {Items} top-level items)", id, generation.Document.Profile.Name, generation.Document.Items.Count);
        RaiseProfilesChanged();
        return new CreateOutcome { Entry = EntryOf(doc) };
    }

    /// <summary>
    /// <c>DELETE /api/profiles/{id}</c>: deletes the profile, whatever its status, with its open document, when
    /// <paramref name="instance"/> is its tag; <see cref="DeleteOutcome.NotFound"/> when there is no such profile, a
    /// conflict (<c>instance</c>) when the tag is another, nothing deleted. The profile is found, its tag compared and it
    /// is deleted in one step under the workspace lock, so a profile created again under the id meanwhile is never
    /// deleted with the tag of the one before. The slot follows the store whatever happens: once the store no longer
    /// lists the id (even when it then failed), the document is removed and disposed, so a save of it under way
    /// finishes first and none follows.
    /// </summary>
    /// <exception cref="IOException">The store couldn't delete it (see <see cref="DocumentStore.Delete"/>).</exception>
    public DeleteOutcome Delete(string id, string instance)
    {
        if (!DocumentStore.IsValidId(id))
            return DeleteOutcome.NotFound;

        var gone = false;
        try
        {
            using (EnterLock())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!Documents.Contains(id))
                    return DeleteOutcome.NotFound;
                // The slot as a read finds it (opened now when nothing read it yet in this run): its tag is the one
                // every entry and document answer of this profile carried.
                if (GetSlot(id) is not { } found)
                    return DeleteOutcome.NotFound;
                var tag = found.Document?.Instance ?? found.Entry!.Instance;
                if (!string.Equals(tag, instance, StringComparison.Ordinal))
                    return new DeleteOutcome(true, DocumentTag.Conflict(found.Document?.Rev));
                try
                {
                    Documents.Delete(id);
                }
                finally
                {
                    gone = !Documents.Contains(id);
                    if (gone)
                    {
                        StoreChanged?.Invoke(id);
                        if (_slots.Remove(id, out var slot))
                            slot.Document?.Dispose();
                    }
                }
            }
        }
        finally
        {
            if (gone)
            {
                Logger.Information("Deleted profile {ProfileId}", id);
                RaiseProfilesChanged();
            }
        }

        return new DeleteOutcome(true, null);
    }

    /// <summary>
    /// <c>POST /api/blobs</c>: stores <paramref name="bytes"/> (at most <c>mediaBytes</c>) and inspects them as media,
    /// from the header only. <see cref="StoredBlob.Created"/> is the store's own answer, so of concurrent identical
    /// uploads exactly one is new.
    /// </summary>
    /// <exception cref="IOException">The blob couldn't be stored.</exception>
    public StoredBlob PutBlob(ReadOnlyMemory<byte> bytes)
    {
        var id = Blobs.Put(bytes, out var created);
        var check = MediaValidator.Check(bytes.Span, Limits);
        var info = new BlobInfo { Id = id, Bytes = bytes.Length };
        info = check.Info is { } media && check.Errors.Count == 0
            ? info with
            {
                Media = new MediaInfoBody
                {
                    Format = media.Format.ToString().ToLowerInvariant(),
                    Width = media.Width,
                    Height = media.Height,
                    Frames = media.Frames,
                    Duration = media.DurationSeconds,
                },
            }
            : info with { MediaError = Body(check.Errors.FirstOrDefault() ?? new SpecError("", "media.format", "The bytes aren't a media asset.")) with { Path = "" } };
        BlobStored?.Invoke(id);
        return new StoredBlob(info, created);
    }

    /// <summary>Takes the workspace lock (reentrant), telling <see cref="WaitingForLock"/> when it has to wait.</summary>
    private Lock.Scope EnterLock() => Locks.Enter(_lock, WaitingForLock);

    private void RaiseProfilesChanged()
    {
        // Counted whether or not anything listens: Version is how many times the list changed.
        var version = Interlocked.Increment(ref _version);
        ProfilesChanged?.Invoke(this, new ProfilesChangedEventArgs(version));
    }

    /// <summary>Disposes every open document (the engine is stopping).</summary>
    public void Dispose()
    {
        List<ProfileDocument> open;
        using (EnterLock())
        {
            if (_disposed)
                return;
            _disposed = true;
            open = [.. _slots.Values.Select(s => s.Document).OfType<ProfileDocument>()];
            _slots.Clear();
        }

        foreach (var doc in open)
            doc.Dispose();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A generation's document as stored, saved and exported: its canonical form (spec/docs/limits.md#sizes), without
    /// <c>rev</c> members. Its length is the generation's measured size (<c>documentBytes</c>).
    /// </summary>
    public static byte[] Serialize(Generation generation) => CanonicalJson.Utf8(generation.Document);

    private static JsonDocument? Parse(ReadOnlyMemory<byte> body, out IReadOnlyList<SpecError> errors)
    {
        errors = [];
        try
        {
            return JsonDocument.Parse(body, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            errors = [new SpecError("", "json", $"The body isn't JSON: {ex.Message}")];
            return null;
        }
    }

    /// <summary>A body against its schema, after the JSON-level pass (<paramref name="jsonChecked"/>: the caller ran it on the whole body).</summary>
    private IReadOnlyList<SpecError>? Check(JsonElement body, string schema, bool jsonChecked = false)
    {
        var result = jsonChecked ? SpecSchemas.StructureOnly.ValidateChecked(body, schema) : SpecSchemas.StructureOnly.Validate(body, schema, Limits.JsonDepth);
        return result.IsValid ? null : ValidationText.FromSchema(result).ToList();
    }

    /// <summary>An object with its root member <paramref name="name"/> left out (values copied as their raw text).</summary>
    private static JsonDocument Without(JsonElement root, string name) => Rewrite(root, name, null);

    /// <summary>An object with its root <c>id</c> set to <paramref name="id"/> (values copied as their raw text, at any depth).</summary>
    private static JsonDocument WithId(JsonElement root, string id) => Rewrite(root, "id", id);

    /// <summary>
    /// A create's document (importDocument) with <paramref name="id"/> as its id: its own <c>id</c> is optional and
    /// replaced, but one that isn't a document id is left in place, so the document's schema check refuses it at its
    /// path as for any other member (a value that isn't an object is left to that check too). After the JSON-level pass.
    /// </summary>
    private static JsonDocument WithImportedId(JsonElement document, string id) =>
        document.ValueKind == JsonValueKind.Object
        && (!document.TryGetProperty("id", out var own) || SpecSchemas.StructureOnly.ValidateChecked(own, DocumentIdSchema).IsValid)
            ? WithId(document, id)
            : JsonDocument.Parse(document.GetRawText(), SpecJson.DocumentOptions);

    private static JsonDocument Rewrite(JsonElement root, string member, string? value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var written = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(member))
                {
                    if (value is null || written)
                        continue;
                    writer.WriteString(property.Name, value);
                    written = true;
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
                }
            }
            if (!written && value is not null)
                writer.WriteString(member, value);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray(), SpecJson.DocumentOptions);
    }

    private static ValidationErrorBody Body(SpecError error) => new() { Path = error.Path, Code = error.Code, Message = error.Message };

    private static CreateOutcome Invalid(IReadOnlyList<SpecError> errors) => new()
    {
        Invalid = new InvalidProblem
        {
            Title = "Invalid request",
            Detail = "The request breaks the formats; see errors.",
            Status = 422,
            Code = "invalid",
            Errors = ValidationText.Cap(errors).Select(Body).ToList(),
        },
    };

    private static CreateOutcome Exists(string id) => new()
    {
        Exists = new ExistsProblem { Title = "Profile exists", Detail = $"A profile with the id '{id}' already exists.", Status = 409, Code = "exists" },
    };

    private sealed record Slot(ProfileDocument? Document, ProfileEntry? Entry);

    /// <summary>Saves a document's generations in its profile folder.</summary>
    private sealed class StorePersistence(DocumentStore store, string id) : IDocumentPersistence
    {
        public void Save(Generation generation) => store.Save(id, Serialize(generation));
    }
}

/// <summary>
/// The blob store as the media cache's <see cref="IMediaAssetStore"/>: a blob's bytes by its <c>sha256:</c> id, through
/// <see cref="BlobStore.ReadVerified"/>. A blob file over <c>mediaBytes</c> (only a file changed on disk can be) is no
/// media: refused from its size, unread; a file whose bytes don't hash to its id is missing media too.
/// </summary>
public sealed class BlobMediaStore(BlobStore blobs) : IMediaAssetStore
{
    public byte[]? Read(string hash) => BlobStore.IsValidId(hash) ? blobs.ReadVerified(hash) : null;
}
