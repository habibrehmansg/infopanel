using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>How a settings file stands for Connect.</summary>
internal enum SettingsState
{
    /// <summary>Not there yet: Connect creates it with only the status line.</summary>
    Absent,

    /// <summary>There, its status line (or none) not the wrapper's.</summary>
    NotConnected,

    /// <summary>Its status line runs the wrapper, which is there.</summary>
    Connected,

    /// <summary>Its status line runs the wrapper, which is missing.</summary>
    Broken,

    /// <summary>Connected once (its record is there), but its status line has changed since.</summary>
    Changed,

    /// <summary>Connect leaves it alone: <see cref="SettingsFile.Problem"/> says why.</summary>
    Refused,
}

/// <summary>
/// One settings file as Connect sees it: its real path (links resolved: several Claude folders may share it), the
/// folders whose <c>settings.json</c> it is, its state, its status line's command now, and why it is refused.
/// </summary>
internal sealed record SettingsFile(string Real, IReadOnlyList<ClaudeFolder> Folders, SettingsState State, string? Command, string? Problem)
{
    public string Folder => Path.GetDirectoryName(Real)!;

    /// <summary>
    /// The wrapper beside it: <c>infopanel-statusline.sh</c> for a <c>settings.json</c>; else named after the file's whole
    /// real path (<c>infopanel-statusline.{name}.{8 hex of its SHA-256}.sh</c>), so no two files in one folder share one.
    /// </summary>
    public string WrapperPath => Path.Combine(Folder, Named(".sh"));

    /// <summary>The record beside it (<c>infopanel-statusline.json</c>, or named as the wrapper): its status line before Connect, to put back.</summary>
    public string RecordPath => Path.Combine(Folder, Named(".json"));

    private string Named(string extension)
    {
        var name = Path.GetFileName(Real);
        if (name == ClaudeConnect.SettingsName)
            return Wrapper.Marker + extension;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Real)))[..8];
        return $"{Wrapper.Marker}.{name}.{hash}{extension}";
    }
}

/// <summary>
/// Connect Claude Code limits, and Disconnect (docs/2.0/ai-usage.md): puts the wrapper (<see cref="Wrapper"/>) in
/// Claude Code's status line, in each Claude folder's <c>settings.json</c>, and takes it out again. Safe with a file
/// Claude Code itself writes often: the real file (through links, never replacing one) read whole and parsed as a plain
/// JSON object, only <c>statusLine</c> changed, the wrapper and a record of the status line before written first, a
/// backup of the file's bytes beside it, the new bytes renamed over it only if the file still holds what was read (else
/// read again, at most <see cref="Attempts"/> times), and read back. Idempotent: a file that already runs the wrapper
/// isn't written. Not thread-safe: the plugin calls it under one lock.
/// </summary>
internal sealed class ClaudeConnect(UsageEnvironment env, ConnectPlaces places)
{
    public const string SettingsName = "settings.json";

    /// <summary>The largest settings file read.</summary>
    public const int MaxSettingsBytes = 1024 * 1024;

    /// <summary>The largest record read.</summary>
    public const int MaxRecordBytes = 4 * 1024 * 1024;

    /// <summary>How often a settings file that keeps changing under Connect is read again before it gives up.</summary>
    public const int Attempts = 3;

    /// <summary>The backups kept beside a settings file.</summary>
    public const int KeptBackups = 3;

    /// <summary>What a backup's name has between the settings file's name and its time.</summary>
    public const string BackupInfix = ".infopanel-backup-";

    /// <summary>The longest command shown in a confirmation.</summary>
    private const int ShownCommandChars = 300;

    /// <summary>The most projects named in a confirmation.</summary>
    private const int ShownProjects = 8;

    /// <summary>Run with a settings file's real path after its new bytes are written beside it and before it is read again and replaced: the tests' seam.</summary>
    internal Action<string>? BeforeReplace;

    /// <summary>Run with a settings file's real path after it is read again and found unchanged, just before the new bytes replace it: the tests' seam.</summary>
    internal Action<string>? AfterReread;

    private string Show(string path) => ConnectFiles.Show(env, path);

    public const string ReadOffState = "Turn on Read Claude Code first";
    public const string WindowsState = "Not available on Windows yet";
    public const string NoFolderState = "No Claude folder found";

    /// <summary>
    /// Why an action can't run now, or null: Read Claude Code off, or Windows; for Connect, the organisation's settings
    /// setting the status line too (Disconnect, which only undoes, stays possible then).
    /// </summary>
    public string? Blocked(bool readOn, bool connect)
    {
        if (!readOn)
            return ReadOffState;
        if (places.Windows)
            return WindowsState;
        return connect ? Overrides.Managed(env, places.ManagedSettings) : null;
    }

    /// <summary>The settings files of <paramref name="folders"/>, links resolved and grouped, each with its state; by path.</summary>
    public IReadOnlyList<SettingsFile> Find(IReadOnlyList<ClaudeFolder> folders)
    {
        var files = new List<SettingsFile>();
        var groups = new Dictionary<string, List<ClaudeFolder>>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!env.Files.DirectoryExists(folder.Path))
                continue;
            var path = Path.Combine(folder.Path, SettingsName);
            var real = ConnectFiles.Real(path);
            if (real == null)
            {
                if (ConnectFiles.IsLink(path))
                {
                    files.Add(new SettingsFile(path, [folder], SettingsState.Refused, null, "it is a link to a file that isn't there"));
                    continue;
                }
                // Not there: made in the folder (its links resolved) when Connect adds a status line.
                real = ConnectFiles.Real(folder.Path) is { } dir ? Path.Combine(dir, SettingsName) : null;
            }
            else if (Directory.Exists(real))
            {
                files.Add(new SettingsFile(path, [folder], SettingsState.Refused, null, "it is a folder"));
                continue;
            }
            if (real == null)
            {
                files.Add(new SettingsFile(path, [folder], SettingsState.Refused, null, "where it is couldn't be told"));
                continue;
            }
            if (!groups.TryGetValue(real, out var group))
                groups[real] = group = [];
            group.Add(folder);
        }
        files.AddRange(groups.Select(g => Inspect(g.Key, g.Value).File));
        return [.. files.OrderBy(f => f.Real, StringComparer.Ordinal)];
    }

    /// <summary>The settings file at <paramref name="real"/> as it is now, with what was read of it.</summary>
    private Inspection Inspect(string real, IReadOnlyList<ClaudeFolder> folders)
    {
        var shell = new SettingsFile(real, folders, SettingsState.Refused, null, null);
        Inspection Refuse(string problem, byte[]? bytes = null) => new(shell with { Problem = problem }, bytes, null, null);

        byte[]? bytes;
        try
        {
            bytes = ReadWhole(real, MaxSettingsBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(ex is RefusedFileException ? "it isn't a regular file" : ex is UnauthorizedAccessException ? "it may not be read" : "it couldn't be read");
        }
        if (bytes == null && File.Exists(real))
            return Refuse($"it is larger than {MaxSettingsBytes / 1024 / 1024} MiB");
        if (Wrapper.Command(shell.WrapperPath, places.Windows) is not { } wrapperCommand)
            return Refuse("its folder's path can't be put in a status line command");

        JsonElement? root = null;
        JsonElement? statusLine = null;
        if (bytes != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Refuse("it isn't plain JSON (comments, trailing commas or something else); nothing changed", bytes);
            }
            if (root.Value.ValueKind != JsonValueKind.Object)
                return Refuse("it isn't a JSON object; nothing changed", bytes);
            var lines = root.Value.EnumerateObject().Where(p => p.NameEquals("statusLine")).ToList();
            if (lines.Count > 1)
                return Refuse("it sets statusLine twice; nothing changed", bytes);
            if (lines.Count == 1 && lines[0].Value.ValueKind != JsonValueKind.Null)
                statusLine = lines[0].Value;
        }

        string? command = null;
        if (statusLine is { } line)
        {
            if (line.ValueKind != JsonValueKind.Object)
                return Refuse("its statusLine isn't one Connect knows (not an object); nothing changed", bytes);
            if (line.TryGetProperty("type", out var type) && !(type.ValueKind == JsonValueKind.String && type.ValueEquals("command")))
                return Refuse("its status line isn't a command; nothing changed", bytes);
            if (!line.TryGetProperty("command", out var text) || text.ValueKind != JsonValueKind.String)
                return Refuse("its status line has no command; nothing changed", bytes);
            command = text.GetString()!;
        }

        var record = Exists(shell.RecordPath);
        SettingsState state;
        if (command == wrapperCommand)
            state = Exists(shell.WrapperPath) ? SettingsState.Connected : SettingsState.Broken;
        else if (record)
            state = SettingsState.Changed;
        else if (command != null && command.Contains(Wrapper.Marker, StringComparison.Ordinal))
            return Refuse("its status line already runs an InfoPanel wrapper from another place; nothing changed", bytes);
        else
            state = bytes == null ? SettingsState.Absent : SettingsState.NotConnected;
        return new Inspection(shell with { State = state, Command = command }, bytes, root, statusLine) { WrapperCommand = wrapperCommand };
    }

    private bool Exists(string path) => env.Files.Stat(Path.GetDirectoryName(path)!, path) != null;

    private const string RunsAWrapperProblem = "its status line already runs an InfoPanel wrapper; nothing changed";

    /// <summary>
    /// Whether the file's status line, not the wrapper's own command, runs a wrapper anyway (the user's command after
    /// Connect, calling it): wrapping it would have the wrapper run itself.
    /// </summary>
    private static bool RunsAWrapper(SettingsFile file) =>
        file.State is SettingsState.Changed or SettingsState.NotConnected && file.Command != null && file.Command.Contains(Wrapper.Marker, StringComparison.Ordinal);

    /// <summary>A file's bytes, whole, through <see cref="IUsageFiles"/> (a regular file, opened through no link); null when it isn't there or holds more than <paramref name="max"/>.</summary>
    private byte[]? ReadWhole(string path, int max)
    {
        try
        {
            using var file = env.Files.Open(Path.GetDirectoryName(path)!, path);
            return file.Stamp.Length > max ? null : file.ReadAtMost(max);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connects every settings file of <paramref name="folders"/> that isn't: the wrapper and record written, the status
    /// line's command changed (or one added), a backup kept; a connected one whose wrapper is missing gets it again from
    /// its record. Returns what it did; throws, saying what, when a file couldn't be connected (the others are).
    /// </summary>
    public string Connect(IReadOnlyList<ClaudeFolder> folders, bool readOn)
    {
        if (Blocked(readOn, connect: true) is { } blocked)
            throw new InvalidOperationException(blocked);
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var file in Find(folders))
        {
            try
            {
                if (ConnectOne(file) is { } what)
                    done.Add(what);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{Show(file.Real)}: {ex.Message}");
            }
        }
        PluginLog.Info($"AI usage: Connect Claude Code limits: {done.Count} settings files changed, {failed.Count} not");
        if (failed.Count > 0)
            throw new InvalidOperationException((done.Count > 0 ? string.Join("; ", done) + ". " : "") + "Not connected: " + string.Join("; ", failed));
        return done.Count == 0 ? "Already connected" : string.Join("; ", done);
    }

    private string? ConnectOne(SettingsFile found)
    {
        if (found.State == SettingsState.Refused)
            throw new InvalidOperationException(found.Problem!);
        if (found.State == SettingsState.Connected)
            return null;

        var wroteOurs = false;
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var now = Inspect(found.Real, found.Folders);
                var file = now.File;
                switch (file.State)
                {
                    case SettingsState.Refused:
                        throw new InvalidOperationException(file.Problem!);
                    case SettingsState.Connected:
                        return null;
                    case SettingsState.Broken:
                    {
                        // The file is right; the wrapper goes back, from the record of what it wraps.
                        var record = ReadRecord(file) ?? throw new InvalidOperationException("its wrapper and the record of your status line are missing; put your status line back by hand from a backup beside it");
                        CheckOwner(file, record);
                        WriteWrapper(file, record.Command);
                        return $"{Show(file.Real)}: its wrapper was missing and is written again";
                    }
                }
                if (RunsAWrapper(file))
                    throw new InvalidOperationException(RunsAWrapperProblem);
                var changed = Settings(now.Root, w => WriteConnected(w, now.StatusLine, now.WrapperCommand!));
                if (changed.Length > MaxSettingsBytes)
                    throw new InvalidOperationException(TooLargeProblem);

                // Ours first, so the status line never runs a wrapper that isn't there.
                WriteWrapper(file, file.Command);
                WriteRecord(file, now.StatusLine, now.WrapperCommand!);
                wroteOurs = true;

                if (Replace(now, changed))
                {
                    // The file runs them now: they stay, whatever the check finds.
                    wroteOurs = false;
                    Check(file.Real, now.WrapperCommand!);
                    return $"{Show(file.Real)}: connected";
                }
            }
            throw new IOException($"it kept changing while Connect wrote it ({Attempts} times); nothing changed there. Try again");
        }
        finally
        {
            if (wroteOurs && !NamesTheWrapper(found))
            {
                // The file never ran them: they go, so nothing says it was connected.
                ConnectFiles.DeleteQuietly(found.WrapperPath);
                ConnectFiles.DeleteQuietly(found.RecordPath);
            }
        }
    }

    private string TooLargeProblem => $"it would be larger than {MaxSettingsBytes / 1024 / 1024} MiB once changed; nothing changed";

    /// <summary>
    /// Whether the settings file, read again now, names the wrapper beside it (another press may have connected it
    /// meanwhile): a <c>statusLine</c> whose command, as JSON decodes it, holds the wrapper's command or its file name
    /// (unquoted); true when the file can't be read or parsed, so it can't be told. What it runs is never deleted.
    /// </summary>
    private bool NamesTheWrapper(SettingsFile file)
    {
        try
        {
            if (ReadWhole(file.Real, MaxSettingsBytes) is not { } bytes)
                return File.Exists(file.Real) || ConnectFiles.IsLink(file.Real);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var name = Path.GetFileName(file.WrapperPath);
            var wrapperCommand = Wrapper.Command(file.WrapperPath, places.Windows);
            return doc.RootElement.EnumerateObject().Any(p =>
                p.NameEquals("statusLine")
                && p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                && command.GetString() is { } text
                && ((wrapperCommand != null && text.Contains(wrapperCommand, StringComparison.Ordinal)) || text.Contains(name, StringComparison.Ordinal)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Disconnects every settings file of <paramref name="folders"/> that runs the wrapper: the status line from its
    /// record put back (or removed when there was none), a backup kept, then the wrapper and record removed. A file whose
    /// status line changed after Connect isn't touched; its wrapper and record go, unless its status line runs the wrapper.
    /// </summary>
    public string Disconnect(IReadOnlyList<ClaudeFolder> folders, bool readOn)
    {
        if (Blocked(readOn, connect: false) is { } blocked)
            throw new InvalidOperationException(blocked);
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var file in Find(folders))
        {
            try
            {
                if (DisconnectOne(file) is { } what)
                    done.Add(what);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{Show(file.Real)}: {ex.Message}");
            }
        }
        PluginLog.Info($"AI usage: Disconnect Claude Code limits: {done.Count} settings files changed, {failed.Count} not");
        if (failed.Count > 0)
            throw new InvalidOperationException((done.Count > 0 ? string.Join("; ", done) + ". " : "") + "Not disconnected: " + string.Join("; ", failed));
        return done.Count == 0 ? "Not connected" : string.Join("; ", done);
    }

    private string? DisconnectOne(SettingsFile found)
    {
        if (found.State is SettingsState.Absent or SettingsState.NotConnected or SettingsState.Refused)
            return null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = Inspect(found.Real, found.Folders);
            var file = now.File;
            switch (file.State)
            {
                case SettingsState.Absent or SettingsState.NotConnected or SettingsState.Refused:
                    return null;
                case SettingsState.Changed:
                    if (file.Command != null && file.Command.Contains(Path.GetFileName(file.WrapperPath), StringComparison.Ordinal))
                        return $"{Show(file.Real)}: your status line changed after Connect and runs the wrapper, so it stays";
                    ConnectFiles.DeleteQuietly(file.WrapperPath);
                    ConnectFiles.DeleteQuietly(file.RecordPath);
                    return $"{Show(file.Real)}: your status line changed after Connect, so it isn't touched; the wrapper is removed";
            }

            var record = ReadRecord(file) ?? throw new InvalidOperationException("the record of your status line is missing; put it back by hand from a backup beside it");
            CheckOwner(file, record);
            var restored = Settings(now.Root, record.StatusLine is { } original ? w => WriteRestored(w, now.StatusLine!.Value, original) : null);
            if (restored.Length > MaxSettingsBytes)
                throw new InvalidOperationException(TooLargeProblem);
            if (Replace(now, restored))
            {
                var after = Inspect(file.Real, file.Folders).File;
                if (after.Command == now.WrapperCommand)
                    throw new IOException("it still runs the wrapper after it was written");
                ConnectFiles.DeleteQuietly(file.WrapperPath);
                ConnectFiles.DeleteQuietly(file.RecordPath);
                return $"{Show(file.Real)}: your status line is back";
            }
        }
        throw new IOException($"it kept changing while Disconnect wrote it ({Attempts} times); nothing changed there. Try again");
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> beside the settings file, keeps a backup of what was read, and renames the new bytes
    /// over the file only if it still holds what was read; false (nothing changed, the backup and new bytes gone) when it
    /// changed meanwhile. The file the rename displaces is kept aside and compared with what was read: written after that
    /// last read, it is renamed back (false: the change is made again from it); written once more meanwhile, the newer
    /// version stays aside and the error names it.
    /// </summary>
    private bool Replace(Inspection read, byte[] bytes)
    {
        var real = read.File.Real;
        var mode = read.Bytes == null || OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(real);
        var temp = ConnectFiles.WriteTemp(real, bytes, mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        string? backup = null;
        try
        {
            if (read.Bytes != null)
                backup = Backup(real, read.Bytes);
            BeforeReplace?.Invoke(real);
            byte[]? again;
            try
            {
                again = ReadWhole(real, MaxSettingsBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                again = [];
            }
            var same = read.Bytes == null ? again == null && !File.Exists(real) : again != null && again.AsSpan().SequenceEqual(read.Bytes);
            if (!same)
            {
                if (backup != null)
                    ConnectFiles.DeleteQuietly(backup);
                return false;
            }
            AfterReread?.Invoke(real);
            if (read.Bytes == null)
            {
                // Made where there was none: never over a file made meanwhile (the move fails when one is there).
                try
                {
                    File.Move(temp, real, overwrite: false);
                }
                catch (IOException) when (File.Exists(real) || ConnectFiles.IsLink(real))
                {
                    return false;
                }
                temp = null;
                Prune(real);
                return true;
            }
            var displaced = ConnectFiles.Aside(real);
            File.Replace(temp, real, displaced);
            temp = null;
            if (Holds(displaced, read.Bytes))
            {
                ConnectFiles.DeleteQuietly(displaced);
                Prune(real);
                return true;
            }
            // Written after the last read: that version goes back, and the change is made again from it.
            var ours = ConnectFiles.Aside(real);
            File.Replace(displaced, real, ours);
            if (!Holds(ours, bytes))
                throw new IOException($"it was written twice while it was being changed; the newer version is kept beside it as {Show(ours)}");
            ConnectFiles.DeleteQuietly(ours);
            if (backup != null)
                ConnectFiles.DeleteQuietly(backup);
            return false;
        }
        finally
        {
            if (temp != null)
                ConnectFiles.DeleteQuietly(temp);
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> holds exactly <paramref name="bytes"/>; false when it can't be read.</summary>
    private bool Holds(string path, byte[] bytes)
    {
        try
        {
            return ReadWhole(path, MaxSettingsBytes) is { } held && held.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The settings file's bytes as read, kept beside it as <c>{name}.infopanel-backup-{yyyyMMdd-HHmmss}</c> (0600).</summary>
    private string Backup(string real, byte[] bytes)
    {
        var stamp = TimeZoneInfo.ConvertTime(env.Now, env.Zone).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = real + BackupInfix + stamp;
        for (var n = 2; ; n++)
        {
            try
            {
                ConnectFiles.WriteNew(name, bytes, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return name;
            }
            catch (IOException) when (File.Exists(name) || ConnectFiles.IsLink(name))
            {
                if (n > 100)
                    throw;
                name = real + BackupInfix + stamp + "-" + n;
            }
        }
    }

    /// <summary>Keeps the newest <see cref="KeptBackups"/> of the settings file's backups (by name: their times), removing the rest.</summary>
    private void Prune(string real)
    {
        var name = Path.GetFileName(real) + BackupInfix;
        var listing = env.Files.Files(Path.GetDirectoryName(real)!, name + "*", 1000, 10_000);
        var backups = listing.Paths
            .Where(p => IsBackupName(Path.GetFileName(p)[name.Length..]))
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .Skip(KeptBackups);
        foreach (var old in backups)
            ConnectFiles.DeleteQuietly(old);
    }

    /// <summary>Whether <paramref name="tail"/> is a backup's time: <c>yyyyMMdd-HHmmss</c>, perhaps with <c>-{n}</c>.</summary>
    private static bool IsBackupName(string tail)
    {
        if (tail.Length < 15 || tail[8] != '-' || !tail[..8].All(char.IsAsciiDigit) || !tail[9..15].All(char.IsAsciiDigit))
            return false;
        return tail.Length == 15 || (tail[15] == '-' && tail.Length > 16 && tail[16..].All(char.IsAsciiDigit));
    }

    /// <summary>Reads the file back: its status line must run the wrapper now.</summary>
    private void Check(string real, string wrapperCommand)
    {
        var after = Inspect(real, []).File;
        if (after.Command != wrapperCommand)
            throw new IOException(after.Command == null ? "after it was written it has no status line" : $"after it was written its status line is {Shown(after.Command)}");
    }

    private void WriteWrapper(SettingsFile file, string? command)
    {
        var defaultFolder = file.Folders.Any(f => f.Path == Path.Combine(env.Home, ".claude")) || !file.Folders.Any(f => f.Path == Path.Combine(env.Home, ".config", "claude"))
            ? "$HOME/.claude"
            : "$HOME/.config/claude";
        var script = Wrapper.Script(command, defaultFolder) ?? throw new InvalidOperationException("your status line command can't be wrapped (it holds a NUL)");
        ConnectFiles.Write(file.WrapperPath, Encoding.UTF8.GetBytes(script), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void WriteRecord(SettingsFile file, JsonElement? statusLine, string wrapperCommand)
    {
        var bytes = Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("writtenBy", "InfoPanel's AI usage plugin (Connect Claude Code limits): your status line before Connect, which Disconnect puts back");
            w.WriteString("settings", file.Real);
            w.WriteString("command", wrapperCommand);
            w.WritePropertyName("statusLine");
            if (statusLine is { } line)
                line.WriteTo(w);
            else
                w.WriteNullValue();
            w.WriteString("connected", env.Now);
            w.WriteEndObject();
        });
        ConnectFiles.Write(file.RecordPath, bytes, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The record's status line and its command; null when it isn't there or can't be read.</summary>
    private Record? ReadRecord(SettingsFile file)
    {
        try
        {
            if (ReadWhole(file.RecordPath, MaxRecordBytes) is not { } bytes)
                return null;
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("statusLine", out var line))
                return null;
            var settings = root.TryGetProperty("settings", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            if (line.ValueKind == JsonValueKind.Null)
                return new Record(null, null, settings);
            if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                return null;
            return new Record(line.Clone(), command.GetString(), settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A record's status line, its command and the settings file it names (its real path).</summary>
    private sealed record Record(JsonElement? StatusLine, string? Command, string? Settings);

    /// <summary>Whether the record is <paramref name="file"/>'s own: it names that settings file.</summary>
    private static bool Owns(SettingsFile file, Record record) => record.Settings == file.Real;

    private void CheckOwner(SettingsFile file, Record record)
    {
        if (!Owns(file, record))
            throw new InvalidOperationException($"the record beside it names another settings file ({(record.Settings == null ? "none" : Show(record.Settings))}); nothing changed. Put your status line back by hand from a backup beside it");
    }

    /// <summary>The status line Disconnect puts back: the one now with the recorded command (and type), every other member as it is now.</summary>
    private static void WriteRestored(Utf8JsonWriter w, JsonElement now, JsonElement recorded)
    {
        w.WriteStartObject();
        var hasType = recorded.TryGetProperty("type", out var type);
        if (hasType && !now.TryGetProperty("type", out _))
        {
            w.WritePropertyName("type");
            type.WriteTo(w);
        }
        foreach (var member in now.EnumerateObject())
        {
            if (member.NameEquals("command") || (hasType && member.NameEquals("type")))
            {
                w.WritePropertyName(member.Name);
                recorded.GetProperty(member.Name).WriteTo(w);
            }
            else
            {
                member.WriteTo(w);
            }
        }
        w.WriteEndObject();
    }

    /// <summary>The status line that runs the wrapper: the user's with only its command changed, or a new one.</summary>
    private static void WriteConnected(Utf8JsonWriter w, JsonElement? statusLine, string wrapperCommand)
    {
        w.WriteStartObject();
        if (statusLine is { } line)
        {
            foreach (var member in line.EnumerateObject())
            {
                if (member.NameEquals("command"))
                    w.WriteString("command", wrapperCommand);
                else
                    member.WriteTo(w);
            }
        }
        else
        {
            w.WriteString("type", "command");
            w.WriteString("command", wrapperCommand);
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// <paramref name="root"/> (or an empty object) with its <c>statusLine</c> written by <paramref name="statusLine"/> in
    /// its place (last when it had none), or left out when that is null; every other member as it was, in order; with
    /// 2-space indentation and a final newline, as Claude Code writes it.
    /// </summary>
    private static byte[] Settings(JsonElement? root, Action<Utf8JsonWriter>? statusLine) => Json(w =>
    {
        w.WriteStartObject();
        var written = false;
        if (root is { } members)
        {
            foreach (var member in members.EnumerateObject())
            {
                if (!member.NameEquals("statusLine"))
                {
                    member.WriteTo(w);
                    continue;
                }
                if (statusLine != null)
                {
                    w.WritePropertyName("statusLine");
                    statusLine(w);
                }
                written = true;
            }
        }
        if (!written && statusLine != null)
        {
            w.WritePropertyName("statusLine");
            statusLine(w);
        }
        w.WriteEndObject();
    });

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128 }))
            write(w);
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static string Shown(string command) =>
        command.Length <= ShownCommandChars ? command : command[..ShownCommandChars] + "…";

    private string Names(IReadOnlyList<ClaudeFolder> folders) => string.Join(", ", folders.Select(f => f.Name));

    private string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";

    /// <summary>The state line both actions show: connected or not, over how many folders and settings files.</summary>
    private string StateLine(IReadOnlyList<ClaudeFolder> folders, IReadOnlyList<SettingsFile> files)
    {
        var usable = files.Where(f => f.State != SettingsState.Refused).ToList();
        var counts = $"{Count(files.Sum(f => f.Folders.Count), "Claude folder")}, {Count(files.Count, "settings file")}";
        if (usable.Count == 0)
            return files.FirstOrDefault() is { } refused ? $"Can't connect: {Show(refused.Real)}: {refused.Problem}" : NoFolderState;
        if (usable.Any(f => f.State == SettingsState.Broken))
            return "Broken: the wrapper is missing";
        if (usable.All(f => f.State == SettingsState.Connected))
            return $"Connected · {Count(usable.Sum(f => f.Folders.Count), "Claude folder")}";
        if (usable.Any(f => f.State == SettingsState.Changed))
            return "Not connected: your status line changed after Connect";
        if (usable.Any(f => f.State == SettingsState.Connected))
            return $"Partly connected · {usable.Count(f => f.State == SettingsState.Connected)} of {Count(usable.Count, "settings file")}";
        return $"Not connected · {counts}";
    }

    /// <summary>Connect's state and confirmation: what it will change, file by file, and what overrides it.</summary>
    public PluginActionState DescribeConnect(IReadOnlyList<ClaudeFolder> folders, bool readOn)
    {
        if (Blocked(readOn, connect: true) is { } blocked)
            return new PluginActionState { Enabled = false, State = blocked };
        if (folders.Count == 0)
            return new PluginActionState { Enabled = false, State = NoFolderState };
        var files = Find(folders);
        var state = StateLine(folders, files);
        var change = files.Where(f => f.State is SettingsState.Absent or SettingsState.NotConnected or SettingsState.Changed or SettingsState.Broken && !RunsAWrapper(f)).ToList();
        if (change.Count == 0)
            return new PluginActionState { Enabled = false, State = state };

        var text = new StringBuilder();
        var sharedBy = change.Count == 1 && change[0].Folders.Count > 1
            ? $", shared by {change[0].Folders.Count} Claude folders ({Names(change[0].Folders)})"
            : change.Count == 1 ? $" ({Names(change[0].Folders)})" : "";
        text.Append($"Changes {Count(change.Count, "settings file")}{sharedBy}: ");
        text.Append(string.Join(", ", change.Select(f => Show(f.Real)))).Append('.');
        foreach (var file in change)
        {
            text.Append("\n\n");
            if (change.Count > 1)
                text.Append($"{Show(file.Real)} ({Names(file.Folders)}): ");
            var after = $"bash {Show(file.WrapperPath)}";
            switch (file.State)
            {
                case SettingsState.Broken:
                    text.Append("Its status line runs InfoPanel's wrapper, which is missing: the wrapper is written again from its record; the settings file isn't changed.");
                    continue;
                case SettingsState.Absent:
                    text.Append("It isn't there yet: it is made with only the status line.\n");
                    break;
                default:
                    text.Append(file.Command == null ? "No status line now.\n" : $"Your status line now: {Shown(file.Command)}\n");
                    break;
            }
            text.Append(file.Command == null
                ? $"After: {after}, a status line showing the limits (5h 24% · wk 45%), which also copies them to {LimitsReader.FileName} in each Claude folder."
                : $"After: {after}, which runs your command unchanged and copies the limits Claude Code gives it to {LimitsReader.FileName} in each Claude folder.");
        }
        if (change.Any(f => f.State is SettingsState.NotConnected or SettingsState.Changed))
            text.Append($"\n\nA backup is kept beside each file it changes ({SettingsName}{BackupInfix}{{date}}-{{time}}). Disconnect puts your status line back.");
        else
            text.Append("\n\nDisconnect puts things back as they were.");
        foreach (var refused in files.Where(f => f.State == SettingsState.Refused || RunsAWrapper(f)))
            text.Append($"\n\nNot changed: {Show(refused.Real)}: {refused.Problem ?? RunsAWrapperProblem}.");

        var user = files.Select(f => f.Real).ToHashSet(StringComparer.Ordinal);
        var projects = Overrides.Projects(env, folders, user);
        text.Append("\n\n");
        if (projects.Count == 0)
            text.Append("Overridden: none (no project or organisation setting sets a status line).");
        else
            text.Append("In these projects your project's status line is used, so limits are seen only from other projects: ")
                .Append(string.Join(", ", projects.Take(ShownProjects)))
                .Append(projects.Count > ShownProjects ? $" and {projects.Count - ShownProjects} more." : ".");

        return new PluginActionState
        {
            State = state,
            Confirm = new PluginActionConfirmation { Title = "Connect Claude Code limits", Text = text.ToString() },
        };
    }

    /// <summary>Disconnect's state and confirmation: what it puts back, file by file.</summary>
    public PluginActionState DescribeDisconnect(IReadOnlyList<ClaudeFolder> folders, bool readOn)
    {
        if (Blocked(readOn, connect: false) is { } blocked)
            return new PluginActionState { Enabled = false, State = blocked };
        if (folders.Count == 0)
            return new PluginActionState { Enabled = false, State = NoFolderState };
        var files = Find(folders);
        var state = StateLine(folders, files);
        var undo = files.Where(f => f.State is SettingsState.Connected or SettingsState.Broken or SettingsState.Changed).ToList();
        if (undo.Count == 0)
            return new PluginActionState { Enabled = false, State = state };

        var paragraphs = new List<string>();
        foreach (var file in undo)
        {
            var path = Show(file.Real);
            if (file.State == SettingsState.Changed)
            {
                paragraphs.Add(file.Command != null && file.Command.Contains(Path.GetFileName(file.WrapperPath), StringComparison.Ordinal)
                    ? $"{path}: your status line changed after Connect and runs the wrapper itself, so nothing is touched."
                    : $"{path}: your status line changed after Connect, so it isn't touched; only the wrapper is removed.");
                continue;
            }
            var record = ReadRecord(file);
            if (record == null)
                paragraphs.Add($"{path}: the record of your status line is missing, so it can't be put back here; a backup is beside it.");
            else if (!Owns(file, record))
                paragraphs.Add($"{path}: the record beside it names another settings file, so it can't be put back here; a backup is beside it.");
            else if (record.Command == null)
                paragraphs.Add($"Removes the status line Connect added in {path}.");
            else
                paragraphs.Add($"Puts back {Shown(record.Command)} in {path}.");
        }
        paragraphs.Add($"A backup is kept beside each file it changes ({SettingsName}{BackupInfix}{{date}}-{{time}}). The usage files ({LimitsReader.FileName}) stay: their age shows.");
        return new PluginActionState
        {
            State = state,
            Confirm = new PluginActionConfirmation { Title = "Disconnect Claude Code limits", Text = string.Join("\n\n", paragraphs) },
        };
    }

    /// <summary>A settings file as read: its state, its bytes (null: not there), its JSON and its status line.</summary>
    private sealed record Inspection(SettingsFile File, byte[]? Bytes, JsonElement? Root, JsonElement? StatusLine)
    {
        public string? WrapperCommand { get; init; }
    }
}

/// <summary>Connect's own touches of the disk: real paths, and files written whole beside their target and renamed over it.</summary>
internal static class ConnectFiles
{
    /// <summary>
    /// A path as a confirmation shows it: under the home as <c>~/…</c> (the home as given or with its links resolved,
    /// as a real path has them), else whole.
    /// </summary>
    public static string Show(UsageEnvironment env, string path)
    {
        var shown = env.Show(path);
        if (shown.StartsWith('~'))
            return shown;
        if (env.Home.Length == 0 || Real(env.Home) is not { } home)
            return path;
        var relative = Path.GetRelativePath(home, path);
        return relative == "." ? "~" : !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) ? "~/" + relative.Replace('\\', '/') : path;
    }

    /// <summary>The path with every link resolved (a folder's on the way too); null when it isn't there or can't be resolved.</summary>
    public static string? Real(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var full = Path.GetFullPath(path);
                return File.Exists(full) || Directory.Exists(full) ? (new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var buffer = new byte[4096];
        if (realpath(path, buffer) == IntPtr.Zero)
            return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>Whether <paramref name="path"/> is a link, whether or not what it names is there.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A new file at <paramref name="path"/> holding <paramref name="bytes"/> with <paramref name="mode"/>, flushed; fails when something is there.</summary>
    public static void WriteNew(string path, byte[] bytes, UnixFileMode mode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = mode;
        using var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stream.SafeFileHandle, mode);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>A new file beside <paramref name="target"/> (in its folder, with a name of its own) holding <paramref name="bytes"/>; its path.</summary>
    public static string WriteTemp(string target, byte[] bytes, UnixFileMode mode)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.infopanel-{Guid.NewGuid():N}.tmp");
        WriteNew(temp, bytes, mode);
        return temp;
    }

    /// <summary>A new name beside <paramref name="target"/> for the file a replace displaces: <c>{name}.infopanel-displaced-{32 hex}</c>.</summary>
    public static string Aside(string target) => $"{target}.infopanel-displaced-{Guid.NewGuid():N}";

    /// <summary><paramref name="bytes"/> as the file at <paramref name="path"/>: written beside it, then renamed over it (a link there is replaced, never followed).</summary>
    public static void Write(string path, byte[] bytes, UnixFileMode mode)
    {
        var temp = WriteTemp(path, bytes, mode);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);
}
