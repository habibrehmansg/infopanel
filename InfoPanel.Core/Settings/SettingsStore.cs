using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using Serilog;

namespace InfoPanel.Settings;

/// <summary>A settings change that breaks the settings' rules; nothing of it was applied.</summary>
public sealed class SettingsChangeException(IReadOnlyList<SpecError> errors)
    : Exception($"The settings change is invalid: {string.Join("; ", errors.Select(e => $"{e.Path} {e.Code}: {e.Message}"))}")
{
    /// <summary>Every problem, each at its JSON pointer into the change (<c>schema</c>), or into the result (<c>limit.*</c>).</summary>
    public IReadOnlyList<SpecError> Errors { get; } = errors;
}

/// <summary>The settings file couldn't be read at start-up, so this run never changes it.</summary>
public sealed class SettingsUnavailableException(SettingsStatus status, SpecError problem)
    : InvalidOperationException($"The settings file is {status.ToString().ToLowerInvariant()} ({problem.Code} at '{problem.Path}': {problem.Message}); settings can't be changed until it is fixed or removed.")
{
    public SettingsStatus Status { get; } = status;

    public SpecError Problem { get; } = problem;
}

/// <summary>
/// The engine settings, kept in one sparse file in the data folder, <c>settings.json</c>: <c>format</c>
/// (<see cref="SettingsLimits.Format"/>) and only the members someone set; an absent member means its default, so a
/// default is never frozen into a user's file.
/// <list type="bullet">
/// <item>Opened once at start-up (<see cref="Open(DataPaths, ILogger?)"/>). No file: the defaults, nothing written. A
/// file over <c>settingsBytes</c> (<see cref="SettingsLimits.MaxFileBytes"/>), nested deeper than <c>settingsDepth</c>
/// (64), not JSON (duplicate member names, or a string or name that doesn't decode to text included), not an object,
/// without a <c>format</c> string of this kind, or holding a number whose text rounds to ±∞ anywhere (unknown members
/// included; <c>json</c> at the number) is <see cref="SettingsStatus.Unreadable"/>; another major version is
/// <see cref="SettingsStatus.Unsupported"/>. Either way the defaults apply, the problem is logged once, the file is
/// never rewritten in this run, and every change throws <see cref="SettingsUnavailableException"/>. A known member
/// with the wrong type or out of range gets its default with one warning naming it, and the next write drops it.</item>
/// <item>Members it doesn't know are kept, at any depth, as the JSON values they were: a change sets or removes only
/// the members it names, and the write serialises the whole tree. So a newer engine's members survive a change made
/// here.</item>
/// <item><see cref="Apply"/> merges a change (a value sets a member, <c>null</c> removes it) and writes the file at once
/// with <see cref="AtomicFile"/>, under the store's lock, before it returns. A change that leaves the file's content as
/// it is (numbers compared as the doubles nearest their text, so stored spellings stay) writes nothing and raises nothing, unless the last write's folder flush failed: then it writes again (no new
/// version) and throws if that fails. A failed write leaves the file and the values as they were and throws.</item>
/// <item><see cref="Changed"/> follows every change to the stored settings, in order.</item>
/// </list>
/// The limits it keeps are the format's settings limits (spec/docs/limits.md, "Settings limits"), each refusal with its
/// <c>limit.*</c> code. A task that adds a setting adds it to <see cref="EngineSettings"/>, <see cref="SettingsLimits"/>,
/// the checks here and the API schema's <c>engineSettings</c> and <c>settingsChange</c>.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>The settings file's name in the data folder.</summary>
    public const string FileName = DataPaths.SettingsFileName;

    private static readonly Regex FormatString = new(@"\Ainfopanel\.settings/([0-9]{1,4})\z", RegexOptions.CultureInvariant);

    /// <summary>How the file and a change's result are read: no duplicate member names, at most <c>settingsDepth</c> deep.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowDuplicateProperties = false, MaxDepth = SpecLimits.Default.SettingsDepth };

    private readonly string _path;
    private readonly string? _root;
    private readonly ILogger _logger;
    private readonly IStorageFaults? _faults;
    private readonly Lock _lock = new();

    /// <summary>What the file holds (or will hold at the next write), under <see cref="_lock"/>.</summary>
    private JsonObject _tree;

    /// <summary><see cref="_tree"/> as it would be written, under <see cref="_lock"/>.</summary>
    private byte[] _content;

    private volatile SettingsSnapshot _current;

    /// <summary>
    /// Under <see cref="_lock"/>: the last write replaced the file but couldn't flush its folder, so an identical change
    /// writes <see cref="_content"/> again rather than answering at once.
    /// </summary>
    private bool _unflushed;

    private SettingsStore(string path, string? root, ILogger logger, IStorageFaults? faults, SettingsStatus status, SpecError? problem, JsonObject tree)
    {
        _path = path;
        _root = root;
        _logger = logger;
        _faults = faults;
        Status = status;
        Problem = problem;
        _tree = tree;
        _content = Serialize(tree);
        _current = new SettingsSnapshot(Effective(tree), 0);
    }

    /// <summary>The settings file's path.</summary>
    public string Path => _path;

    /// <summary>Whether the file could be read at start-up; anything but <see cref="SettingsStatus.Ok"/> refuses changes.</summary>
    public SettingsStatus Status { get; }

    /// <summary>Why the file couldn't be read (a JSON pointer into it), or null when <see cref="Status"/> is <see cref="SettingsStatus.Ok"/>.</summary>
    public SpecError? Problem { get; }

    /// <summary>The settings in effect and their version.</summary>
    public SettingsSnapshot Current => _current;

    /// <summary>
    /// Raised after each change to the stored settings, under the store's lock so the order is the changes' order. A handler must be
    /// quick and must not call <see cref="Apply"/>.
    /// </summary>
    public event Action<SettingsSnapshot>? Changed;

    /// <summary>Opens the data folder's settings file (<see cref="DataPaths.SettingsFile"/>) and removes temp files an interrupted write left.</summary>
    public static SettingsStore Open(DataPaths paths, ILogger? logger = null) => Open(paths.SettingsFile, paths.Root, logger, faults: null);

    internal static SettingsStore Open(string path, string? root, ILogger? logger, IStorageFaults? faults)
    {
        logger ??= Log.ForContext<SettingsStore>();
        path = System.IO.Path.GetFullPath(path);
        RemoveTempFiles(path, logger);

        var (status, problem, tree) = Read(path);
        if (status != SettingsStatus.Ok)
        {
            logger.Error("The settings file {File} is {Status} ({Code} at '{Path}': {Message}); the defaults apply, and it is left as it is and can't be changed until it is fixed or removed",
                path, status.ToString().ToLowerInvariant(), problem!.Code, problem.Path, problem.Message);
            tree = NewTree();
        }
        else
        {
            foreach (var (pointer, message) in Sanitize(tree!))
                logger.Warning("The settings file {File} has {Path} {Problem}; the default applies, and the next change drops it", path, pointer, message);
        }

        return new SettingsStore(path, root, logger, faults, status, problem, tree!);
    }

    /// <summary>
    /// Merges <paramref name="change"/> into the settings and writes them: a member's value sets it, <c>null</c> removes it
    /// (back to its default); <c>sources</c>, <c>sources.libre</c>, <c>media</c> and <c>clients</c> merge member by member, and each
    /// <c>clients</c> member is set or removed whole. Unknown members are refused.
    /// </summary>
    /// <returns>The settings in effect afterwards (the same version when nothing changed).</returns>
    /// <exception cref="SettingsUnavailableException">The file couldn't be read at start-up (checked first).</exception>
    /// <exception cref="SettingsChangeException">
    /// The change breaks a rule (<see cref="CheckChange"/>), or makes settings over a limit (more clients than
    /// <c>settingsClients</c>, <c>limit.settingsClients</c> at <c>/clients</c>; over <c>settingsBytes</c> as stored,
    /// <c>limit.settingsBytes</c> at the root) or that the file couldn't hold and read back (<c>json</c>: duplicate member
    /// names, text that doesn't decode); nothing was applied.
    /// </exception>
    /// <exception cref="IOException">
    /// The write failed: nothing changed. Or the file was replaced but its folder couldn't be flushed: the new values are
    /// in effect (and <see cref="Changed"/> was raised), since the file holds them.
    /// </exception>
    public SettingsSnapshot Apply(JsonElement change)
    {
        if (Status != SettingsStatus.Ok)
            throw new SettingsUnavailableException(Status, Problem!);

        IReadOnlyList<SpecError> errors;
        try
        {
            errors = CheckChange(change, SpecLimits.Default);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw Unstorable(ex.Message);
        }

        if (errors.Count > 0)
            throw new SettingsChangeException(errors);

        lock (_lock)
        {
            JsonObject next;
            byte[] content;
            try
            {
                next = (JsonObject)_tree.DeepClone();
                Merge(next, change);

                if (next["clients"] is JsonObject clients && clients.Count > SettingsLimits.Clients)
                    throw new SettingsChangeException([new SpecError("/clients", "limit.settingsClients", $"At most {SettingsLimits.Clients} clients keep preferences (settingsClients); this change would make {clients.Count}.")]);

                content = Serialize(next);
                if (content.Length > SettingsLimits.MaxFileBytes)
                    throw new SettingsChangeException([new SpecError("", "limit.settingsBytes", $"The settings would take {content.Length} bytes as stored; at most {SettingsLimits.MaxFileBytes} are kept (settingsBytes).")]);

                // Only what the next start can read is written: read the result back as the file is read.
                ParseTree(content, out var problem);
                if (problem != null)
                    throw Unstorable(problem.Message);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Text that doesn't encode (a lone surrogate), or JSON deeper than a change can be parsed.
                throw Unstorable(ex.Message);
            }

            if (SameContent(content, _content))
            {
                // Nothing changes, and the members keep the spellings they have (_tree stays as it is).
                if (!_unflushed)
                    return _current;

                // The last write replaced the file but couldn't flush its folder: write it again, so the retry flushes
                // what that write didn't (no new version, nothing raised), or throws as it did.
                AtomicFile.Write(_path, _content, _root, _faults);
                _unflushed = false;
                return _current;
            }

            var replaced = false;
            try
            {
                AtomicFile.Write(_path, content, _root, _faults, replaced: () => replaced = true);
            }
            catch when (replaced)
            {
                // The file holds the new settings; only flushing its folder failed. They are what the next start reads.
                _unflushed = true;
                Commit(next, content);
                throw;
            }

            _unflushed = false;
            return Commit(next, content);
        }
    }

    /// <summary>
    /// Whether two written forms of the settings hold the same content: equal JSON as the formats compare it, numbers as
    /// the doubles nearest their text (spec/docs/validation.md#numbers), so <c>30.0</c> and <c>30</c> are one value.
    /// </summary>
    private static bool SameContent(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b))
            return true;
        using var x = JsonDocument.Parse(a, ReadOptions);
        using var y = JsonDocument.Parse(b, ReadOptions);
        return Json.DeepEquals(x.RootElement, y.RootElement);
    }

    /// <summary>A change whose result the file couldn't hold, or the next start couldn't read back.</summary>
    private static SettingsChangeException Unstorable(string reason) =>
        new([new SpecError("", "json", $"The settings this change makes couldn't be stored and read back: {reason}")]);

    /// <summary>Under <see cref="_lock"/>: the new tree is in effect.</summary>
    private SettingsSnapshot Commit(JsonObject tree, byte[] content)
    {
        _tree = tree;
        _content = content;
        var snapshot = new SettingsSnapshot(Effective(tree), _current.Version + 1);
        _current = snapshot;
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private static JsonObject NewTree() => new() { ["format"] = SettingsLimits.Format };

    private static (SettingsStatus Status, SpecError? Problem, JsonObject? Tree) Read(string path)
    {
        static (SettingsStatus, SpecError?, JsonObject?) Unreadable(string pointer, string code, string message) =>
            (SettingsStatus.Unreadable, new SpecError(pointer, code, message), null);

        switch (RegularFile.StateOf(path))
        {
            case PathState.Folder:
                return Unreadable("", "io", "A folder is where the settings file belongs.");
            case PathState.Absent:
                return (SettingsStatus.Ok, null, NewTree());
        }

        byte[] bytes;
        try
        {
            // A regular file only: a FIFO or device there is unreadable, without waiting on it.
            using var stream = RegularFile.OpenRead(path);
            if (stream.Length > SettingsLimits.MaxFileBytes)
                return Unreadable("", "limit.settingsBytes", $"The file has {stream.Length} bytes; at most {SettingsLimits.MaxFileBytes} are read.");
            using var buffer = new MemoryStream();
            var chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > SettingsLimits.MaxFileBytes)
                        return Unreadable("", "limit.settingsBytes", $"The file has more than {SettingsLimits.MaxFileBytes} bytes.");
                    buffer.Write(chunk, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable("", "io", ex.Message);
        }

        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            json = json[3..];

        // Its nesting first, as the formats' JSON-level pass checks it: deeper than the settings are read at is the
        // limit's code, not json. Text that isn't JSON at all is the parse's to report.
        if (DepthOf(json) is { } depth && depth > ReadOptions.MaxDepth)
            return Unreadable("", "limit.settingsDepth", $"The settings nest {depth} levels deep; they are read at most {ReadOptions.MaxDepth} deep (settingsDepth).");

        var root = ParseTree(json, out var parseProblem);
        if (parseProblem != null)
            return (SettingsStatus.Unreadable, parseProblem, null);

        if (root is not JsonObject tree)
            return Unreadable("", "schema", "The settings are not a JSON object.");
        if (tree["format"] is not JsonValue formatValue || formatValue.GetValueKind() != JsonValueKind.String)
            return Unreadable("/format", "schema", "The settings have no format string.");

        var format = formatValue.GetValue<string>();
        var match = FormatString.Match(format);
        if (!match.Success)
            return Unreadable("/format", "schema", $"'{Clip(format)}' is not a settings format.");
        if (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            return (SettingsStatus.Unsupported,
                new SpecError("/format", "unsupported.format", $"The settings are {Clip(format)}; this engine reads {SettingsLimits.Format}."), null);
        }

        // A number that rounds to ±∞ makes the file unreadable wherever it is, unknown members included, as it makes a
        // stored document unreadable (spec/docs/validation.md#numbers).
        if (FirstInfiniteNumber(tree, "") is { } infinite)
            return Unreadable(infinite, "json", InfiniteMessage);

        return (SettingsStatus.Ok, null, tree);
    }

    /// <summary>How deeply <paramref name="json"/> nests, as <c>jsonDepth</c> counts it, read without recursion; null when it isn't JSON.</summary>
    private static int? DepthOf(ReadOnlySpan<byte> json)
    {
        try
        {
            return SpecJson.Depth(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses settings JSON as the file is read (<see cref="ReadOptions"/>: no duplicate member names, at most 64 deep) and
    /// decodes every string and member name in it once, so text that doesn't decode (a lone surrogate escape) is found
    /// here rather than when a member is first read. Anything that fails is a <c>json</c> problem at the root.
    /// </summary>
    private static JsonNode? ParseTree(ReadOnlySpan<byte> json, out SpecError? problem)
    {
        try
        {
            var root = JsonNode.Parse(json, documentOptions: ReadOptions);
            if (root != null)
            {
                // Writing unescapes every string and name, and throws on one that doesn't decode.
                using var writer = new Utf8JsonWriter(Stream.Null);
                root.WriteTo(writer);
            }

            problem = null;
            return root;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            problem = new SpecError("", "json", ex.Message);
            return null;
        }
    }

    /// <summary>Removes each known member that breaks its rule, and says which and why.</summary>
    private static List<(string Path, string Message)> Sanitize(JsonObject tree)
    {
        var problems = new List<(string, string)>();

        void Check(JsonObject parent, string name, string pointer, Func<JsonElement, string?> rule)
        {
            if (!parent.TryGetPropertyValue(name, out var node))
                return;
            var problem = rule(ElementOf(node));
            if (problem == null)
                return;
            parent.Remove(name);
            problems.Add((pointer, problem));
        }

        JsonObject? Container(JsonObject parent, string name, string pointer)
        {
            if (!parent.TryGetPropertyValue(name, out var node))
                return null;
            if (node is JsonObject child)
                return child;
            parent.Remove(name);
            problems.Add((pointer, "that isn't an object"));
            return null;
        }

        Check(tree, "frameRate", "/frameRate", e => IntProblem(e, SettingsLimits.FrameRate));
        Check(tree, "graphRate", "/graphRate", e => IntProblem(e, SettingsLimits.GraphRate));
        Check(tree, "logLevel", "/logLevel", LogLevelProblem);
        if (Container(tree, "sources", "/sources") is { } sources && Container(sources, "libre", "/sources/libre") is { } libre)
        {
            Check(libre, "enabled", "/sources/libre/enabled", BoolProblem);
            Check(libre, "storage", "/sources/libre/storage", BoolProblem);
            Check(libre, "storageInterval", "/sources/libre/storageInterval", e => IntProblem(e, SettingsLimits.StorageInterval));
        }

        if (Container(tree, "media", "/media") is { } media)
            Check(media, "localNetwork", "/media/localNetwork", BoolProblem);

        if (Container(tree, "clients", "/clients") is { } clients)
        {
            var kept = 0;
            foreach (var (name, node) in clients.ToList())
            {
                var pointer = "/clients/" + Escape(name);
                var problem = !SettingsLimits.IsClientName(name) ? "whose name isn't a client name"
                    : node is null ? "that is null"
                    : ClientBytesProblem(ElementOf(node), SettingsLimits.ClientBytes)
                    ?? (kept == SettingsLimits.Clients ? $"beyond the first {SettingsLimits.Clients} clients" : null);
                if (problem == null)
                {
                    kept++;
                    continue;
                }

                clients.Remove(name);
                problems.Add((pointer, problem));
            }
        }

        return problems;
    }

    /// <summary>The settings a (sanitised) tree holds, with the default for each absent member.</summary>
    private static EngineSettings Effective(JsonObject tree)
    {
        var libre = (tree["sources"] as JsonObject)?["libre"] as JsonObject;
        var media = tree["media"] as JsonObject;
        var clients = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        if (tree["clients"] is JsonObject stored)
        {
            foreach (var (name, node) in stored)
                clients[name] = ElementOf(node).Clone();
        }

        return new EngineSettings(
            Int(tree["frameRate"]) ?? SettingsLimits.FrameRate.Default,
            Int(tree["graphRate"]) ?? SettingsLimits.GraphRate.Default,
            tree["logLevel"] is { } level ? SettingsLimits.LogLevelNamed(ElementOf(level).GetString())!.Value : SettingsLimits.LogLevel,
            new LibreSettings(
                Bool(libre?["enabled"]) ?? LibreSettings.Default.Enabled,
                Bool(libre?["storage"]) ?? LibreSettings.Default.Storage,
                Int(libre?["storageInterval"]) ?? LibreSettings.Default.StorageInterval),
            new MediaSettings(Bool(media?["localNetwork"]) ?? MediaSettings.Default.LocalNetwork),
            clients.ToImmutable());

        static int? Int(JsonNode? node) => node is null ? null : SpecJson.ReadInt32(ElementOf(node));

        static bool? Bool(JsonNode? node) => node is null ? null : ElementOf(node).GetBoolean();
    }

    // ---- changing ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every rule <paramref name="change"/> breaks on its own, each at its path, in the formats' order: its nesting first
    /// (deeper than <c>settingsDepth</c> is <c>limit.settingsDepth</c> at the root, and nothing else is checked); then a
    /// number whose text rounds to ±∞ anywhere in it, client preferences and unknown members included (<c>json</c> at the
    /// number, and nothing else is checked); then each member, as the API schema's <c>settingsChange</c> has it
    /// (<c>schema</c>), and each client's preferences within <c>settingsClientBytes</c> on their canonical form
    /// (<c>limit.settingsClientBytes</c> at <c>/clients/{name}</c>). <paramref name="limits"/> are the store's
    /// <see cref="SpecLimits.Default"/>; a test may tighten them.
    /// </summary>
    internal static IReadOnlyList<SpecError> CheckChange(JsonElement change, SpecLimits limits)
    {
        var errors = new List<SpecError>();

        void Leaf(JsonElement value, string pointer, Func<JsonElement, string?> rule)
        {
            if (value.ValueKind != JsonValueKind.Null && rule(value) is { } problem)
                errors.Add(new SpecError(pointer, "schema", $"The value {problem}."));
        }

        bool Object(JsonElement value, string pointer)
        {
            if (value.ValueKind == JsonValueKind.Object)
                return true;
            errors.Add(new SpecError(pointer, "schema", "The value isn't an object."));
            return false;
        }

        void Unknown(string pointer) => errors.Add(new SpecError(pointer, "schema", "Not a setting."));

        // Bounded first: a change nested deeper than the settings are read at can't be stored whatever it holds (a change
        // nests as the settings it makes do), and the scan below descends once per level.
        var depth = SpecJson.Depth(change);
        if (depth > limits.SettingsDepth)
        {
            errors.Add(new SpecError("", "limit.settingsDepth", $"The change nests {depth} levels deep; the settings are read at most {limits.SettingsDepth} deep (settingsDepth)."));
            return errors;
        }

        // Numbers next, whatever the change is and everywhere in it (client preferences and unknown members too): one that rounds to ±∞ is
        // json at the number, and then no other rule is applied, as the formats' validator does.
        InfiniteNumbers(change, new List<(string? Name, int Index)>(), errors);
        if (errors.Count > 0)
            return errors;

        if (change.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SpecError("", "schema", "A settings change is a JSON object."));
            return errors;
        }

        foreach (var member in change.EnumerateObject())
        {
            var pointer = "/" + Escape(member.Name);
            switch (member.Name)
            {
                case "frameRate":
                    Leaf(member.Value, pointer, e => IntProblem(e, SettingsLimits.FrameRate));
                    break;
                case "graphRate":
                    Leaf(member.Value, pointer, e => IntProblem(e, SettingsLimits.GraphRate));
                    break;
                case "logLevel":
                    Leaf(member.Value, pointer, LogLevelProblem);
                    break;
                case "sources":
                    if (!Object(member.Value, pointer))
                        break;
                    foreach (var source in member.Value.EnumerateObject())
                    {
                        var sourcePointer = pointer + "/" + Escape(source.Name);
                        if (source.Name != "libre")
                        {
                            Unknown(sourcePointer);
                            continue;
                        }

                        if (!Object(source.Value, sourcePointer))
                            continue;
                        foreach (var setting in source.Value.EnumerateObject())
                        {
                            var settingPointer = sourcePointer + "/" + Escape(setting.Name);
                            switch (setting.Name)
                            {
                                case "enabled" or "storage":
                                    Leaf(setting.Value, settingPointer, BoolProblem);
                                    break;
                                case "storageInterval":
                                    Leaf(setting.Value, settingPointer, e => IntProblem(e, SettingsLimits.StorageInterval));
                                    break;
                                default:
                                    Unknown(settingPointer);
                                    break;
                            }
                        }
                    }

                    break;
                case "media":
                    if (!Object(member.Value, pointer))
                        break;
                    foreach (var setting in member.Value.EnumerateObject())
                    {
                        var settingPointer = pointer + "/" + Escape(setting.Name);
                        if (setting.Name == "localNetwork")
                            Leaf(setting.Value, settingPointer, BoolProblem);
                        else
                            Unknown(settingPointer);
                    }

                    break;
                case "clients":
                    if (!Object(member.Value, pointer))
                        break;
                    foreach (var client in member.Value.EnumerateObject())
                    {
                        var clientPointer = pointer + "/" + Escape(client.Name);
                        if (!SettingsLimits.IsClientName(client.Name))
                        {
                            errors.Add(new SpecError(clientPointer, "schema",
                                $"A client name is 1–{SettingsLimits.ClientNameLength} of A–Z, a–z, 0–9, '.', '_' and '-'."));
                        }
                        else if (client.Value.ValueKind != JsonValueKind.Null && ClientBytesProblem(client.Value, limits.SettingsClientBytes) is { } problem)
                        {
                            errors.Add(new SpecError(clientPointer, "limit.settingsClientBytes", $"The preferences {problem}."));
                        }
                    }

                    break;
                default:
                    Unknown(pointer);
                    break;
            }
        }

        return errors;
    }

    /// <summary>Applies a checked change to <paramref name="tree"/>, removing the containers it empties.</summary>
    private static void Merge(JsonObject tree, JsonElement change)
    {
        foreach (var member in change.EnumerateObject())
        {
            switch (member.Name)
            {
                case "frameRate" or "graphRate":
                    Set(tree, member.Name, member.Value, e => JsonValue.Create(SpecJson.ReadInt32(e)));
                    break;
                case "logLevel":
                    Set(tree, member.Name, member.Value, e => JsonValue.Create(e.GetString()));
                    break;
                case "sources":
                    var sources = Child(tree, "sources");
                    foreach (var source in member.Value.EnumerateObject())
                    {
                        var libre = Child(sources, source.Name);
                        foreach (var setting in source.Value.EnumerateObject())
                        {
                            if (setting.Name == "storageInterval")
                                Set(libre, setting.Name, setting.Value, e => JsonValue.Create(SpecJson.ReadInt32(e)));
                            else
                                Set(libre, setting.Name, setting.Value, e => JsonValue.Create(e.GetBoolean()));
                        }

                        PruneIfEmpty(sources, source.Name);
                    }

                    PruneIfEmpty(tree, "sources");
                    break;
                case "media":
                    var media = Child(tree, "media");
                    foreach (var setting in member.Value.EnumerateObject())
                        Set(media, setting.Name, setting.Value, e => JsonValue.Create(e.GetBoolean()));
                    PruneIfEmpty(tree, "media");
                    break;
                case "clients":
                    var clients = Child(tree, "clients");
                    foreach (var client in member.Value.EnumerateObject())
                        Set(clients, client.Name, client.Value, e => JsonNode.Parse(e.GetRawText()));
                    PruneIfEmpty(tree, "clients");
                    break;
            }
        }

        static void Set(JsonObject parent, string name, JsonElement value, Func<JsonElement, JsonNode?> node)
        {
            if (value.ValueKind == JsonValueKind.Null)
                parent.Remove(name);
            else
                parent[name] = node(value);
        }

        static JsonObject Child(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject child)
                return child;
            child = new JsonObject();
            parent[name] = child;
            return child;
        }

        static void PruneIfEmpty(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject { Count: 0 })
                parent.Remove(name);
        }
    }

    // ---- rules ------------------------------------------------------------------------------------------------------

    /// <summary>A whole number in range, read as the double nearest its text as the schema validator reads it (spec/docs/validation.md#numbers).</summary>
    private static string? IntProblem(JsonElement e, SettingRange range)
    {
        if (e.ValueKind != JsonValueKind.Number || !JsonNumber.TryReadInteger(e, out var value))
            return "isn't a whole number";
        return value >= range.Min && value <= range.Max
            ? null
            : $"{Clip(e.GetRawText())} is outside {range.Min}–{range.Max}";
    }

    private static string? BoolProblem(JsonElement e) =>
        e.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "isn't true or false";

    private static string? LogLevelProblem(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && SettingsLimits.LogLevelNamed(e.GetString()) != null
            ? null
            : "isn't one of debug, information, warning and error";

    /// <summary>
    /// A client's preferences over <paramref name="limit"/> bytes on their canonical form (spec/docs/limits.md#sizes), the
    /// size every content limit measures: the same however the value was sent or is stored, so a change and the next
    /// start measure it alike.
    /// </summary>
    private static string? ClientBytesProblem(JsonElement e, int limit)
    {
        var bytes = CanonicalJson.Size(e);
        return bytes <= limit ? null : $"take {bytes} bytes on their canonical form; at most {limit} are kept (settingsClientBytes)";
    }

    // ---- numbers ----------------------------------------------------------------------------------------------------

    private const string InfiniteMessage = "The number rounds to infinity as a 64-bit double: not a number the settings read.";

    /// <summary>
    /// Adds a <c>json</c> error for every number in <paramref name="value"/> whose text rounds to ±∞, at its pointer. The
    /// caller bounds the depth; <paramref name="path"/> holds the steps down to <paramref name="value"/> (a member name, or
    /// null with an index) and is turned into a pointer only for an error.
    /// </summary>
    private static void InfiniteNumbers(JsonElement value, List<(string? Name, int Index)> path, List<SpecError> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!JsonNumber.TryRead(value, out _))
                    errors.Add(new SpecError(PointerOf(path), "json", InfiniteMessage));
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    path.Add((null, i++));
                    InfiniteNumbers(item, path, errors);
                    path.RemoveAt(path.Count - 1);
                }

                break;
            case JsonValueKind.Object:
                foreach (var member in value.EnumerateObject())
                {
                    path.Add((member.Name, 0));
                    InfiniteNumbers(member.Value, path, errors);
                    path.RemoveAt(path.Count - 1);
                }

                break;
        }
    }

    private static string PointerOf(List<(string? Name, int Index)> path)
    {
        var pointer = new StringBuilder();
        foreach (var (name, index) in path)
            pointer.Append('/').Append(name is null ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : Escape(name));
        return pointer.ToString();
    }

    /// <summary>The pointer of the first number in <paramref name="node"/> whose text rounds to ±∞, or null.</summary>
    private static string? FirstInfiniteNumber(JsonNode? node, string pointer)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                    if (FirstInfiniteNumber(child, pointer + "/" + Escape(name)) is { } found)
                        return found;
                return null;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (FirstInfiniteNumber(a[i], pointer + "/" + i) is { } found)
                        return found;
                return null;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                return JsonNumber.TryRead(ElementOf(v), out _) ? null : pointer;
            default:
                return null;
        }
    }

    // ---- JSON -------------------------------------------------------------------------------------------------------

    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>A node as a JSON element: the one it was read from when it has one, else its serialised form.</summary>
    private static JsonElement ElementOf(JsonNode? node)
    {
        if (node is null)
            return Null;
        if (node is JsonValue value && value.TryGetValue<JsonElement>(out var element))
            return element;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>How the file is written.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private static byte[] Serialize(JsonObject tree)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            tree.WriteTo(writer);
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Escapes one reference token for a JSON pointer.</summary>
    private static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");

    private static string Clip(string text) => text.Length <= 64 ? text : text[..64] + "…";

    // ---- temp files -------------------------------------------------------------------------------------------------

    /// <summary>Removes <c>settings.json.{32 hex}.tmp</c> files an interrupted write left; they are never read.</summary>
    private static void RemoveTempFiles(string path, ILogger logger)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileName(path);

        IEnumerable<string> temps;
        try
        {
            temps = RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}"))
                .Where(t => t is FileInfo && AtomicFile.IsTempFile(t.Name, target => target == name))
                .Select(t => t.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Couldn't look for temp files beside {File}", path);
            return;
        }

        foreach (var temp in temps)
        {
            try
            {
                File.Delete(temp);
                logger.Information("Removed {Temp}, left by an interrupted write of the settings", temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Couldn't remove {Temp}, left by an interrupted write of the settings", temp);
            }
        }
    }
}
