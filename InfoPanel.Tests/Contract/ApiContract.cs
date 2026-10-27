using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Tests.Contract;

/// <summary>A response an operation declares: its status, its media types (with their bodies) and its required headers.</summary>
internal sealed record DeclaredResponse(int Status, IReadOnlyList<DeclaredMedia> Content, IReadOnlyList<string> RequiredHeaders);

/// <summary>
/// One media type of a response. <see cref="Schema"/> is the reference the body is validated against (a definition of
/// the API schema, or of <see cref="ApiContract.IndexSchemaId"/> for a body the document writes inline), null for a
/// body that isn't JSON or has no schema.
/// </summary>
internal sealed record DeclaredMedia(string Type, string? Schema)
{
    public bool IsJson => Type == "application/json" || Type.EndsWith("+json", StringComparison.Ordinal) && Type.StartsWith("application/", StringComparison.Ordinal);
}

/// <summary>
/// An operation of spec/openapi.json: its method and path template (<c>GET /api/profiles/{id}</c>), its id, the scope
/// its security requirement names (null when it needs no token) and its responses by status.
/// </summary>
internal sealed record Operation(string Method, string Path, string Id, string? Scope, IReadOnlyDictionary<int, DeclaredResponse> Responses)
{
    public string Key => $"{Method} {Path}";

    public override string ToString() => $"{Key} ({Id})";

    private string[] Segments { get; } = Path.Trim('/').Split('/');

    /// <summary>Whether <paramref name="path"/> (a request path, decoded) fills this operation's template: literal segments case-insensitively, a parameter any non-empty segment, a trailing slash ignored.</summary>
    public bool Fits(string path)
    {
        var segments = path.Length > 1 ? path.TrimEnd('/').TrimStart('/').Split('/') : [""];
        if (segments.Length != Segments.Length)
            return false;
        for (var i = 0; i < segments.Length; i++)
        {
            var template = Segments[i];
            if (template.StartsWith('{') ? segments[i].Length == 0 : !string.Equals(template, segments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

/// <summary>
/// The engine API's request index, <c>spec/openapi.json</c>, read from the spec folder the way the format tests read
/// <c>spec/examples</c> (the engine never reads it): its operations, and the schemas their bodies are checked with, which
/// are the engine's own schema files plus one more holding the body schemas the document writes inline (an array of a
/// definition, a <c>oneOf</c> of them), so every body goes through the engine's validator.
/// </summary>
internal sealed class ApiContract
{
    /// <summary>The <c>$id</c> of the schema made from the document's inline body schemas.</summary>
    public const string IndexSchemaId = "urn:infopanel:test:openapi";

    private static readonly Lazy<ApiContract> Loaded = new(() => Load(File.ReadAllText(Path.Combine(FindSpecDir(), "openapi.json"))));

    public static ApiContract Instance => Loaded.Value;

    private ApiContract(JsonObject document, IReadOnlyList<Operation> operations, SchemaSet schemas)
    {
        Document = document;
        Operations = operations;
        Schemas = schemas;
    }

    public JsonObject Document { get; }

    public IReadOnlyList<Operation> Operations { get; }

    /// <summary>The spec's schemas and <see cref="IndexSchemaId"/>.</summary>
    public SchemaSet Schemas { get; }

    /// <summary><c>info.version</c>: the API's protocol.</summary>
    public string Version => (string)Document["info"]!["version"]!;

    /// <summary>The operation with this key (<c>GET /api/session</c>), or null.</summary>
    public Operation? Find(string key) => Operations.FirstOrDefault(o => o.Key == key);

    /// <summary>The operation whose method and path template a request fills, or null.</summary>
    public Operation? Match(string method, string path) =>
        Operations.FirstOrDefault(o => string.Equals(o.Method, method, StringComparison.OrdinalIgnoreCase) && o.Fits(path));

    /// <summary>The socket operation's <c>x-infopanel-socket</c>: the subprotocol and the references of its message definitions.</summary>
    public (string Subprotocol, string ClientMessage, string ServerMessage, string FrameHeader) Socket
    {
        get
        {
            var socket = Document["paths"]!.AsObject().Select(p => p.Value!["get"]?["x-infopanel-socket"]).Single(x => x is not null)!;
            return ((string)socket["subprotocol"]!, (string)socket["clientMessage"]!["$ref"]!, (string)socket["serverMessage"]!["$ref"]!,
                (string)socket["frameHeader"]!["$ref"]!);
        }
    }

    /// <summary>
    /// The <c>type</c> of every message a socket union (<c>clientMessage</c>, <c>serverMessage</c>) allows: its
    /// <c>oneOf</c> branches' <c>type</c> constants.
    /// </summary>
    public static IReadOnlyList<string> MessageTypes(string unionRef)
    {
        var defs = JsonNode.Parse(SpecSchemas.Files["api.schema.json"])!["$defs"]!;
        static string Def(string reference) => reference[(reference.LastIndexOf('/') + 1)..];
        return [.. defs[Def(unionRef)]!["oneOf"]!.AsArray().Select(b => (string)defs[Def((string)b!["$ref"]!)]!["properties"]!["type"]!["const"]!)];
    }

    internal static string FindSpecDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var spec = Path.Combine(dir.FullName, "spec");
            if (File.Exists(Path.Combine(spec, "openapi.json")))
                return spec;
        }

        throw new DirectoryNotFoundException("spec/openapi.json not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Reads a request index (the committed one, or another in a test).</summary>
    public static ApiContract Load(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        var components = document["components"]?.AsObject();
        var inline = new JsonObject();
        var operations = new List<Operation>();

        JsonNode Resolve(JsonNode node, string kind)
        {
            if (node["$ref"] is not { } reference)
                return node;
            var name = ((string)reference!).Split('/')[^1];
            return components?[kind]?[name] ?? throw new InvalidOperationException($"openapi.json: unresolvable {reference}");
        }

        string? SchemaRef(JsonNode? schema)
        {
            if (schema is null)
                return null;
            if (schema is JsonObject o && o.Count == 1 && o["$ref"] is { } r && ((string)r!).StartsWith("urn:", StringComparison.Ordinal))
                return (string)r!;
            var key = "b" + inline.Count;
            inline[key] = schema.DeepClone();
            return $"{IndexSchemaId}#/$defs/{key}";
        }

        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, value) in item!.AsObject())
            {
                if (method is "parameters" or "summary" or "description")
                    continue;
                var op = value!.AsObject();
                var security = op["security"]!.AsArray();
                var scope = security.Count == 0 ? null : (string?)security[0]!.AsObject().Single().Value!.AsArray().FirstOrDefault();
                var responses = new Dictionary<int, DeclaredResponse>();
                foreach (var (status, raw) in op["responses"]!.AsObject())
                {
                    var response = Resolve(raw!, "responses");
                    var content = response["content"]?.AsObject().Select(m => new DeclaredMedia(m.Key, m.Key.Contains("json") ? SchemaRef(m.Value!["schema"]) : null)).ToList() ?? [];
                    var headers = response["headers"]?.AsObject().Where(h => (bool?)h.Value!["required"] == true).Select(h => h.Key).ToList() ?? [];
                    responses[int.Parse(status, System.Globalization.CultureInfo.InvariantCulture)] = new DeclaredResponse(int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), content, headers);
                }

                operations.Add(new Operation(method.ToUpperInvariant(), path, (string)op["operationId"]!, scope, responses));
            }
        }

        var index = new JsonObject { ["$id"] = IndexSchemaId, ["$defs"] = inline };
        var schemas = SchemaSet.Load(SpecSchemas.Files.Values.Append(index.ToJsonString()));
        return new ApiContract(document, operations, schemas);
    }
}
