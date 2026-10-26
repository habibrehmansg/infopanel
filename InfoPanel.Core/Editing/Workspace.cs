using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace InfoPanel.Editing;

/// <summary>Raised when the profile list or a listed summary (name, size, dirty) changes; <see cref="Version"/> only goes up.</summary>
public sealed class ProfilesChangedEventArgs(long version) : EventArgs
{
    public long Version { get; } = version;
}

/// <summary>
/// The engine's profiles: the list, in <c>profiles.xml</c> order, and one <see cref="ProfileDocument"/> per profile,
/// opened on first use. Thread-safe.
/// <list type="bullet">
/// <item>The list holds each profile as last saved; <c>profiles.xml</c> is only ever written from it, so the unsaved
/// edits of an open document never reach the file through another profile's save.</item>
/// <item>Lock order: a document's <c>SyncRoot</c>, then the workspace lock, then the store's. The workspace never
/// waits for a document's lock while holding its own.</item>
/// <item><see cref="ProfilesChanged"/> fires (outside every lock) when profiles are created, duplicated, imported or
/// deleted, and when an open document's name, size or dirty state changes.</item>
/// </list>
/// </summary>
public sealed class Workspace
{
    private static readonly ILogger Logger = Log.ForContext<Workspace>();

    public const int MaxDimension = ProfileStore.MaxDimension;
    public const int MaxNameLength = 256;

    private readonly Lock _lock = new();
    private readonly SensorHub _sensors;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, ProfileDocument> _documents = [];
    private readonly Dictionary<Guid, (string Name, int Width, int Height, bool Dirty)> _listed = [];
    private List<Profile>? _entries;
    private long _version;

    public Workspace(ProfileStore store, SensorHub? sensors = null, TimeProvider? time = null)
    {
        Store = store;
        _sensors = sensors ?? new SensorHub([]);
        _time = time ?? TimeProvider.System;
    }

    public ProfileStore Store { get; }

    /// <summary>Raised outside every lock; handlers must not block.</summary>
    public event EventHandler<ProfilesChangedEventArgs>? ProfilesChanged;

    /// <summary>The current profiles-list version (see <see cref="ProfilesChanged"/>).</summary>
    public long Version
    {
        get { lock (_lock) return _version; }
    }

    /// <summary>Every profile in list order; open documents report their own name, size, revision and dirty state.</summary>
    public IReadOnlyList<ProfileSummary> ListProfiles()
    {
        List<(Profile Entry, ProfileDocument? Doc)> rows;
        lock (_lock)
            rows = Entries().Select(p => (p, _documents.GetValueOrDefault(p.Guid))).ToList();

        return rows
            .Select(r => r.Doc?.ToSummary() ?? new ProfileSummary(r.Entry.Guid, r.Entry.Name, r.Entry.Width, r.Entry.Height, 1, false))
            .ToList();
    }

    /// <summary>The profile's document, loading its items on first use; null when there is no such profile.</summary>
    /// <exception cref="InvalidDataException">The profile's items file can't be read.</exception>
    public ProfileDocument? Open(Guid profileId)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(profileId, out var doc))
                return doc;

            var entry = Entries().FirstOrDefault(p => p.Guid == profileId);
            if (entry == null)
                return null;

            // The document gets its own copy of the profile: the entry stays as last saved.
            var profile = Copy(entry);
            doc = new ProfileDocument(profile, Store.LoadItems(profile), _sensors, _time);
            doc.Changed += OnDocumentChanged;
            _documents[profileId] = doc;
            _listed[profileId] = Listed(entry, dirty: false);
            return doc;
        }
    }

    /// <summary>Creates and saves an empty profile (model defaults otherwise).</summary>
    /// <exception cref="PatchException">A blank or over-long name, or a size outside 1–<see cref="MaxDimension"/>.</exception>
    public ProfileSummary Create(string? name, int width, int height)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
            throw new PatchException("name", $"The name must be 1–{MaxNameLength} characters.");
        if (!PropertyMap.IsXmlText(trimmed))
            throw new PatchException("name", "The name contains a character a profile can't store (a control character or an unpaired surrogate).");
        if (width is < 1 or > MaxDimension)
            throw new PatchException("width", $"The width must be 1–{MaxDimension}.");
        if (height is < 1 or > MaxDimension)
            throw new PatchException("height", $"The height must be 1–{MaxDimension}.");

        var profile = new Profile(trimmed, width, height);
        Add(profile, [], assets: null);
        return Summary(profile);
    }

    /// <summary>
    /// Saves a copy of the profile's current state (unsaved edits included) as a new profile named "{name} copy",
    /// with fresh item ids and a copy of its assets folder. Null when there is no such profile, including one deleted
    /// while this runs: the copy is made under the workspace lock, after checking the profile is still there.
    /// </summary>
    public ProfileSummary? Duplicate(Guid profileId)
    {
        if (Open(profileId) is not { } doc)
            return null;

        var (profile, items) = doc.CaptureState();
        profile.Guid = Guid.NewGuid();
        profile.Name += " copy";
        foreach (var item in ItemTree.Walk(items))
            item.Guid = Guid.NewGuid();
        foreach (var item in items)
            item.SetProfile(profile);

        AfterCapture?.Invoke();
        lock (_lock)
        {
            if (!IsOpenCore(doc))
                return null;
            AddLocked(profile, items, assets: Store.GetProfileAssetsDirectory(profileId));
        }

        Logger.Information("Added profile {ProfileName} ({ProfileGuid})", profile.Name, profile.Guid);
        RaiseProfilesChanged();
        return Summary(profile);
    }

    /// <summary>Test seam: runs in <see cref="Duplicate"/> and <see cref="Export"/> after the document is captured, before the workspace lock.</summary>
    internal Action? AfterCapture { get; set; }

    /// <summary>True while <paramref name="doc"/> is the open document of a listed profile (false once it is deleted).</summary>
    public bool IsOpen(ProfileDocument doc)
    {
        lock (_lock)
            return IsOpenCore(doc);
    }

    /// <summary>True while <paramref name="doc"/> is the open document of a listed profile. Under the workspace lock.</summary>
    private bool IsOpenCore(ProfileDocument doc) =>
        _documents.TryGetValue(doc.Id, out var open) && open == doc && Entries().Exists(p => p.Guid == doc.Id);

    /// <summary>Deletes the profile for good: its list entry, items file and assets folder, and closes its document.</summary>
    public bool Delete(Guid profileId)
    {
        lock (_lock)
        {
            var entries = Entries();
            var index = entries.FindIndex(p => p.Guid == profileId);
            if (index < 0)
                return false;

            Store.DeleteProfile(profileId);
            entries.RemoveAt(index);
            if (_documents.Remove(profileId, out var doc))
                doc.Changed -= OnDocumentChanged;
            _listed.Remove(profileId);
            _version++;
        }

        Logger.Information("Deleted profile {ProfileGuid}", profileId);
        RaiseProfilesChanged();
        return true;
    }

    /// <summary>Imports an <c>.infopanel</c> archive as a new profile (<see cref="InfoPanelArchive.Import(Stream, ProfileStore)"/>).</summary>
    /// <exception cref="InvalidDataException">Not a valid archive; nothing is added.</exception>
    public (ProfileSummary Summary, ArchiveImportResult Result) Import(Stream archive)
    {
        ArchiveImportResult result;
        lock (_lock)
        {
            var entries = Entries();
            result = InfoPanelArchive.Import(archive, Store);
            entries.Add(result.Profile);
            _version++;
        }

        RaiseProfilesChanged();
        return (Summary(result.Profile), result);
    }

    /// <summary>
    /// Writes the profile's current state (unsaved edits included) and assets as an <c>.infopanel</c> archive. Returns
    /// the exported profile (for <see cref="InfoPanelArchive.FileNameFor"/>), or null when there is no such profile,
    /// including one deleted while this runs (the assets are read under the workspace lock, after checking).
    /// </summary>
    public Profile? Export(Guid profileId, Stream destination)
    {
        if (Open(profileId) is not { } doc)
            return null;

        var (profile, items) = doc.CaptureState();
        AfterCapture?.Invoke();
        lock (_lock)
        {
            if (!IsOpenCore(doc))
                return null;
            InfoPanelArchive.Export(profile, items, Store, destination);
        }

        return profile;
    }

    /// <summary>
    /// Saves the document: its items file, then its entry in <c>profiles.xml</c> (every other entry as last saved).
    /// Null when there is no such profile.
    /// </summary>
    public MutationResult? Save(Guid profileId)
    {
        if (Open(profileId) is not { } doc)
            return null;

        return doc.Save((profile, items) =>
        {
            lock (_lock)
            {
                var entries = Entries();
                var index = entries.FindIndex(p => p.Guid == profileId);
                if (index < 0)
                    throw EditException.NotFound($"Profile '{profileId}' was deleted.");

                Store.SaveItems(profileId, items);
                var previous = entries[index];
                entries[index] = Copy(profile);
                try
                {
                    SaveProfiles();
                }
                catch
                {
                    // The list must hold only what profiles.xml holds, or another profile's save would write this one.
                    entries[index] = previous;
                    throw;
                }
            }
        });
    }

    /// <summary>
    /// When the data folder has no profiles, saves <see cref="ExampleProfiles.WideBar"/> and returns true. Called at
    /// start-up, before a workspace reads the list.
    /// </summary>
    public static bool EnsureExampleProfile(ProfileStore store)
    {
        if (store.LoadProfiles().Count > 0)
            return false;

        var (profile, items) = ExampleProfiles.WideBar();
        store.AddProfile(profile, items);
        return true;
    }

    /// <summary>Writes <c>profiles.xml</c> from the list: every profile as last saved. Under the workspace lock.</summary>
    private void SaveProfiles() => Store.SaveProfiles(Entries());

    private List<Profile> Entries() => _entries ??= Store.LoadProfiles();

    /// <summary>Adds a new profile to the store and the list.</summary>
    private void Add(Profile profile, List<DisplayItem> items, string? assets)
    {
        lock (_lock)
            AddLocked(profile, items, assets);

        Logger.Information("Added profile {ProfileName} ({ProfileGuid})", profile.Name, profile.Guid);
        RaiseProfilesChanged();
    }

    /// <summary>Adds a new profile to the store (copying <paramref name="assets"/> first) and the list. Under the workspace lock; the caller raises <see cref="ProfilesChanged"/>.</summary>
    private void AddLocked(Profile profile, List<DisplayItem> items, string? assets)
    {
        var entries = Entries();
        var assetsCopy = Store.GetProfileAssetsDirectory(profile.Guid);
        try
        {
            if (assets != null && Directory.Exists(assets))
                CopyDirectory(assets, assetsCopy);
            Store.AddProfile(profile, items);
        }
        catch
        {
            try
            {
                if (assets != null && Directory.Exists(assetsCopy))
                    Directory.Delete(assetsCopy, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Could not remove {Path} while rolling back", assetsCopy);
            }

            throw;
        }

        entries.Add(Copy(profile));
        _version++;
    }

    private ProfileSummary Summary(Profile profile) =>
        ListProfiles().FirstOrDefault(s => s.Id == profile.Guid)
        ?? new ProfileSummary(profile.Guid, profile.Name, profile.Width, profile.Height, 1, false);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (sender is not ProfileDocument doc)
            return;

        var summary = doc.ToSummary();
        var listed = (summary.Name, summary.Width, summary.Height, summary.Dirty);
        lock (_lock)
        {
            if (!_documents.TryGetValue(doc.Id, out var open) || open != doc
                || (_listed.TryGetValue(doc.Id, out var previous) && previous == listed))
            {
                return;
            }

            _listed[doc.Id] = listed;
            _version++;
        }

        RaiseProfilesChanged();
    }

    private void RaiseProfilesChanged()
    {
        var handlers = ProfilesChanged;
        if (handlers == null)
            return;

        var args = new ProfilesChangedEventArgs(Version);
        foreach (EventHandler<ProfilesChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "A ProfilesChanged handler failed");
            }
        }
    }

    private static (string, int, int, bool) Listed(Profile profile, bool dirty) => (profile.Name, profile.Width, profile.Height, dirty);

    private static Profile Copy(Profile profile) => ProfileSerializer.ReadProfileSnapshot(ProfileSerializer.SnapshotProfile(profile));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
