using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Persistence;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>What a <see cref="BlobSweep"/> did.</summary>
/// <param name="SkippedBecause">Why nothing was deleted, when a document couldn't be read; null when the sweep ran.</param>
public sealed record BlobSweepResult(int Documents, int Blobs, int Referenced, int Deleted, string? SkippedBecause);

/// <summary>
/// Removes blobs no saved file refers to, at start-up only (nothing is deleted while the engine runs, so a blob only
/// the undo history or an unsaved change refers to stays until the next start). The roots are chosen by the root rule of
/// docs/2.0/data-folder.md (<see cref="DocumentStore.SweepRoots"/>), so what a later engine keeps in a profile's folder
/// counts without being understood, the earlier saved versions the store keeps there (<c>versions/{n}.json</c>)
/// included. A reference is any <c>sha256:</c> followed by 64 hex digits anywhere in a root's text, as it is or with
/// its JSON escapes decoded, whether or not the root is JSON; only a profile's <c>document.json</c> must also be
/// well-formed JSON. An unreferenced blob is deleted only when it was last written more than <see cref="GracePeriod"/>
/// ago, and only once <c>profiles/</c> and every profile's folder and <c>versions</c> folder is flushed to disk
/// (<see cref="DocumentStore.FlushBeforeBlobsGo"/>), so a dropped version, a replaced document or a deleted profile that
/// a power loss could bring back never comes back without its blobs; a flush that fails deletes nothing. When any root can't be read
/// (missing, unreadable, not a regular file, larger than
/// <see cref="DocumentStore.MaxBytes"/>, a document that isn't JSON), or what the roots are can't be told (a folder that
/// can't be listed, a link whose target can't be reached), nothing is deleted at all. The sweep is bounded: it looks at
/// most at <see cref="MaxEntries"/> entries (the profiles' files and folders and the blobs) and reads at most
/// <see cref="MaxBytes"/> of roots; past either, it deletes nothing that run.
/// </summary>
public static partial class BlobSweep
{
    private static readonly ILogger Logger = Log.ForContext(typeof(BlobSweep));

    /// <summary>How long an unreferenced blob is kept after it was last stored.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    /// <summary>The most entries a sweep looks at: the files and folders under <c>profiles/</c>, and the files in <c>blobs/</c>.</summary>
    public const int MaxEntries = 250_000;

    /// <summary>The most bytes of roots a sweep reads.</summary>
    public const long MaxBytes = 1L << 30;

    [GeneratedRegex("sha256:[0-9a-f]{64}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    /// <summary>
    /// Sweeps <paramref name="blobs"/> against every document of <paramref name="documents"/>, as part of start-up: after
    /// both stores are opened and before anything is created, saved, deleted, reordered or stored in either. Both stores
    /// are locked for the whole sweep. Blobs last written before <paramref name="now"/> minus <see cref="GracePeriod"/>
    /// and referenced by no document are deleted; a blob that can't be deleted is logged and kept.
    /// </summary>
    /// <exception cref="InvalidOperationException">A store isn't open, or something was already changed in either.</exception>
    public static BlobSweepResult Run(DocumentStore documents, BlobStore blobs, DateTimeOffset now) =>
        Run(documents, blobs, now, MaxEntries, MaxBytes);

    /// <inheritdoc cref="Run(DocumentStore, BlobStore, DateTimeOffset)"/>
    /// <param name="maxEntries">The most entries it looks at (<see cref="MaxEntries"/>).</param>
    /// <param name="maxBytes">The most bytes of roots it reads (<see cref="MaxBytes"/>).</param>
    internal static BlobSweepResult Run(DocumentStore documents, BlobStore blobs, DateTimeOffset now, int maxEntries, long maxBytes)
    {
        lock (documents.SyncRoot)
        lock (blobs.SyncRoot)
        {
            if (!documents.InStartup || !blobs.InStartup)
                throw new InvalidOperationException("The blob sweep runs only at start-up: after both stores are opened and before either changes.");
            return Sweep(documents, blobs, now, maxEntries, maxBytes);
        }
    }

    private static BlobSweepResult Sweep(DocumentStore documents, BlobStore blobs, DateTimeOffset now, int maxEntries, long maxBytes)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var budget = new ListingBudget(maxEntries, "The blob sweep");
        IReadOnlyList<SweepRoot> files;
        IReadOnlyList<string> stored;
        try
        {
            files = documents.SweepRoots(budget);
            stored = blobs.List(budget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Skipped the blob sweep: the documents or blobs can't be listed");
            return new BlobSweepResult(0, 0, 0, 0, $"the documents or blobs can't be listed ({ex.Message})");
        }

        long read = 0;
        foreach (var (file, isDocument) in files)
        {
            try
            {
                var content = DocumentStore.ReadAtMost(file, documents.MaxBytes);
                if ((read += content.Length) > maxBytes)
                {
                    Logger.Warning("Skipped the blob sweep: its roots are more than {MaxBytes} bytes", maxBytes);
                    return new BlobSweepResult(files.Count, stored.Count, 0, 0, $"the roots are more than {maxBytes} bytes, more than the sweep reads");
                }
                if (isDocument)
                    RequireJson(content);
                CollectReferences(content, references);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                Logger.Warning(ex, "Skipped the blob sweep: {Path} can't be read", file);
                return new BlobSweepResult(files.Count, stored.Count, 0, 0, $"{file} can't be read ({ex.Message})");
            }
        }

        // What a power loss could still undo (a dropped version, a replaced document) goes on disk before any blob it may
        // refer to can go; when it can't, nothing is deleted.
        if (stored.Any(id => !references.Contains(id)))
        {
            try
            {
                documents.FlushBeforeBlobsGo();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Skipped the blob sweep: the profiles' folders can't be flushed to disk");
                return new BlobSweepResult(files.Count, stored.Count, 0, 0, $"the profiles' folders can't be flushed to disk ({ex.Message})");
            }
        }

        var cutoff = (now - GracePeriod).UtcDateTime;
        int referenced = 0, deleted = 0;
        foreach (var id in stored)
        {
            if (references.Contains(id))
            {
                referenced++;
                continue;
            }

            try
            {
                if (blobs.DeleteIfOlder(id, cutoff))
                    deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Could not delete unreferenced blob {BlobId}", id);
            }
        }

        Logger.Information("Blob sweep: {Documents} documents, {Blobs} blobs, {Referenced} referenced, {Deleted} deleted",
            files.Count, stored.Count, referenced, deleted);
        return new BlobSweepResult(files.Count, stored.Count, referenced, deleted, null);
    }

    /// <summary>
    /// Adds every reference in <paramref name="content"/>'s text, lower-cased: as it is, and with its JSON escapes
    /// decoded (<c>\u0073ha256:…</c>), wherever they are, so a reference in a JSON string counts however it is spelled and
    /// the content needn't be JSON (a swap file, a recovery file cut short).
    /// </summary>
    internal static void CollectReferences(ReadOnlySpan<byte> content, HashSet<string> references)
    {
        // Latin-1 maps each byte to one char, so every ASCII reference in UTF-8 text is found as it is.
        var text = Encoding.Latin1.GetString(content);
        Collect(text, references);
        if (text.Contains('\\'))
            Collect(Unescaped(text), references);
    }

    /// <summary><paramref name="text"/> with every JSON escape in it decoded, left to right, inside a string or not.</summary>
    private static string Unescaped(string text)
    {
        var decoded = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == 'u' && i + 5 < text.Length && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit))
                {
                    decoded.Append((char)unit);
                    i += 5;
                    continue;
                }

                var simple = next switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => (char?)null };
                if (simple is { } one)
                {
                    decoded.Append(one);
                    i++;
                    continue;
                }
            }

            decoded.Append(c);
        }

        return decoded.ToString();
    }

    /// <summary>Checks that a profile's document is well-formed JSON, at any depth.</summary>
    /// <exception cref="JsonException">It isn't.</exception>
    private static void RequireJson(ReadOnlySpan<byte> document)
    {
        if (document.StartsWith("\uFEFF"u8))
            document = document[3..];
        var reader = new Utf8JsonReader(document, new JsonReaderOptions { MaxDepth = int.MaxValue });
        while (reader.Read())
        {
        }
    }

    private static void Collect(string text, HashSet<string> references)
    {
        foreach (Match match in Reference().Matches(text))
            references.Add(match.Value.ToLowerInvariant());
    }
}
