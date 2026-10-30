using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using InfoPanel.Documents;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>A 1.x folder's profile list, settings and folder-level diagnostics, as read now.</summary>
public sealed record V1FolderContents(
    IReadOnlyList<Profile> Profiles,
    XmlReadReport? ProfilesReport,
    V1Settings? Settings,
    bool IsV11,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>
/// A 1.x data folder (<c>{LocalApplicationData}/InfoPanel</c>, <c>%LOCALAPPDATA%\InfoPanel</c> as 1.x wrote it), read
/// only: files are opened read-only and shared, names matched case-insensitively, links never followed, and nothing is
/// created, locked or written there. It has 1.x data when it holds <c>profiles.xml</c> or <c>settings.xml</c>.
/// <para>The migration's state (<see cref="GetState"/>) is <c>none</c>, <c>pending</c> or <c>done</c>, from the
/// run-once record <c>import-v1.json</c> in the engine's data folder (<see cref="WriteRecord"/>).</para>
/// <para><b>1.2.x and later only.</b> 1.1.x data is recognised by its settings, as 1.4.x recognised it: a readable
/// <c>settings.xml</c> (or its <c>.bak</c>) whose <c>Version</c> is 114 or lower, or absent (1.4.x's default, 114, which
/// made 1.4.x run its one-time 1.1.4 upgrade). 1.2.x writes 115 on its first start, 1.3.1 and later 131. Such a folder
/// is reported (<c>import.version</c>, an error), its profiles listed but none read, and its settings left out.</para>
/// </summary>
public sealed class V1Folder
{
    public const string ProfilesFile = "profiles.xml", SettingsFile = "settings.xml", SettingsBackupFile = "settings.xml.bak";

    /// <summary>The run-once record's name in the engine's data folder.</summary>
    public const string RecordFileName = DataPaths.ImportRecordFileName;

    public const string StateNone = "none", StatePending = "pending", StateDone = "done";

    private static readonly string[] Outcomes = ["imported", "skipped", "failed"];
    private static readonly string[] SettingsOutcomes = ["imported", "skipped"];

    /// <summary>The record's own members; any other is a later engine's, ignored when read and kept when replaced.</summary>
    private static readonly string[] RecordMembers = ["completed", "folder", "profiles", "settings"];

    private const string RecordSchema = SpecSchemas.Api + "#/$defs/v1MigrationRecord";

    /// <summary>The record is the engine's own file, read tolerantly: members this engine doesn't know are ignored.</summary>
    private static readonly JsonSerializerOptions RecordReading = new(SpecJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    public V1Folder(string root, ImportLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Limits = limits ?? ImportLimits.Default;
    }

    public string Root { get; }
    public ImportLimits Limits { get; }

    /// <summary><c>{LocalApplicationData}/InfoPanel</c>, where 1.x kept its data (the engine's own folder is its <c>v2</c>).</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "InfoPanel");

    /// <summary>The run-once record of the engine whose data folder is <paramref name="data"/>.</summary>
    public static string RecordFile(DataPaths data) => Path.Combine(data.Root, RecordFileName);

    /// <summary>The 1.x folders the engine must never write into, by name (found ignoring case, as the import reads them).</summary>
    private static readonly string[] ProtectedFolders = ["profiles", "assets"];

    /// <summary>
    /// The top-level entries the engine writes in its data folder (<see cref="DataPaths.EngineFiles"/> and
    /// <see cref="DataPaths.EngineFolders"/>). A file the engine writes by renaming a temp file over it replaces a link
    /// instead of writing through it; the folders and the lock are written through.
    /// </summary>
    private static readonly string[] DataEntries = [.. DataPaths.EngineFiles, .. DataPaths.EngineFolders];

    /// <summary>
    /// Why a 1.x folder at <paramref name="v1Root"/> can't be used with the data folder <paramref name="dataRoot"/>, or
    /// null: the data folder must not be the 1.x folder, contain it, or be inside its <c>profiles</c> or <c>assets</c>
    /// folder. A data folder elsewhere inside the 1.x folder is allowed: the default layout puts it in <c>InfoPanel/v2</c>.
    /// Checked on the paths as given and on their real paths (links resolved, components that don't exist yet kept), so
    /// a link can't alias one folder into the other. With links resolved the complete paths are compared: the 1.x
    /// <c>profiles</c> and <c>assets</c> folders (each entry of that name, ignoring case) must neither hold nor be
    /// inside the data folder or any entry the engine writes in it (its top-level entries and each existing profile
    /// folder), and none of those entries may be or hold the 1.x folder, or be elsewhere inside it than in the data
    /// folder. A path that can't be resolved (links that loop, a folder that can't be read) is refused. Names compare as
    /// the platform's default filesystem compares them (<see cref="PathNameRules.Platform"/>): ignoring letter case on
    /// Windows and macOS, exactly on Linux; on macOS a composed <c>é</c> and <c>e</c> with a combining accent are one
    /// name. The 1.x <c>profiles</c> and <c>assets</c> folders are still found by name ignoring case, as the import reads them.
    /// </summary>
    public static string? FindOverlap(string v1Root, string dataRoot) => FindOverlap(v1Root, dataRoot, PathNameRules.Platform);

    /// <summary>As <see cref="FindOverlap(string, string)"/>, comparing names by <paramref name="names"/>.</summary>
    internal static string? FindOverlap(string v1Root, string dataRoot, PathNameRules names)
    {
        var v1 = Path.GetFullPath(v1Root);
        var data = Path.GetFullPath(dataRoot);
        var problem = $"The 2.0 data folder {data} must not be the 1.x folder {v1}, contain it, or be inside its profiles or assets folder, " +
            "on the paths as given or with links resolved.";
        if (Overlaps(v1, [.. ProtectedFolders.Select(name => Path.Combine(v1, name))], data, [], names))
            return problem;

        try
        {
            var realV1 = PathExtensions.ResolveRealPath(v1Root);
            var realData = PathExtensions.ResolveRealPath(dataRoot);
            return Overlaps(realV1, RealProtectedFolders(v1Root, realV1), realData, RealDataEntries(dataRoot), names) ? problem : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The 2.0 data folder {data} and the 1.x folder {v1} can't be checked with links resolved: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether the data folder holds the 1.x folder or nests with its protected folders, or any of <paramref name="entries"/>
    /// (in the data folder) holds the 1.x folder, nests with a protected folder, or is inside the 1.x folder elsewhere than
    /// in the data folder.
    /// </summary>
    private static bool Overlaps(string v1, IReadOnlyList<string> protectedFolders, string data, IEnumerable<string> entries, PathNameRules names) =>
        names.IsInside(v1, data)
        || protectedFolders.Any(folder => Nested(folder, data, names))
        || entries.Any(entry =>
            names.IsInside(v1, entry)
            || protectedFolders.Any(folder => Nested(folder, entry, names))
            || (names.IsInside(entry, v1) && !names.IsInside(entry, data)));

    private static bool Nested(string a, string b, PathNameRules names) => names.IsInside(a, b) || names.IsInside(b, a);

    /// <summary>
    /// Why the engine mustn't write in <paramref name="path"/>, a folder in the data folder <paramref name="dataRoot"/>
    /// it gives to someone else to write in (a plugin host's settings or log folder), or null: as
    /// <see cref="FindOverlap(string, string)"/> checks the data folder's own entries at start-up, with links resolved
    /// it must neither be, hold nor be inside the 1.x <c>profiles</c> or <c>assets</c> folder, nor hold the 1.x folder or
    /// be inside it elsewhere than in the data folder. Checked whenever the folder is given out, since a link deeper in
    /// the data folder than its own entries isn't seen at start-up.
    /// </summary>
    public static string? FindWriteProblem(string v1Root, string dataRoot, string path) => FindWriteProblem(v1Root, dataRoot, path, PathNameRules.Platform);

    /// <summary>As <see cref="FindWriteProblem(string, string, string)"/>, comparing names by <paramref name="names"/>.</summary>
    internal static string? FindWriteProblem(string v1Root, string dataRoot, string path, PathNameRules names)
    {
        var v1 = Path.GetFullPath(v1Root);
        try
        {
            var realV1 = PathExtensions.ResolveRealPath(v1Root);
            var real = PathExtensions.ResolveRealPath(path);
            return Overlaps(realV1, RealProtectedFolders(v1Root, realV1), PathExtensions.ResolveRealPath(dataRoot), [real], names)
                ? $"{path} is {real} with links resolved, which is the 1.x folder {v1}'s, and the engine never writes there."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path} and the 1.x folder {v1} can't be checked with links resolved: {ex.Message}";
        }
    }

    /// <summary>The real paths of the 1.x <c>profiles</c> and <c>assets</c> folders, and of every entry with those names ignoring case.</summary>
    private static List<string> RealProtectedFolders(string v1Root, string realV1)
    {
        var folders = ProtectedFolders.Select(name => PathExtensions.ResolveRealPath(Path.Combine(v1Root, name))).ToList();
        if (Directory.Exists(realV1))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(realV1))
            {
                if (ProtectedFolders.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    folders.Add(PathExtensions.ResolveRealPath(entry));
            }
        }

        return folders;
    }

    /// <summary>The real paths of the entries the engine writes in the data folder, and of each existing profile folder.</summary>
    private static List<string> RealDataEntries(string dataRoot)
    {
        var entries = DataEntries.Select(name => PathExtensions.ResolveRealPath(Path.Combine(dataRoot, name))).ToList();
        var profiles = PathExtensions.ResolveRealPath(Path.Combine(dataRoot, "profiles"));
        if (Directory.Exists(profiles))
            entries.AddRange(Directory.EnumerateFileSystemEntries(profiles).Select(PathExtensions.ResolveRealPath));
        return entries;
    }

    /// <summary>Whether the folder has 1.x data: <c>profiles.xml</c> or <c>settings.xml</c>.</summary>
    public bool HasData => Find([ProfilesFile]) is not null || Find([SettingsFile]) is not null;

    /// <summary>
    /// Whether the migration is <c>pending</c> for the engine whose data folder is <paramref name="data"/>, as
    /// <see cref="GetState"/> tells it, without reading the folder's files: it has 1.x data and the data folder no record
    /// (a record there, readable or not, makes it done).
    /// </summary>
    public bool IsPending(DataPaths data) => RegularFile.StateOf(RecordFile(data)) == PathState.Absent && HasData;

    /// <summary>A file of the folder by its segments, matched ignoring case; null when missing or a link.</summary>
    private string? Find(IReadOnlyList<string> segments) =>
        V1FolderAssets.Walk(Root, segments, directoryAtEnd: false, out var path) == V1AssetLookup.Found ? path : null;

    /// <summary>
    /// Reads a file of the folder, at most <paramref name="maxBytes"/>, counted against <paramref name="budget"/>. Null
    /// with no problem when it isn't there; a problem when it is a link, over the limit, or can't be read.
    /// </summary>
    private byte[]? ReadFile(IReadOnlyList<string> segments, int maxBytes, ImportBudget budget, out string? problem)
    {
        problem = null;
        var lookup = V1FolderAssets.Walk(Root, segments, directoryAtEnd: false, out var path);
        if (lookup == V1AssetLookup.Missing)
            return null;
        if (lookup == V1AssetLookup.Link)
        {
            problem = "it is a link, which isn't followed.";
            return null;
        }

        try
        {
            // A regular file only, opened without waiting: a FIFO, socket or device there can't be read, and isn't waited on.
            using var stream = RegularFile.OpenRead(path!, FileShare.ReadWrite | FileShare.Delete);
            var bytes = budget.ReadAll(stream, maxBytes);
            if (bytes is null)
                problem = $"it is larger than {maxBytes} bytes.";
            return bytes;
        }
        catch (ImportBudgetExceededException)
        {
            problem = "the import already read its limit of bytes.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Reads the profile list and the settings (each read once per call).</summary>
    public V1FolderContents Read() => Read(new ImportBudget(Limits.ExpandedBytes));

    private V1FolderContents Read(ImportBudget budget) => Read(budget, new ImportDiagnostics(Limits.Diagnostics));

    /// <summary>Reads into <paramref name="diagnostics"/>, which may already hold the answer's earlier diagnostics.</summary>
    private V1FolderContents Read(ImportBudget budget, ImportDiagnostics diagnostics)
    {
        var (profiles, report) = ReadProfiles(budget, diagnostics);
        var settings = ReadSettings(budget, diagnostics);

        var isV11 = settings is { Version: <= V1SettingsReader.DefaultVersion };
        if (isV11)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.version", "",
                $"This is 1.1.x data (settings version {settings!.Version}): only data from 1.2.x and later is imported. None of its profiles or settings are read; " +
                "opening it once in 1.4.x upgrades it.", SettingsFile);
            settings = null;
        }

        return new V1FolderContents(profiles, report, settings, isV11, diagnostics.ToList());
    }

    private (List<Profile> Profiles, XmlReadReport? Report) ReadProfiles(ImportBudget budget, ImportDiagnostics diagnostics)
    {
        var bytes = ReadFile([ProfilesFile], Limits.ProfileXmlBytes, budget, out var problem);
        if (bytes is null)
        {
            if (problem is not null)
                diagnostics.Add(ImportDiagnostics.Error, "import.profiles", "/profiles", $"{ProfilesFile} can't be read: {problem} No profiles are listed.", ProfilesFile);
            return ([], null);
        }

        var report = new XmlReadReport();
        List<Profile> profiles;
        try
        {
            profiles = ProfileSerializer.ReadProfiles(SafeXml.Load(bytes, XmlLimits.From(Limits)), report);
        }
        catch (XmlRefusedException ex)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.profiles", "/profiles", $"{ProfilesFile} can't be read: {ex.Message} No profiles are listed.", ProfilesFile, ex.Line);
            return ([], null);
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.profiles", "/profiles", $"{ProfilesFile} isn't a 1.x profile list: {ex.Message} No profiles are listed.", ProfilesFile);
            return ([], null);
        }

        // An xsi:nil <Profile/> deserializes as a null entry: it is left out, as nil items are.
        var nils = profiles.RemoveAll(p => p is null);
        if (nils > 0)
            diagnostics.Add(ImportDiagnostics.Info, "import.nil", "/profiles",
                $"{nils} empty (xsi:nil) {(nils == 1 ? "profile is" : "profiles are")} left out of {ProfilesFile}.", ProfilesFile);

        if (profiles.Count > Limits.Profiles)
        {
            diagnostics.Add(ImportDiagnostics.Warning, "import.profiles", "/profiles",
                $"{ProfilesFile} lists {profiles.Count} profiles; the first {Limits.Profiles} are read and the rest left out.", ProfilesFile);
            profiles = profiles.Take(Limits.Profiles).ToList();
        }

        var first = new Dictionary<Guid, int>();
        for (var i = 0; i < profiles.Count; i++)
        {
            if (first.TryAdd(profiles[i].Guid, i))
                continue;
            diagnostics.Add(ImportDiagnostics.Warning, "import.profile.duplicate", $"/profiles/{i}",
                $"'{profiles[i].Name}' has the id of '{profiles[first[profiles[i].Guid]].Name}' (profile {first[profiles[i].Guid]}): 1.x kept one items file and one assets folder for both, " +
                "so it can't be read on its own; reading that id reads the first.", ProfilesFile);
        }
        return (profiles, report);
    }

    /// <summary><c>settings.xml</c>, else <c>settings.xml.bak</c> when it can't be read, as 1.4.x falls back.</summary>
    private V1Settings? ReadSettings(ImportBudget budget, ImportDiagnostics diagnostics)
    {
        var main = ReadSettingsRoot(SettingsFile, budget, out var mainProblem);
        if (main is not null)
            return V1SettingsReader.Read(main, SettingsFile, Limits, diagnostics);

        var backup = ReadSettingsRoot(SettingsBackupFile, budget, out var backupProblem);
        if (backup is not null)
        {
            diagnostics.Add(ImportDiagnostics.Info, "import.settings.backup", "/settings",
                $"{SettingsFile} {(mainProblem is null ? "isn't there" : "can't be read (" + mainProblem + ")")}; {SettingsBackupFile} is read instead, as 1.x falls back.", SettingsBackupFile);
            return V1SettingsReader.Read(backup, SettingsBackupFile, Limits, diagnostics);
        }

        if (mainProblem is not null || backupProblem is not null)
            diagnostics.Add(ImportDiagnostics.Error, "import.settings", "/settings",
                $"Neither {SettingsFile} ({mainProblem ?? "not there"}) nor {SettingsBackupFile} ({backupProblem ?? "not there"}) can be read: no settings are read.", SettingsFile);
        return null;
    }

    /// <summary>A settings file's <c>Settings</c> element; null when it isn't there (no problem) or can't be read (a problem).</summary>
    private XElement? ReadSettingsRoot(string name, ImportBudget budget, out string? problem)
    {
        var bytes = ReadFile([name], Limits.ProfileXmlBytes, budget, out problem);
        if (bytes is null)
            return null;
        try
        {
            var root = SafeXml.Load(bytes, XmlLimits.From(Limits)).Root;
            // Settings in no namespace, as 1.4.x's serializer requires; any other root is unreadable, so the backup is read.
            if (root?.Name == XName.Get("Settings"))
                return root;
            problem = "its root element isn't Settings";
        }
        catch (XmlRefusedException ex)
        {
            problem = ex.Message;
        }
        return null;
    }

    // ---- the migration's state and record ------------------------------------------------------------------------

    /// <summary>
    /// The migration's state for the engine whose data folder is <paramref name="data"/>: <c>done</c> when it has a record
    /// (or one that can't be read: migration never runs on its own twice), else <c>pending</c> when the folder has 1.x data,
    /// else <c>none</c>; with the folder's profiles, settings and diagnostics when it has 1.x data.
    /// </summary>
    public V1Migration GetState(DataPaths data)
    {
        var record = ReadRecord(data, out var recordProblem);
        var hasData = HasData;
        var state = record is not null || recordProblem is not null ? StateDone : hasData ? StatePending : StateNone;

        // One collector for the whole answer, so the record's diagnostic and the folder's share one cap.
        var diagnostics = new ImportDiagnostics(Limits.Diagnostics);
        if (recordProblem is not null)
            diagnostics.Add(ImportDiagnostics.Warning, "import.record", "/record",
                $"The migration record can't be read ({recordProblem}); the migration counts as done.", RecordFileName);

        if (!hasData)
            return new V1Migration { State = state, Folder = Root, Record = record, Diagnostics = diagnostics.ToList() is { Count: > 0 } listed ? listed : null };

        var contents = Read(new ImportBudget(Limits.ExpandedBytes), diagnostics);
        return new V1Migration
        {
            State = state,
            Folder = Root,
            Record = record,
            Profiles = contents.Profiles.Select(p => new V1ProfileSummary
            {
                // A name 1.x left null (xsi:nil) is listed as empty.
                Id = p.Guid.ToString(), Name = p.Name ?? "", Width = p.Width, Height = p.Height, Active = p.Active,
            }).ToList(),
            Settings = contents.Settings,
            Diagnostics = contents.Diagnostics,
        };
    }

    /// <summary>The record, or null: none (no problem) or unreadable (<paramref name="problem"/>).</summary>
    private V1MigrationRecord? ReadRecord(DataPaths data, out string? problem)
    {
        var file = InspectRecord(data);
        problem = file.Problem;
        return file.Record;
    }

    /// <summary>
    /// What the record file holds: the record when it can be read (tolerantly: members this engine doesn't know are
    /// ignored, read at one depth, jsonDepth); the members this engine doesn't know, as written (<see cref="EngineJson"/>),
    /// whenever the file is a JSON object, so a replacement keeps them; and whether it may be replaced at all: not when
    /// the engine can't hold it whole (over <see cref="ImportLimits.RecordBytes"/>, well-formed but nested deeper than
    /// jsonDepth, or a file it can't open, a FIFO say), which is then left as it is.
    /// </summary>
    private RecordState InspectRecord(DataPaths data)
    {
        var path = RecordFile(data);
        if (RegularFile.StateOf(path) == PathState.Absent)
            return new RecordState(null, null, null, Replaceable: true);

        byte[] bytes;
        try
        {
            bytes = DocumentStore.ReadAtMost(path, Limits.RecordBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new RecordState(null, ex.Message, null, Replaceable: false);
        }

        List<JsonMemberText> unknown;
        try
        {
            unknown = EngineJson.Members(bytes).Where(member => member.Name is null || !RecordMembers.Contains(member.Name)).ToList();
        }
        catch (JsonException ex)
        {
            return EngineJson.IsTooDeep(bytes)
                ? new RecordState(null, $"it nests deeper than {EngineJson.MaxDepth} levels", null, Replaceable: false)
                : new RecordState(null, ex.Message, null, Replaceable: true);
        }

        try
        {
            var record = JsonSerializer.Deserialize<V1MigrationRecord>(bytes, RecordReading)
                ?? throw new JsonException("The record is null.");
            if (Check(record) is { } invalid)
                throw new JsonException(invalid.Message);
            // The members it knows, held to their schema (types and formats: a completed that isn't a timestamp, say), so
            // an answer never carries a record outside its format; the members it doesn't know aren't looked at.
            var known = SpecSchemas.Strict.Validate(JsonSerializer.SerializeToElement(record, SpecJson.Options), RecordSchema);
            if (!known.IsValid)
                throw new JsonException($"{known.Errors[0].InstancePath}: {known.Errors[0].Message}");
            return new RecordState(record, null, unknown, Replaceable: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new RecordState(null, ex.Message, unknown, Replaceable: true);
        }
    }

    /// <summary>The record file: its record or why it can't be read, its members this engine doesn't know, and whether it may be replaced.</summary>
    private sealed record RecordState(V1MigrationRecord? Record, string? Problem, List<JsonMemberText>? Unknown, bool Replaceable);

    /// <summary>
    /// Replaces the run-once record with what a client reports (<c>completed</c> is now, <c>folder</c> this folder) and
    /// answers the new state. Written atomically by the store's write rules, in the data folder, never the 1.x folder.
    /// The record it replaces keeps its members this engine doesn't know (a later engine's), as written, after the new
    /// ones; a record the engine can't hold whole (<see cref="InspectRecord"/>) is left as it is and the write refused.
    /// </summary>
    /// <exception cref="ImportRefusal">More than <see cref="ImportLimits.RecordOutcomes"/> outcomes, a value outside the
    /// record's rules, or larger than <see cref="ImportLimits.RecordBytes"/> as stored.</exception>
    /// <exception cref="IOException">The record couldn't be written, or the one there can't be held whole; it is unchanged.</exception>
    public V1Migration WriteRecord(DataPaths data, IReadOnlyList<V1MigrationOutcome> profiles, string settings, DateTimeOffset now)
    {
        var record = new V1MigrationRecord
        {
            Completed = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Folder = Root,
            Profiles = profiles,
            Settings = settings,
        };
        if (Check(record) is { } invalid)
            throw invalid;

        // The record it replaces keeps its members this engine doesn't know, as written; one the engine can't hold whole
        // is never replaced.
        var existing = InspectRecord(data);
        if (!existing.Replaceable)
            throw new IOException($"The migration record {RecordFile(data)} can't be read whole ({existing.Problem}), so it is left as it is.");
        var bytes = EngineJson.Append(JsonSerializer.SerializeToUtf8Bytes(record, SpecJson.Options), existing.Unknown);
        if (bytes.Length > Limits.RecordBytes)
            throw new ImportRefusal("limit.recordBytes", $"The record is {bytes.Length} bytes; at most {Limits.RecordBytes} are stored.");

        AtomicFile.Write(RecordFile(data), bytes, data.Root);
        return GetState(data);
    }

    /// <summary>Why a record breaks its rules, or null.</summary>
    private ImportRefusal? Check(V1MigrationRecord record)
    {
        if (record.Profiles.Count > Limits.RecordOutcomes)
            return new ImportRefusal("limit.recordOutcomes", $"The record has {record.Profiles.Count} profile outcomes; at most {Limits.RecordOutcomes} are allowed.", "/profiles");
        if (!SettingsOutcomes.Contains(record.Settings))
            return new ImportRefusal("record.settings", "settings must be 'imported' or 'skipped'.", "/settings");
        for (var i = 0; i < record.Profiles.Count; i++)
        {
            var outcome = record.Profiles[i];
            // The serializer refuses a null list, not a null element of it.
            if (outcome is null)
                return new ImportRefusal("record.outcome", $"The profile outcome at /profiles/{i} is null.", $"/profiles/{i}");
            if (!Guid.TryParseExact(outcome.V1Id, "D", out var v1Id) || v1Id.ToString() != outcome.V1Id)
                return new ImportRefusal("record.v1Id", "v1Id must be a 1.x profile's Guid in lower case.", $"/profiles/{i}/v1Id");
            if (outcome.Id is not null && !DocumentStore.IsValidId(outcome.Id))
                return new ImportRefusal("record.id", "id must be a profile id or null.", $"/profiles/{i}/id");
            if (!Outcomes.Contains(outcome.Outcome))
                return new ImportRefusal("record.outcome", "outcome must be 'imported', 'skipped' or 'failed'.", $"/profiles/{i}/outcome");
            // Counted in characters (code points), as the schema's maxLength counts it, and refused with its code.
            if (outcome.Reason is { } reason && reason.EnumerateRunes().Count() > Limits.RecordReasonLength)
                return new ImportRefusal("schema", $"reason is longer than {Limits.RecordReasonLength} characters.", $"/profiles/{i}/reason");
        }
        return null;
    }

    // ---- one profile ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the profile <paramref name="id"/> (<c>profiles/{guid}.xml</c> and <c>assets/{guid}/</c>) into its envelope,
    /// storing its assets as blobs. Null when the folder has no such profile (or no profile list). 1.x autosaved, unsaved
    /// changes for it are reported (<c>import.autosave</c>); the saved version is read.
    /// </summary>
    /// <exception cref="ImportRefusal"><c>import.version</c>: 1.1.x data, which isn't imported.</exception>
    public V1Profile? ReadProfile(Guid id, V1ReadContext context)
    {
        var contents = Read(context.Budget);
        var profile = contents.Profiles.FirstOrDefault(p => p.Guid == id);
        if (profile is null)
            return null;
        if (contents.IsV11)
            throw new ImportRefusal("import.version", "This is 1.1.x data: only data from 1.2.x and later is imported.");

        var diagnostics = new ImportDiagnostics(context.Limits.Diagnostics);
        var itemsFile = $"profiles/{id}.xml";
        var bytes = ReadFile(["profiles", id + ".xml"], context.Limits.ItemsXmlBytes, context.Budget, out var problem);
        var items = bytes is not null
            ? new V1Reader.ItemsInput(itemsFile, bytes, Missing: false, OverLimit: false)
            : problem is null
                ? new V1Reader.ItemsInput(itemsFile, null, Missing: true, OverLimit: false)
                : new V1Reader.ItemsInput(itemsFile, null, Missing: false, OverLimit: false, Problem: problem);

        if (Find(["autosave", "profiles", id + ".xml"]) is not null)
            diagnostics.Add(ImportDiagnostics.Warning, "import.autosave", "",
                "1.x holds autosaved changes to this profile that were never saved; the saved version is read.", $"autosave/profiles/{id}.xml");

        return V1Reader.Read(V1Reader.FolderSource, profile, contents.ProfilesReport, ProfilesFile, items,
            new V1FolderAssets(Root, id, context.Budget), context, diagnostics);
    }
}
