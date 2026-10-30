using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Documents;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>
/// A profile archive ready to send: a temporary file (deleted when disposed) and the name to offer for it. The caller
/// disposes it.
/// </summary>
public sealed record ExportedArchive(FileStream Content, string FileName, IReadOnlyList<string> Blobs) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// The 2.0 profile archive, <c>.infopanel2</c> (<c>infopanel.archive/1</c>, spec/docs/archive.md): a zip of exactly
/// <c>manifest.json</c> (the format, the capabilities it requires, its one profile), <c>document.json</c> (a profile's
/// export document) and <c>blobs/{64 lower-case hex}</c>, one per blob the document refers to, named by its SHA-256. No
/// directories, nothing else; entries stored or deflated.
/// <list type="bullet">
/// <item><b>Export</b> (<see cref="Export(Workspace, string, DateTimeOffset, ImportLimits?)"/>) writes the manifest, then
/// the document as the JSON export gives it (the current generation; a profile the engine can't open is not exported) and
/// every blob it refers to that is stored and whose bytes hash to its name, in id order, into a temporary file before
/// anything is sent. The no-image id and missing or corrupt blobs are left out.</item>
/// <item><b>Import</b> (<see cref="Open"/>) is strict, and only unpacks: the manifest read first, exact names, the
/// document checked as a create checks one, every blob referenced by it and hashed against its name, then stored. A client creates the profile from
/// the answer through the normal create, so nothing is published before everything is checked; an import cut short
/// leaves only blobs, which the start-up sweep reclaims after its grace period.</item>
/// </list>
/// What the document refers to is found by the blob sweep's scanner, so export, import and the sweep agree.
/// </summary>
public static partial class ProfileArchive
{
    public const string FileExtension = ".infopanel2";
    public const string DocumentEntry = "document.json";
    public const string ManifestEntry = "manifest.json";

    /// <summary>
    /// The manifest an export writes, in its canonical form: <c>infopanel.archive/1</c>, no capability required, one
    /// profile, <c>document.json</c>.
    /// </summary>
    public static ReadOnlySpan<byte> Manifest => """{"format":"infopanel.archive/1","profiles":["document.json"],"requires":[]}"""u8;

    /// <summary>The archive capabilities this engine has: none (version 1.0 defines none).</summary>
    public static IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// What is wrong with a manifest, at paths into it (spec/docs/archive.md), in the order every reader checks: the
    /// JSON-level pass, then its format (another major version is <c>unsupported.format</c>) and each capability it
    /// requires (one this engine lacks is <c>unsupported.capability</c>), then its schema, closed. Empty when it is valid.
    /// </summary>
    public static IReadOnlyList<SpecError> CheckManifest(JsonElement manifest, int jsonDepth = 256)
    {
        if (ValidationText.CheckJson(manifest, jsonDepth) is { } json)
            return json;
        if (ValidationText.CheckFormat(manifest, "archive", Capabilities) is { } format)
            return [format.Error];
        var result = SpecSchemas.Strict.Validate(manifest, SpecSchemas.Archive, jsonDepth);
        return result.IsValid ? [] : ValidationText.Cap([.. ValidationText.FromSchema(result)]);
    }
    public const string BlobsFolder = "blobs/";

    /// <summary>"No image": never a blob, never exported, never missing.</summary>
    public const string NoImage = BlobStore.IdPrefix + "0000000000000000000000000000000000000000000000000000000000000000";

    [GeneratedRegex("^blobs/[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BlobEntry();

    /// <summary>The characters 1.4.x's export name replaced (Windows' invalid file name characters), on every OS.</summary>
    private static readonly char[] InvalidNameCharacters = ['"', '<', '>', '|', ':', '*', '?', '\\', '/', .. Enumerable.Range(0, 32).Select(c => (char)c)];

    /// <summary>
    /// 1.4.x's export file name rule: the profile's name with invalid file name characters replaced by <c>_</c> and spaces
    /// by <c>_</c>, a dash, the Unix seconds, and the extension.
    /// </summary>
    public static string FileNameFor(string profileName, DateTimeOffset now) =>
        string.Concat(profileName.Select(c => InvalidNameCharacters.Contains(c) ? '_' : c)).Replace(" ", "_")
        + "-" + now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>
    /// The archive of profile <paramref name="id"/>, built in a temporary file: null and no problem when there is no such
    /// profile; its status problem (409), and no file, when it isn't <c>ok</c>. An archive its own import would refuse under
    /// <paramref name="limits"/> isn't exported: more entries than <see cref="ImportLimits.Entries"/>
    /// (<c>archive.entries</c>; the manifest and the document are two, with or without blobs), more bytes read than
    /// <see cref="ImportLimits.ExpandedBytes"/> (the manifest's and the document's own bytes count), or a file over
    /// <see cref="ImportLimits.ArchiveBytes"/> (both <c>archive.expanded</c>); the temporary file is gone.
    /// </summary>
    /// <exception cref="ImportRefusal">The archive couldn't be imported back.</exception>
    /// <exception cref="IOException">The archive couldn't be written.</exception>
    public static (ExportedArchive? Archive, ProfileStatusProblem? Problem) Export(Workspace workspace, string id, DateTimeOffset now, ImportLimits? limits = null) =>
        Export(workspace, id, now, limits ?? ImportLimits.Default, TempFile.Create);

    /// <summary><see cref="Export(Workspace, string, DateTimeOffset, ImportLimits?)"/> into the file <paramref name="createFile"/> makes.</summary>
    internal static (ExportedArchive? Archive, ProfileStatusProblem? Problem) Export(Workspace workspace, string id, DateTimeOffset now, ImportLimits limits, Func<FileStream> createFile)
    {
        var (document, problem) = workspace.Export(id);
        if (document is null)
            return (null, problem);

        var file = createFile();
        try
        {
            var written = new List<string>();
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                // What its own import counts: the entries, and every byte it reads (the manifest, the document, then each blob).
                if (limits.Entries < 2)
                    throw TooManyEntries(limits);
                long expanded = Manifest.Length + document.Length;
                if (expanded > limits.ExpandedBytes)
                    throw TooManyBytes(limits.ExpandedBytes);
                WriteEntry(zip, ManifestEntry, Manifest.ToArray(), now);
                WriteEntry(zip, DocumentEntry, document, now);
                foreach (var blob in References(document).Where(r => r != NoImage).Order(StringComparer.Ordinal))
                {
                    // Hashed here, not taken on the store's remembered verdict: these are the bytes the archive carries.
                    if (workspace.Blobs.ReadHashed(blob) is not { } bytes)
                        continue;
                    if (2 + written.Count + 1 > limits.Entries)
                        throw TooManyEntries(limits);
                    expanded += bytes.Length;
                    if (expanded > limits.ExpandedBytes)
                        throw TooManyBytes(limits.ExpandedBytes);
                    WriteEntry(zip, BlobsFolder + blob[BlobStore.IdPrefix.Length..], bytes, now);
                    written.Add(blob);
                }
            }
            file.Flush();
            if (file.Length > limits.ArchiveBytes)
                throw new ImportRefusal("archive.expanded", $"The archive is {file.Length} bytes; an import accepts at most {limits.ArchiveBytes}.");
            file.Position = 0;
            return (new ExportedArchive(file, FileNameFor(NameOf(document) ?? "profile", now), written), null);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static ImportRefusal TooManyEntries(ImportLimits limits) =>
        new("archive.entries", $"The archive would have more than {limits.Entries} entries, which an import refuses.");

    private static ImportRefusal TooManyBytes(long maxExpanded) =>
        new("archive.expanded", $"The archive would hold more than {maxExpanded} bytes, which an import refuses.");

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, DateTimeOffset now)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = now;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Every <c>sha256:</c> id in a document's text, as the blob sweep finds them (malformed JSON: its plain text).</summary>
    internal static HashSet<string> References(byte[] document)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            BlobSweep.CollectReferences(document, references);
        }
        catch (JsonException)
        {
            // The plain-text pass ran before the JSON one: a document that isn't JSON keeps what its text names.
        }
        return references;
    }

    /// <summary>The document's <c>profile.name</c>, when it reads as a string.</summary>
    private static string? NameOf(byte[] document)
    {
        try
        {
            using var json = JsonDocument.Parse(document, SpecJson.DocumentOptions);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a profile archive (a seekable stream: the body in a temporary file): checks it, stores its blobs, and answers
    /// its document, the blobs, and the ids the document refers to that are neither in the archive nor stored here (a
    /// stored file that doesn't hash to its id counts as not stored).
    /// </summary>
    /// <exception cref="ImportRefusal">
    /// A guard (<c>archive.*</c>, <see cref="ArchiveReader"/>); a 1.x archive (<c>archive.v1</c>); no manifest
    /// (<c>archive.manifest</c>), or one over <see cref="ImportLimits.ManifestBytes"/> (<c>limit.manifestBytes</c>) or that
    /// <see cref="CheckManifest"/> refuses (its errors under <c>/manifest</c>: <c>json</c>, <c>unsupported.*</c>,
    /// <c>schema</c>); an entry other than the manifest, <c>document.json</c> and <c>blobs/{64 hex}</c>
    /// (<c>archive.name</c>); no document (<c>archive.document</c>); a
    /// document over <c>documentBytes</c> (in its canonical form; or over <see cref="DocumentStore.ReadCap"/> as stored, not
    /// read), not JSON, or one a create would refuse (its errors under <c>/document</c>); a
    /// blob the document doesn't refer to (<c>archive.unreferenced</c>), over <c>videoBytes</c>
    /// (<c>limit.videoBytes</c>), or whose bytes don't hash to its name (<c>archive.hash</c>).
    /// </exception>
    public static ArchiveContents Open(Stream archive, Workspace workspace, ImportLimits? limits = null)
    {
        limits ??= ImportLimits.Default;
        using var reader = ArchiveReader.Open(archive, limits, new ImportBudget(limits.ExpandedBytes), storedOrDeflatedOnly: true);

        if (!reader.Entries.Any(e => e.Name == DocumentEntry) && reader.Entries.Any(e => e.Segments.Count == 1
                && (e.Name.Equals("Profile.xml", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("DisplayItems.xml", StringComparison.OrdinalIgnoreCase))))
            throw new ImportRefusal("archive.v1", "This is a 1.x .infopanel file, not a 2.0 profile archive: import it as a 1.x profile.");

        // The manifest, read before anything else in the archive: its format and the capabilities it requires decide
        // whether this engine reads the rest at all.
        var manifestEntry = reader.Entries.FirstOrDefault(e => e.Name == ManifestEntry)
            ?? throw new ImportRefusal("archive.manifest", $"The archive has no {ManifestEntry}: it isn't a profile archive.");
        var manifestBytes = reader.Read(manifestEntry, limits.ManifestBytes)
            ?? throw ImportRefusal.Entry("limit.manifestBytes", ManifestEntry, $"The manifest is larger than {limits.ManifestBytes} bytes (manifestBytes).");
        try
        {
            using var manifest = JsonDocument.Parse(manifestBytes, SpecJson.DocumentOptions);
            if (CheckManifest(manifest.RootElement, workspace.Limits.JsonDepth) is { Count: > 0 } manifestErrors)
                throw new ImportRefusal([.. manifestErrors.Select(e => e.Under("/manifest"))]);
        }
        catch (JsonException ex)
        {
            throw new ImportRefusal("json", $"The manifest isn't JSON: {ex.Message}", "/manifest", ex);
        }

        ArchiveEntry? documentEntry = null;
        var blobEntries = new List<ArchiveEntry>();
        foreach (var entry in reader.Entries)
        {
            if (entry == manifestEntry)
                continue;
            if (entry.Name == DocumentEntry)
                documentEntry = entry;
            else if (BlobEntry().IsMatch(entry.Name))
                blobEntries.Add(entry);
            else
                throw ImportRefusal.Entry("archive.name", entry.Name,
                    $"A profile archive holds only {ManifestEntry}, {DocumentEntry} and {BlobsFolder}{{64 lower-case hex}} entries.");
        }
        if (documentEntry is null)
            throw new ImportRefusal("archive.document", $"The archive has no {DocumentEntry}.");

        // The document, checked as a create checks one: read within a create body's raw cap, then admitted by its
        // canonical size (documentBytes, CheckImportDocument).
        var readCap = DocumentStore.ReadCap(workspace.Limits);
        var documentBytes = reader.Read(documentEntry, readCap)
            ?? throw new ImportRefusal("limit.documentBytes", $"The document is larger than {readCap} bytes as stored: documentBytes ({workspace.Limits.DocumentBytes}) plus 64 KiB of room for whitespace and escapes.", "/document");
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(documentBytes, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ImportRefusal("json", $"The document isn't JSON: {ex.Message}", "/document", ex);
        }

        using (json)
        {
            if (workspace.CheckImportDocument(json.RootElement) is { Count: > 0 } errors)
                throw new ImportRefusal(errors);

            // Every blob is one the document refers to, before any is read.
            var references = References(documentBytes);
            foreach (var entry in blobEntries)
            {
                if (!references.Contains(BlobStore.IdPrefix + entry.Name[BlobsFolder.Length..]))
                    throw ImportRefusal.Entry("archive.unreferenced", entry.Name, "The document doesn't refer to this blob.");
            }

            // Each blob streamed to disk as POST /api/blobs streams a body, never held whole, hashed on the way: stored only
            // when its bytes hash to its name.
            var blobs = new List<BlobInfo>();
            foreach (var entry in blobEntries)
            {
                var id = BlobStore.IdPrefix + entry.Name[BlobsFolder.Length..];
                BlobStore.StreamedBlob? stored;
                using (var data = reader.OpenRead(entry))
                    // The import runs synchronously on a worker with no synchronization context: waiting for the write is safe.
                    stored = workspace.Blobs.PutAsync(data, id).GetAwaiter().GetResult();
                if (stored is not { } blob)
                    throw ImportRefusal.Entry("limit.videoBytes", entry.Name, $"The blob is larger than {workspace.Limits.VideoBytes} bytes (videoBytes).");
                if (blob.Id != id)
                    throw ImportRefusal.Entry("archive.hash", entry.Name, "The blob's bytes don't hash to its name.");
                blobs.Add(workspace.Inspected(blob).Info);
            }

            var carried = blobs.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var missing = references
                .Where(r => r != NoImage && !carried.Contains(r) && workspace.Blobs.StoredSize(r) is null)
                .Order(StringComparer.Ordinal)
                .ToList();

            return new ArchiveContents { Document = json.RootElement.Clone(), Blobs = blobs, Missing = missing };
        }
    }
}
