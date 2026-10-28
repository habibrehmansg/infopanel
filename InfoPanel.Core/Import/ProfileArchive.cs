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
/// The 2.0 profile archive, <c>.infopanel2</c>: a zip of exactly <c>document.json</c> (a profile's export document) and
/// <c>blobs/{64 lower-case hex}</c>, one per blob the document refers to, named by its SHA-256. No manifest, no
/// directories, nothing else; entries stored or deflated.
/// <list type="bullet">
/// <item><b>Export</b> (<see cref="Export(Workspace, string, DateTimeOffset, ImportLimits?)"/>) writes the document as the JSON export gives it (the current generation;
/// the stored bytes of a profile that can't be opened) and every blob it refers to that is stored and whose bytes hash to
/// its name, in id order, into a temporary file before anything is sent. The no-image id and missing or corrupt blobs
/// are left out.</item>
/// <item><b>Import</b> (<see cref="Open"/>) is strict, and only unpacks: exact names, the document checked as a create
/// checks one, every blob referenced by it and hashed against its name, then stored. A client creates the profile from
/// the answer through the normal create, so nothing is published before everything is checked; an import cut short
/// leaves only blobs, which the start-up sweep reclaims after its grace period.</item>
/// </list>
/// What the document refers to is found by the blob sweep's scanner, so export, import and the sweep agree.
/// </summary>
public static partial class ProfileArchive
{
    public const string FileExtension = ".infopanel2";
    public const string DocumentEntry = "document.json";
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
    /// profile; the problem (409) when its file can't be read at all. An archive its own import would refuse under
    /// <paramref name="limits"/> isn't exported: more entries than <see cref="ImportLimits.Entries"/>
    /// (<c>archive.entries</c>; the document is one, with or without blobs), more bytes read than
    /// <see cref="ImportLimits.ExpandedBytes"/> (the document's own bytes count, checked while it is copied), or a file over
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
            string? name;
            var written = new List<string>();
            using (document)
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                // What its own import counts: the entries, and every byte it reads (the document, then each blob).
                if (limits.Entries < 1)
                    throw TooManyEntries(limits);
                var scanned = WriteDocument(zip, document.Content, workspace.Limits.DocumentBytes, limits.ExpandedBytes, now, out var expanded);
                name = scanned is null ? null : NameOf(scanned);
                var references = scanned is null ? [] : References(scanned);
                foreach (var blob in references.Where(r => r != NoImage).Order(StringComparer.Ordinal))
                {
                    // Hashed here, not taken on the store's remembered verdict: these are the bytes the archive carries.
                    if (workspace.Blobs.ReadHashed(blob) is not { } bytes)
                        continue;
                    if (1 + written.Count + 1 > limits.Entries)
                        throw TooManyEntries(limits);
                    expanded += bytes.Length;
                    if (expanded > limits.ExpandedBytes)
                        throw TooManyBytes(limits.ExpandedBytes);
                    var entry = zip.CreateEntry(BlobsFolder + blob[BlobStore.IdPrefix.Length..], CompressionLevel.Optimal);
                    entry.LastWriteTime = now;
                    using (var stream = entry.Open())
                        stream.Write(bytes);
                    written.Add(blob);
                }
            }
            file.Flush();
            if (file.Length > limits.ArchiveBytes)
                throw new ImportRefusal("archive.expanded", $"The archive is {file.Length} bytes; an import accepts at most {limits.ArchiveBytes}.");
            file.Position = 0;
            return (new ExportedArchive(file, FileNameFor(name ?? "profile", now), written), null);
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

    /// <summary>
    /// Copies the document into its entry, keeping its bytes for the reference scan when they are at most
    /// <paramref name="maxBytes"/> (a larger stored document can't be imported anyway); null otherwise. Refuses
    /// (<c>archive.expanded</c>) as soon as more than <paramref name="maxExpanded"/> bytes are copied, as its import would.
    /// </summary>
    /// <exception cref="ImportRefusal">The document is over <paramref name="maxExpanded"/>.</exception>
    private static byte[]? WriteDocument(ZipArchive zip, Stream content, int maxBytes, long maxExpanded, DateTimeOffset now, out long length)
    {
        length = 0;
        var entry = zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal);
        entry.LastWriteTime = now;
        using var output = entry.Open();
        using var kept = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        var keeping = true;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            length += read;
            if (length > maxExpanded)
                throw TooManyBytes(maxExpanded);
            output.Write(chunk, 0, read);
            if (!keeping)
                continue;
            if (kept.Length + read > maxBytes)
                keeping = false;
            else
                kept.Write(chunk, 0, read);
        }
        return keeping ? kept.ToArray() : null;
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
    /// A guard (<c>archive.*</c>, <see cref="ArchiveReader"/>); a 1.x archive (<c>archive.v1</c>); an entry other than
    /// <c>document.json</c> and <c>blobs/{64 hex}</c> (<c>archive.name</c>); no document (<c>archive.document</c>); a
    /// document over <c>documentBytes</c>, not JSON, or one a create would refuse (its errors under <c>/document</c>); a
    /// blob the document doesn't refer to (<c>archive.unreferenced</c>), over <c>mediaBytes</c>
    /// (<c>limit.mediaBytes</c>), or whose bytes don't hash to its name (<c>archive.hash</c>).
    /// </exception>
    public static ArchiveContents Open(Stream archive, Workspace workspace, ImportLimits? limits = null)
    {
        limits ??= ImportLimits.Default;
        using var reader = ArchiveReader.Open(archive, limits, new ImportBudget(limits.ExpandedBytes));

        if (!reader.Entries.Any(e => e.Name == DocumentEntry) && reader.Entries.Any(e => e.Segments.Count == 1
                && (e.Name.Equals("Profile.xml", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("DisplayItems.xml", StringComparison.OrdinalIgnoreCase))))
            throw new ImportRefusal("archive.v1", "This is a 1.x .infopanel file, not a 2.0 profile archive: import it as a 1.x profile.");

        ArchiveEntry? documentEntry = null;
        var blobEntries = new List<ArchiveEntry>();
        foreach (var entry in reader.Entries)
        {
            if (entry.Name == DocumentEntry)
                documentEntry = entry;
            else if (BlobEntry().IsMatch(entry.Name))
                blobEntries.Add(entry);
            else
                throw ImportRefusal.Entry("archive.name", entry.Name,
                    $"A profile archive holds only {DocumentEntry} and {BlobsFolder}{{64 lower-case hex}} entries.");
        }
        if (documentEntry is null)
            throw new ImportRefusal("archive.document", $"The archive has no {DocumentEntry}.");

        // The document, checked as a create checks one.
        var documentBytes = reader.Read(documentEntry, workspace.Limits.DocumentBytes)
            ?? throw new ImportRefusal("limit.documentBytes", $"The document is larger than {workspace.Limits.DocumentBytes} bytes (documentBytes).", "/document");
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

            // Each blob read, hashed against its name, then stored as POST /api/blobs stores it.
            var blobs = new List<BlobInfo>();
            foreach (var entry in blobEntries)
            {
                var id = BlobStore.IdPrefix + entry.Name[BlobsFolder.Length..];
                var bytes = reader.Read(entry, workspace.Limits.MediaBytes)
                    ?? throw ImportRefusal.Entry("limit.mediaBytes", entry.Name, $"The blob is larger than {workspace.Limits.MediaBytes} bytes (mediaBytes).");
                if (BlobStore.IdOf(bytes) != id)
                    throw ImportRefusal.Entry("archive.hash", entry.Name, "The blob's bytes don't hash to its name.");
                blobs.Add(workspace.PutBlob(bytes).Info);
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
