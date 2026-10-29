using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Documents;

namespace InfoPanel.Persistence;

/// <summary>Why this engine must not touch a data folder: its marker can't be read, or asks for what this engine lacks.</summary>
public sealed class StoreMarkerException(string message) : IOException(message);

/// <summary>
/// The data folder's marker, <c>store.json</c> at its root (docs/2.0/data-folder.md):
/// <c>{"format":"infopanel.store/1","requires":[]}</c>. It answers one question: is it safe for this engine to touch
/// this folder? <see cref="Preflight"/> reads it before anything is created there, the lock file included, and
/// <see cref="Open"/> reads it again under the lock, before anything repairs, sweeps or writes there, and writes it when
/// it is absent. A marker that can't be read, of another major version, or whose
/// <c>requires</c> lists a capability this engine lacks (<see cref="Capabilities"/>) stops start-up, and nothing in the
/// folder is touched. Members it doesn't know are ignored; the file is never rewritten.
/// <para>A later engine that keeps something an earlier one would damage (blob references outside the profiles'
/// folders, say, or a rule a token must meet) lists a capability for it, so the earlier engine stops instead.</para>
/// </summary>
public static partial class StoreMarker
{
    /// <summary>The marker's format string, for the data folder's layout version 1.</summary>
    public const string Format = "infopanel.store/1";

    /// <summary>The most bytes of the marker read; a larger one can't be read.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The store capabilities this engine has: none, since version 1.0 of the layout needs none.</summary>
    public static IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the engine writes when the marker is absent.</summary>
    internal static ReadOnlyMemory<byte> Written => """{"format":"infopanel.store/1","requires":[]}"""u8.ToArray();

    [GeneratedRegex("\\Ainfopanel\\.store/([1-9][0-9]{0,8})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoreFormat();

    /// <summary>
    /// Checks the marker in <paramref name="paths"/>, read only, before anything is created in the folder (the lock file
    /// included): a start-up the marker stops leaves the folder exactly as it was. Absent is fine (<see cref="Open"/>
    /// writes it); <see cref="Open"/> checks it again under the lock.
    /// </summary>
    /// <param name="capabilities">The store capabilities the engine has; <see cref="Capabilities"/> when null.</param>
    /// <exception cref="StoreMarkerException">This engine must not touch the folder; nothing was changed in it.</exception>
    public static void Preflight(DataPaths paths, IReadOnlySet<string>? capabilities = null)
    {
        var path = paths.StoreFile;
        if (Exists(path) && Check(path, capabilities ?? Capabilities) is { } refused)
            throw new StoreMarkerException(refused);
    }

    /// <summary>
    /// Checks the marker in <paramref name="paths"/> before anything else touches the folder. When this engine may touch
    /// it, removes the temp files an interrupted write of the marker left and, when the marker is absent, writes it.
    /// </summary>
    /// <param name="capabilities">The store capabilities the engine has; <see cref="Capabilities"/> when null.</param>
    /// <exception cref="StoreMarkerException">This engine must not touch the folder; nothing was changed in it.</exception>
    /// <exception cref="IOException">The marker was absent and couldn't be written.</exception>
    public static void Open(DataPaths paths, IReadOnlySet<string>? capabilities = null)
    {
        var path = paths.StoreFile;
        var present = Exists(path);
        if (present && Check(path, capabilities ?? Capabilities) is { } refused)
            throw new StoreMarkerException(refused);

        RemoveTempFiles(paths);
        if (!present)
            AtomicFile.Write(path, Written, paths.Root);
    }

    /// <summary>Whether anything is at <paramref name="path"/>: a file, a folder or a link (even one to nothing).</summary>
    private static bool Exists(string path) => RegularFile.StateOf(path) != PathState.Absent;

    /// <summary>Why this engine must not touch the folder whose marker is at <paramref name="path"/>, or null.</summary>
    private static string? Check(string path, IReadOnlySet<string> capabilities)
    {
        string? format = null;
        var requires = new List<string>();
        try
        {
            // A regular file only: a FIFO or device there is refused without waiting on it.
            using var json = JsonDocument.Parse(RegularFile.ReadAtMost(path, MaxBytes), EngineJson.DocumentOptions);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Unreadable(path, "it isn't a JSON object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in json.RootElement.EnumerateObject())
            {
                if (member.Name is not ("format" or "requires"))
                    continue;
                if (!seen.Add(member.Name))
                    return Unreadable(path, $"it has \"{member.Name}\" more than once");
                if (member.Name == "format")
                {
                    if (member.Value.ValueKind != JsonValueKind.String)
                        return Unreadable(path, "its \"format\" isn't a string");
                    format = member.Value.GetString();
                }
                else
                {
                    if (member.Value.ValueKind != JsonValueKind.Array)
                        return Unreadable(path, "its \"requires\" isn't an array");
                    foreach (var capability in member.Value.EnumerateArray())
                    {
                        if (capability.ValueKind != JsonValueKind.String)
                            return Unreadable(path, "its \"requires\" holds something that isn't a capability name");
                        requires.Add(capability.GetString()!);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return Unreadable(path, ex.Message);
        }

        if (format is null)
            return Unreadable(path, "it has no \"format\"");
        var major = StoreFormat().Match(format);
        if (!major.Success)
            return Unreadable(path, $"its format '{format}' isn't a data folder's");
        if (format != Format)
            return $"The data folder's marker {path} says the folder is '{format}', which this engine doesn't read (it reads {Format}): "
                + "a later engine wrote it. Start that engine, or this one with another data folder.";

        var missing = requires.Where(c => !capabilities.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            return $"The data folder's marker {path} says the folder needs {string.Join(", ", missing)}, which this engine doesn't have: "
                + "a later engine wrote it. Start that engine, or this one with another data folder.";
        return null;
    }

    private static string Unreadable(string path, string why) =>
        $"The data folder's marker {path} can't be read ({why}), so this engine can't tell whether it is safe to change the folder. "
        + "Restore the file, or start the engine with another data folder.";

    private static void RemoveTempFiles(DataPaths paths)
    {
        foreach (var entry in RegularFile.List(paths.Root, new ListingBudget(RegularFile.MaxListing, $"Listing {paths.Root}")))
        {
            var file = entry.FullName;
            if (entry is not FileInfo || !AtomicFile.IsTempFile(entry.Name, name => name == DataPaths.StoreFileName))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.ForContext(typeof(StoreMarker)).Warning(ex, "Could not remove {Path}", file);
            }
        }
    }
}
