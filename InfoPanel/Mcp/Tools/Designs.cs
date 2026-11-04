using System.Text;
using System.Text.Json.Nodes;
using InfoPanel.Documents;

namespace InfoPanel.Mcp.Tools;

/// <summary>
/// Designs a call from elsewhere brings (docs/2.0/relay.md, "Designs"): a design the service produced (an item's
/// <c>layout</c>, <c>source</c>, <c>scene</c> and <c>origin</c>) and the texts its source needs stored. Every file and
/// dependency is checked before anything is written (<see cref="Preflight"/>), so a refused design leaves nothing stored
/// and nothing changed; then each file is stored as <c>POST /api/blobs</c> stores bytes, under its SHA-256, each behind the
/// call's cutoff (<see cref="Store"/>). The engine never interprets a design: it stores what it is given, checked as any
/// item the API is given.
/// </summary>
public static class Designs
{
    /// <summary>The most files a call brings.</summary>
    public const int MaxFiles = 16;

    /// <summary>The most a file holds, as UTF-8 (1 MiB).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private const string NothingDone = "Nothing was stored or changed.";

    /// <summary>
    /// Checks <paramref name="designs"/> and <paramref name="files"/> before any write: at most <see cref="MaxFiles"/>
    /// files, each at most <see cref="MaxFileBytes"/> as UTF-8; each file one of a design's <c>source.deps</c> (its id its
    /// SHA-256); every dep of every design one of the files or already stored; no design's scene holding a provider's
    /// media source (<see cref="WebImageRule"/>). Answers the files' bytes to store, each once.
    /// </summary>
    /// <exception cref="ToolFailure">The first miss, named: <c>files</c>, <c>design</c>, <c>missingDep</c> or <c>webImage</c>.</exception>
    public static IReadOnlyList<byte[]> Preflight(Workspace workspace, IReadOnlyList<JsonObject> designs, IReadOnlyList<string>? files)
    {
        files ??= [];
        if (files.Count > MaxFiles)
            throw new ToolFailure("files", $"A design comes with at most {MaxFiles} files; this call brought {files.Count} (files). {NothingDone}");

        var deps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var design in designs)
        {
            if (WebImageRule.HoldsProvider(design["scene"]))
                throw new ToolFailure(WebImageRule.Code, $"{WebImageRule.Refusal} {NothingDone}");
            foreach (var dep in DepsOf(design))
                deps.Add(dep);
        }

        var stored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(files[i]);
            if (bytes.Length > MaxFileBytes)
            {
                throw new ToolFailure("files",
                    $"File {i + 1} is {bytes.Length} bytes, over 1 MiB ({MaxFileBytes} bytes) (files). {NothingDone}");
            }

            var id = BlobStore.IdOf(bytes);
            if (!deps.Contains(id))
                throw new ToolFailure("files", $"File {i + 1} ({id}) isn't one of the design's deps (files). {NothingDone}");
            stored.TryAdd(id, bytes);
        }

        foreach (var dep in deps)
        {
            // Stored only as a verified read finds it: a file under the id whose bytes don't hash to it isn't.
            if (!stored.ContainsKey(dep) && workspace.Blobs.StoredSize(dep) is null)
            {
                throw new ToolFailure("missingDep",
                    $"The design needs {dep}, which isn't stored and isn't among its files (missingDep): add_asset gives an SVG's id. {NothingDone}");
            }
        }

        return [.. stored.Values];
    }

    /// <summary>
    /// Stores each of <paramref name="files"/> (as <see cref="Preflight"/> answered them), each just behind the call's
    /// cutoff: the step hook's <c>write</c> and <c>locking</c>, then the cutoff under the blob store's lock.
    /// </summary>
    /// <exception cref="ChangeCutOffException">The cutoff tripped before a file: it and the rest aren't stored.</exception>
    public static void Store(ProfileChanges changes, McpCall call, IReadOnlyList<byte[]> files)
    {
        foreach (var bytes in files)
        {
            changes.BeforeWrite();
            changes.Workspace.PutBlob(bytes, call.BeforeWrite);
        }
    }

    /// <summary>A design's <c>source.deps</c>: stored blob ids (<c>sha256:</c>), none when absent.</summary>
    /// <exception cref="ToolFailure"><c>design</c>: deps that aren't a list of blob ids.</exception>
    private static IEnumerable<string> DepsOf(JsonObject design)
    {
        var deps = design["source"]?["deps"];
        if (deps is null)
            return [];
        if (deps is not JsonArray list)
            throw new ToolFailure("design", $"The design's source.deps must be a list of stored blob ids (sha256:…) (design). {NothingDone}");
        var ids = new List<string>();
        foreach (var dep in list)
        {
            var id = dep is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (!BlobStore.IsValidId(id))
            {
                throw new ToolFailure("design",
                    $"The design's source.deps lists {(id ?? dep?.ToJsonString() ?? "null")}, which isn't a stored blob's id (sha256:…) (design). {NothingDone}");
            }

            ids.Add(id!);
        }

        return ids;
    }
}
