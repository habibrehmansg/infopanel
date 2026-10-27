using System;
using System.IO;

namespace InfoPanel.Import;

/// <summary>
/// The bytes one import request may still read: decompressed archive entries and 1.x folder files, counted as they are
/// read, never from what a header claims. Not thread-safe: one request reads with one budget.
/// </summary>
public sealed class ImportBudget(long bytes)
{
    public long Remaining { get; private set; } = bytes;

    /// <summary>Whether a read has already gone over the budget.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Counts <paramref name="bytes"/> read.</summary>
    /// <exception cref="ImportBudgetExceededException">The request has now read more than its budget.</exception>
    public void Take(long bytes)
    {
        Remaining -= bytes;
        if (Remaining < 0)
        {
            Exhausted = true;
            throw new ImportBudgetExceededException();
        }
    }

    /// <summary>
    /// Reads <paramref name="source"/> to its end, counting every byte against the budget, into memory. Returns null when
    /// it holds more than <paramref name="maxBytes"/> (reading stops one buffer past it).
    /// </summary>
    /// <exception cref="ImportBudgetExceededException">The request read more than its budget.</exception>
    public byte[]? ReadAll(Stream source, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            Take(read);
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>An import request read more bytes than its <see cref="ImportBudget"/> allows.</summary>
public sealed class ImportBudgetExceededException() : Exception("The import read more bytes than its limit allows.");
