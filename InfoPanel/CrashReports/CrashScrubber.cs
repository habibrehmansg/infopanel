using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sentry;

namespace InfoPanel.CrashReporting;

/// <summary>A value the scrubber replaces wherever it appears in free text, with the label it becomes.</summary>
public readonly record struct KnownValue(string Value, string Label);

/// <summary>
/// What a crash report may carry (docs/2.0/crash-reports.md): every event is rebuilt from an allowlist of its fields, and
/// carries no free text. An exception is its type and its stack frames, never its message (inner exceptions the same);
/// the event's message and the breadcrumbs' are the engine's own texts only (its fixed messages and its log templates,
/// never a template's values); tags only the engine's fixed values. What text remains (templates, types, frames, module
/// names, file names, sources) goes through a second net (<see cref="ScrubText"/>): URL credentials and marked values,
/// whole paths, the values it knows (the user's and computer's names, folders, profile, plugin, panel and sensor names),
/// e-mail addresses, local host names, MAC and IP addresses, token-like values, quoted values, cut at 300 characters.
/// </summary>
public static partial class CrashScrubber
{
    /// <summary>The longest free text kept, in characters.</summary>
    public const int MaxText = 300;

    /// <summary>The tags an event may carry, each with the values it may have: the engine's own, never a string from the user's data.</summary>
    private static readonly Dictionary<string, Regex> TagValues = new(StringComparer.Ordinal)
    {
        ["process"] = new(@"\Aengine\z"),
        ["os"] = new(@"\A(?:macos|windows|linux)\z"),
        ["exit_code"] = new(@"\A(?:-?[0-9]{1,10}|unknown)\z"),
        ["ran"] = new(@"\A(?:under 10 s|10 s to 1 min|1 to 10 min|10 min to 1 h|over 1 h)\z"),
        ["restarts"] = new(@"\A[0-9]{1,10}\z"),
    };

    /// <summary>The tags an event may carry.</summary>
    public static readonly IReadOnlySet<string> Tags = TagValues.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>A run with fewer different characters than this is no token, whatever its length (a ruler of dashes, padding).</summary>
    private const int MinTokenVariety = 5;

    /// <summary>Known values shorter than this are replaced only as whole words (a short user name inside an ordinary word stays).</summary>
    private const int WholeWordBelow = 3;

    /// <summary>
    /// The SDK's own mark of an unhandled exception that doesn't end the process (an unobserved task's): not written to JSON,
    /// so carried across the rebuild, or the SDK would end the session as crashed while the engine runs on.
    /// </summary>
    private static readonly System.Reflection.MethodInfo? SetTerminal =
        typeof(Sentry.Protocol.Mechanism).GetProperty(nameof(Sentry.Protocol.Mechanism.Terminal))?.GetSetMethod(nonPublic: true);

    /// <summary>
    /// The event rebuilt from its allowlisted fields: each exception's type, module, mechanism and frames (function,
    /// module, line, column, in-app flag and file name after the build root), never its message; the message only when
    /// <paramref name="engineText"/> says it is the engine's own (none without it); the level, release, environment,
    /// platform, time, id and SDK; the allowlisted tags with their allowed values; the contexts <c>os</c> and
    /// <c>runtime</c> (name, version), <c>app</c> (version) and <c>device</c> (architecture); breadcrumbs (the message
    /// when the engine's own, category, level, type, time and an exception type); the user as <paramref name="installId"/>
    /// only. Everything else is dropped. The text kept goes through <see cref="ScrubText"/> all the same.
    /// </summary>
    public static SentryEvent Scrub(SentryEvent @event, string? installId, IReadOnlyList<KnownValue> known, Func<string, bool>? engineText = null)
    {
        var source = ToJson(@event);
        var kept = new JsonObject();
        foreach (var name in new[] { "event_id", "timestamp", "platform", "level", "release", "environment" })
        {
            if (source[name] is JsonValue value)
                kept[name] = value.DeepClone();
        }

        if (source["sdk"] is JsonObject sdk)
            kept["sdk"] = sdk.DeepClone();

        // The message, under the member the SDK wrote it in (the protocol's logentry, or message), its text only (no
        // parameters), and only the engine's own: a fixed message or a log template.
        foreach (var name in new[] { "logentry", "message" })
        {
            if (Text(source[name]) is { } message && engineText?.Invoke(message) == true)
                kept[name] = new JsonObject { ["message"] = ScrubText(message, known) };
        }

        if (source["exception"]?["values"] is JsonArray exceptions)
            kept["exception"] = new JsonObject { ["values"] = new JsonArray([.. exceptions.OfType<JsonObject>().Select(e => (JsonNode)Exception(e, known))]) };

        if (source["tags"] is JsonObject tags)
        {
            var keptTags = new JsonObject();
            foreach (var (key, value) in tags)
            {
                if (TagValues.TryGetValue(key, out var allowed) && Text(value) is { } text && allowed.IsMatch(text))
                    keptTags[key] = text;
            }

            if (keptTags.Count > 0)
                kept["tags"] = keptTags;
        }

        if (source["contexts"] is JsonObject contexts)
            kept["contexts"] = Contexts(contexts);

        if (Breadcrumbs(source["breadcrumbs"], known, engineText) is { } breadcrumbs)
            kept["breadcrumbs"] = breadcrumbs;

        if (installId != null)
            kept["user"] = new JsonObject { ["id"] = installId };

        using var document = JsonDocument.Parse(kept.ToJsonString());
        var scrubbed = SentryEvent.FromJson(document.RootElement);
        CarryTerminal(@event, scrubbed);
        return scrubbed;
    }

    /// <summary>Each exception's <see cref="Sentry.Protocol.Mechanism.Terminal"/>, from <paramref name="from"/> to its rebuilt copy in <paramref name="to"/> (the same order).</summary>
    private static void CarryTerminal(SentryEvent from, SentryEvent to)
    {
        var source = from.SentryExceptions?.ToList() ?? [];
        var target = to.SentryExceptions?.ToList() ?? [];
        for (var i = 0; i < Math.Min(source.Count, target.Count); i++)
        {
            if (source[i].Mechanism?.Terminal is { } terminal && target[i].Mechanism is { } mechanism)
                SetTerminal?.Invoke(mechanism, [terminal]);
        }
    }

    /// <summary>The event as the SDK writes it.</summary>
    internal static JsonObject ToJson(SentryEvent @event)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            @event.WriteTo(writer, null);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }

    private static JsonObject Exception(JsonObject source, IReadOnlyList<KnownValue> known)
    {
        // Never its message (the value): only its type, its module and where it was thrown.
        var kept = new JsonObject();
        if (Text(source["type"]) is { } type)
            kept["type"] = Identifier(type);
        if (Text(source["module"]) is { } module)
            kept["module"] = Identifier(module);
        if (source["mechanism"] is JsonObject mechanism)
        {
            var keptMechanism = new JsonObject();
            if (Text(mechanism["type"]) is { } kind)
                keptMechanism["type"] = kind;
            if (mechanism["handled"] is JsonValue handled)
                keptMechanism["handled"] = handled.DeepClone();
            kept["mechanism"] = keptMechanism;
        }

        if (source["stacktrace"]?["frames"] is JsonArray frames)
        {
            var keptFrames = new JsonArray();
            foreach (var frame in frames.OfType<JsonObject>())
            {
                var keptFrame = new JsonObject();
                foreach (var name in new[] { "function", "module" })
                {
                    if (Text(frame[name]) is { } text)
                        keptFrame[name] = Identifier(text);
                }

                foreach (var name in new[] { "lineno", "colno", "in_app" })
                {
                    if (frame[name] is JsonValue number)
                        keptFrame[name] = number.DeepClone();
                }

                if (Text(frame["filename"]) is { } file)
                    keptFrame["filename"] = FileName(file, known);
                keptFrames.Add(keptFrame);
            }

            kept["stacktrace"] = new JsonObject { ["frames"] = keptFrames };
        }

        return kept;
    }

    /// <summary>A frame's file name: after the deterministic build root <c>/_/</c>, else scrubbed as free text (a path becomes <c>&lt;path&gt;</c> with its extension).</summary>
    internal static string FileName(string file, IReadOnlyList<KnownValue> known)
    {
        var root = file.Replace('\\', '/').IndexOf("/_/", StringComparison.Ordinal);
        return root >= 0 ? ScrubText(file.Replace('\\', '/')[(root + 3)..], known) : ScrubText(file, known);
    }

    private static JsonObject Contexts(JsonObject source)
    {
        var kept = new JsonObject();
        foreach (var (name, members) in new (string, string[])[] { ("os", ["name", "version"]), ("runtime", ["name", "version"]), ("app", ["app_version"]), ("device", ["arch"]) })
        {
            if (source[name] is not JsonObject context)
                continue;
            var keptContext = new JsonObject();
            if (Text(context["type"]) is { } type)
                keptContext["type"] = type;
            foreach (var member in members)
            {
                if (Text(context[member]) is { } text)
                    keptContext[member] = text;
            }

            kept[name] = keptContext;
        }

        return kept;
    }

    private static JsonNode? Breadcrumbs(JsonNode? source, IReadOnlyList<KnownValue> known, Func<string, bool>? engineText)
    {
        // The SDK writes them as an array; the protocol also allows { "values": [...] }.
        var list = source as JsonArray ?? source?["values"] as JsonArray;
        if (list == null)
            return null;
        var kept = new JsonArray();
        foreach (var crumb in list.OfType<JsonObject>())
        {
            var keptCrumb = new JsonObject();
            if (crumb["timestamp"] is JsonValue time)
                keptCrumb["timestamp"] = time.DeepClone();
            if (Text(crumb["message"]) is { } message && engineText?.Invoke(message) == true)
                keptCrumb["message"] = ScrubText(message, known);
            // Its source and its exception's type are type names (the log sink's): kept only in that shape.
            if (Text(crumb["category"]) is { } category && TypeName().IsMatch(category))
                keptCrumb["category"] = Identifier(category);
            foreach (var name in new[] { "level", "type" })
            {
                if (Text(crumb[name]) is { } text && Word().IsMatch(text))
                    keptCrumb[name] = text;
            }

            if (Text(crumb["data"]?[TemplateBreadcrumbSink.ExceptionTypeKey]) is { } exceptionType && TypeName().IsMatch(exceptionType))
                keptCrumb["data"] = new JsonObject { [TemplateBreadcrumbSink.ExceptionTypeKey] = Identifier(exceptionType) };
            kept.Add(keptCrumb);
        }

        return source is JsonArray ? kept : new JsonObject { ["values"] = kept };
    }

    /// <summary>
    /// A name from the engine's code (a type, a module, a function, a log's source) through the rules but not the known
    /// values: those are the user's words, and a sensor group called <c>System</c> would otherwise rewrite every type.
    /// </summary>
    private static string Identifier(string name) => ScrubText(name, []);

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject message => Text(message["formatted"]) ?? Text(message["message"]),
        _ => null,
    };

    // ---- free text ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="text"/> scrubbed (the second net over what an event still carries), in this order: a URL's user
    /// information (<c>rtsp://user:password@</c>) becomes <c>&lt;credentials&gt;</c>; the value after a marker
    /// (<c>Bearer</c>, <c>Basic</c> in any case, <c>token=</c>, <c>password:</c> and the like, <see cref="MarkedToken"/>)
    /// whatever its characters becomes <c>&lt;token&gt;</c>; whole absolute paths (Windows, UNC, POSIX and
    /// <c>~</c>-relative, spaces and all: a path's plain words belong to it up to a later word that holds a separator or
    /// is a file name with an extension) become <c>&lt;path&gt;</c> with the file's extension; the known values become
    /// their labels (longest first, in any case; one shorter than 3 characters only as a whole word); then e-mail
    /// addresses, host names under <c>ts.net</c>, <c>local</c> and <c>lan</c>, MAC addresses, IPv4 and IPv6 addresses
    /// (loopback kept as <c>loopback</c>); runs of 20 or more letters, digits, <c>-</c> and <c>_</c> unless they hold fewer
    /// than <see cref="MinTokenVariety"/> different characters (a ruler of dashes), or read as words and aren't hex (no
    /// digit, each part between <c>-</c> and <c>_</c> lower-case of at most 15 letters, CamelCase or upper-case:
    /// <c>InvalidOperationException</c> stays, a 20-letter lower-case word and <c>CafeBabeDeadBeefCafeBabe</c> go); then
    /// quoted values become <c>&lt;value&gt;</c>; cut at <see cref="MaxText"/> characters. What each step put in is never
    /// changed by a later one but a quoted value's (a known name never rewrites a label).
    /// </summary>
    public static string ScrubText(string text, IReadOnlyList<KnownValue> known)
    {
        var labels = new List<string>();
        // A label as a run of private-use characters no rule matches, put back at the end.
        string Label(string label)
        {
            if (labels.Count >= LabelCodes)
                return label;
            labels.Add(label);
            return string.Concat(LabelStart, (char)(LabelCode + labels.Count - 1), LabelEnd);
        }

        text = text.Replace(LabelStart, '\uFFFD').Replace(LabelEnd, '\uFFFD');
        text = UrlCredentials().Replace(text, m => m.Groups["scheme"].Value + Label("<credentials>") + "@");
        text = MarkedToken().Replace(text, m => m.Groups["marker"].Value + Label("<token>"));
        text = WindowsPath().Replace(text, m => PathLabel(m, Label));
        text = UnixPath().Replace(text, m => PathLabel(m, Label));

        foreach (var value in known.Where(k => !string.IsNullOrWhiteSpace(k.Value)).OrderByDescending(k => k.Value.Length))
        {
            var label = Label(value.Label);
            text = value.Value.Length >= WholeWordBelow
                ? text.Replace(value.Value, label, StringComparison.OrdinalIgnoreCase)
                : Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value.Value)}(?![\p{{L}}\p{{N}}])", label.Replace("$", "$$", StringComparison.Ordinal),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = Email().Replace(text, _ => Label("<email>"));
        text = LocalHost().Replace(text, _ => Label("<host>"));
        text = Mac().Replace(text, _ => Label("<mac>"));
        text = Ipv4().Replace(text, m => Label(m.Value.StartsWith("127.", StringComparison.Ordinal) ? "loopback" : "<ip>"));
        text = Ipv6().Replace(text, m => m.Value == "::1" ? Label("loopback") : m.Value.Contains("::", StringComparison.Ordinal) || m.Value.Count(c => c == ':') >= 5 ? Label("<ip>") : m.Value);
        text = TokenLike().Replace(text, m => m.Value.Distinct().Count() < MinTokenVariety || (!LongHex().IsMatch(m.Value) && ReadsAsWords(m.Value)) ? m.Value : Label("<token>"));
        text = Quoted().Replace(text, "<value>");
        text = PlacedLabel().Replace(text, m => labels[m.Groups["code"].Value[0] - LabelCode]);
        return text.Length <= MaxText ? text : string.Concat(text.AsSpan(0, MaxText - 1), "…");
    }

    // A label placed in the text while it is scrubbed: these private-use characters, the code between them its index.
    private const char LabelStart = '\uE000';
    private const char LabelEnd = '\uE001';
    private const char LabelCode = '\uE100';
    private const int LabelCodes = 0x0700;

    [GeneratedRegex("\uE000(?<code>[\uE100-\uE7FF])\uE001")]
    private static partial Regex PlacedLabel();

    /// <summary>A URL's user information: <c>scheme://user:password@</c> or <c>scheme://user@</c>.</summary>
    [GeneratedRegex(@"(?<scheme>(?<![\p{L}\p{N}+.-])[A-Za-z][A-Za-z0-9+.-]{0,31}://)[^\s/?#@'""“”<>]+@")]
    private static partial Regex UrlCredentials();

    /// <summary>A run of 16 or more hexadecimal digits and nothing else.</summary>
    [GeneratedRegex(@"\A[0-9A-Fa-f]{16,}\z")]
    private static partial Regex LongHex();

    /// <summary>A type's full name, as a log's source and a logged exception's type are: dotted identifiers, nested (<c>+</c>) and generic (<c>`1</c>) ones included.</summary>
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*(?:`[0-9]{1,2})?\z")]
    private static partial Regex TypeName();

    /// <summary>A breadcrumb's level or type: one lower-case word.</summary>
    [GeneratedRegex(@"\A[a-z]{1,32}\z")]
    private static partial Regex Word();

    /// <summary>Whether a long run reads as the engine's own words: no digit, each part between <c>-</c> and <c>_</c> a word (<see cref="WordPart"/>).</summary>
    private static bool ReadsAsWords(string run) =>
        !run.Any(char.IsAsciiDigit) && run.Split('-', '_').All(part => part.Length == 0 || WordPart().IsMatch(part));

    /// <summary>A path's label, <c>&lt;path&gt;</c> with its file's extension, the punctuation that ended its sentence kept after it.</summary>
    private static string PathLabel(Match match, Func<string, string> label)
    {
        var path = match.Value.TrimEnd('.', ',', ';', ':', ')');
        var trailing = match.Value[path.Length..];
        var last = path.TrimEnd('/', '\\');
        var name = last[(last.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 ? name[dot..] : "";
        return label(extension.Length is > 1 and <= 16 && extension[1..].All(char.IsAsciiLetterOrDigit) ? "<path>" + extension : "<path>") + trailing;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])'[^'\r\n]*'(?![\p{L}\p{N}])|""[^""\r\n]*""|“[^”\r\n]*”")]
    private static partial Regex Quoted();

    /// <summary>
    /// A path's words after its first: up to <see cref="MorePlainPathWords"/> plain words (no separator) and then one that
    /// holds a separator (and starts no other path) or is a file name with an extension that ends a word.
    /// </summary>
    private const string MorePathWords = @"(?:(?:[ ]+[^\s'""“”|<>\\/]+){0," + MorePlainPathWords + @"}?[ ]+(?:(?![A-Za-z]:[\\/]|[\\/~])(?=[^\s'""“”|<>]*[\\/])[^\s'""“”|<>]+|[^\s'""“”|<>\\/.]+\.[A-Za-z0-9]{1,16}(?=[.,;:)]?(?:\s|$))))*";

    /// <summary>The most plain words (folder names with spaces) a path's later words may start with.</summary>
    private const string MorePlainPathWords = "6";

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:[A-Za-z]:[\\/]|\\\\|~\\)[^\s'""“”|<>]*" + MorePathWords)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.:/<~\\])~?/[^\s'""“”|<>]+" + MorePathWords)]
    private static partial Regex UnixPath();

    /// <summary>
    /// A value after a marker: <c>Bearer</c> or <c>Basic</c> and a space, or a credential's name (<c>token</c>,
    /// <c>password</c>, <c>secret</c>, <c>apikey</c> and the like) and <c>=</c> or <c>:</c>, all in any case.
    /// </summary>
    [GeneratedRegex(@"(?<marker>(?<![\p{L}\p{N}])(?:(?:Bearer|Basic)[ ]+|(?:access_token|refresh_token|id_token|session_token|token|secret|password|passwd|apikey|api_key|api-key|credential)[ ]*[=:][ ]*))[^\s'""“”,;&]+", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedToken();

    /// <summary>A word as the engine writes them: lower-case of at most 15 letters, CamelCase (acronyms of up to 5 capitals included), or upper-case.</summary>
    [GeneratedRegex(@"\A(?:[A-Z]{0,5}[a-z]{2,15}(?:[A-Z]{1,5}[a-z]{2,15})*[A-Z]{0,5}|[A-Z]{1,15})\z")]
    private static partial Regex WordPart();

    [GeneratedRegex(@"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\p{L}\p{N}-])[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.(?:ts\.net|local|lan)(?![\p{L}\p{N}-])", RegexOptions.IgnoreCase)]
    private static partial Regex LocalHost();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])[0-9A-Fa-f]{2}([:-])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])")]
    private static partial Regex Mac();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z:])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"[A-Za-z0-9_-]{20,}")]
    private static partial Regex TokenLike();
}
