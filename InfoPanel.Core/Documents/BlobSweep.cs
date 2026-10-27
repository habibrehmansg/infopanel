using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>What a <see cref="BlobSweep"/> did.</summary>
/// <param name="SkippedBecause">Why nothing was deleted, when a document couldn't be read; null when the sweep ran.</param>
public sealed record BlobSweepResult(int Documents, int Blobs, int Referenced, int Deleted, string? SkippedBecause);

/// <summary>
/// Removes blobs no saved document refers to, at start-up only (nothing is deleted while the engine runs, so a blob
/// only the undo history or an unsaved change refers to stays until the next start). A reference is any
/// <c>sha256:</c> followed by 64 hex digits anywhere in a <c>document.json</c>, found by scanning its text (and, for
/// strings with JSON escapes, their decoded value), so documents of an unsupported format and opaque data count without
/// being understood. An unreferenced blob is deleted only when it was last written more than <see cref="GracePeriod"/>
/// ago. When any document can't be read (missing, unreadable, larger than <see cref="DocumentStore.MaxBytes"/>, or not
/// well-formed JSON), or whether one exists can't be told, nothing is deleted at all.
/// </summary>
public static partial class BlobSweep
{
    private static readonly ILogger Logger = Log.ForContext(typeof(BlobSweep));

    /// <summary>How long an unreferenced blob is kept after it was last stored.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    [GeneratedRegex("sha256:[0-9a-f]{64}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    /// <summary>
    /// Sweeps <paramref name="blobs"/> against every document of <paramref name="documents"/>, as part of start-up: after
    /// both stores are opened and before anything is created, saved, deleted, reordered or stored in either. Both stores
    /// are locked for the whole sweep. Blobs last written before <paramref name="now"/> minus <see cref="GracePeriod"/>
    /// and referenced by no document are deleted; a blob that can't be deleted is logged and kept.
    /// </summary>
    /// <exception cref="InvalidOperationException">A store isn't open, or something was already changed in either.</exception>
    public static BlobSweepResult Run(DocumentStore documents, BlobStore blobs, DateTimeOffset now)
    {
        lock (documents.SyncRoot)
        lock (blobs.SyncRoot)
        {
            if (!documents.InStartup || !blobs.InStartup)
                throw new InvalidOperationException("The blob sweep runs only at start-up: after both stores are opened and before either changes.");
            return Sweep(documents, blobs, now);
        }
    }

    private static BlobSweepResult Sweep(DocumentStore documents, BlobStore blobs, DateTimeOffset now)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> files, stored;
        try
        {
            files = documents.DocumentFiles();
            stored = blobs.List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Skipped the blob sweep: the documents or blobs can't be listed");
            return new BlobSweepResult(0, 0, 0, 0, $"the documents or blobs can't be listed ({ex.Message})");
        }

        foreach (var file in files)
        {
            try
            {
                CollectReferences(DocumentStore.ReadAtMost(file, documents.MaxBytes), references);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
            {
                Logger.Warning(ex, "Skipped the blob sweep: {Path} can't be read", file);
                return new BlobSweepResult(files.Count, stored.Count, 0, 0, $"{file} can't be read ({ex.Message})");
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

    /// <summary>Adds every reference in <paramref name="document"/>'s text and in its escaped JSON strings, lower-cased.</summary>
    /// <exception cref="JsonException">The document is not well-formed JSON.</exception>
    internal static void CollectReferences(ReadOnlySpan<byte> document, HashSet<string> references)
    {
        // Latin-1 maps each byte to one char, so every ASCII reference in UTF-8 text is found as it is.
        Collect(Encoding.Latin1.GetString(document), references);

        // A string can spell a reference with escapes (sha256:...); its decoded value is scanned too.
        if (document.StartsWith("﻿"u8))
            document = document[3..];
        var reader = new Utf8JsonReader(document, new JsonReaderOptions { MaxDepth = 4096 });
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.ValueIsEscaped)
                Collect(reader.GetString()!, references);
        }
    }

    private static void Collect(string text, HashSet<string> references)
    {
        foreach (Match match in Reference().Matches(text))
            references.Add(match.Value.ToLowerInvariant());
    }
}
