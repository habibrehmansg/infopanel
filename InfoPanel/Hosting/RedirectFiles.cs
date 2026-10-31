using System.Net;
using System.Security.Cryptography;

namespace InfoPanel.Hosting;

/// <summary>
/// The private redirect files the engine opens the editor through, so the link and its token are in no process's
/// arguments (another user can read those on some systems; Jupyter hides its token the same way): a small HTML page
/// that forwards the browser to the link, written owner-only (0600; on Windows a protected access list granting only this
/// user, <see cref="IWindowsSecurity"/>) in the run folder (owner-only, 0700 or its Windows list, this user's;
/// <see cref="LocalChannel.CreateRunDirectory"/>), named <c>{hash16}.open-{random}.html</c> after the data folder's channel.
/// Only the file's path goes to the browser. Each file is removed once the browser has had time to read it
/// (<see cref="ReadWindow"/>), at the engine's stop (<see cref="Dispose"/>), and a crash's leftovers at the next start
/// (<see cref="RemoveLeftovers"/>). A removal that fails (Windows: the browser still holds the file open) is kept and tried
/// again at the stop, and what is still there then goes with the next start's leftovers.
/// </summary>
public sealed class RedirectFiles(string runDirectory, string prefix, TimeProvider time, ILogger<RedirectFiles> logger, IWindowsSecurity? windows = null)
    : IDisposable
{
    /// <summary>How long a file stays for the browser to read it: Chrome reads it as it opens, a cold start included.</summary>
    public static readonly TimeSpan ReadWindow = TimeSpan.FromMinutes(2);

    private readonly string _run = Path.GetFullPath(runDirectory);
    private readonly IWindowsSecurity? _windows = windows ?? (OperatingSystem.IsWindows() ? new WindowsPrivateFiles() : null);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ITimer> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removing = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// The files written and not yet removed: those waiting for their time, those being removed, and those whose removal
    /// failed.
    /// </summary>
    public IReadOnlyCollection<string> Pending
    {
        get
        {
            lock (_lock)
                return [.. _pending.Keys, .. _removing, .. _failed];
        }
    }

    /// <summary>
    /// Writes a new redirect file to <paramref name="url"/> and returns its full path; it is removed after
    /// <see cref="ReadWindow"/>.
    /// </summary>
    /// <exception cref="LocalChannelException">The run folder isn't this user's alone (a link, another user's).</exception>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file couldn't be written.</exception>
    public string Write(string url)
    {
        LocalChannel.CreateRunDirectory(_run, windows: _windows);
        var path = Path.Combine(_run, $"{prefix}.open-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.html");
        using (var stream = Create(path))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Html(url));
            stream.Write(bytes);
        }

        lock (_lock)
        {
            if (_disposed)
            {
                Delete(path);
                throw new ObjectDisposedException(nameof(RedirectFiles));
            }

            _pending[path] = time.CreateTimer(_ => Remove(path), null, ReadWindow, Timeout.InfiniteTimeSpan);
        }

        return path;
    }

    /// <summary>
    /// Creates the new file <paramref name="path"/> for this user alone: 0600 on Unix; on Windows a protected access list
    /// granting only this user, never the run folder's inherited one.
    /// </summary>
    private FileStream Create(string path)
    {
        if (_windows != null)
            return _windows.CreateFile(path, _windows.CurrentUser);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    /// <summary>
    /// Removes <paramref name="file"/> now (a browser that couldn't be started, or its time is up); when that fails, it is
    /// tried again at the engine's stop. The file stays tracked while it is being removed, so a stop meanwhile tries it too.
    /// </summary>
    public void Remove(string file)
    {
        bool ours;
        lock (_lock)
        {
            ours = _pending.Remove(file, out var timer);
            timer?.Dispose();
            ours |= _failed.Remove(file);
            ours = ours && !_disposed && _removing.Add(file);
        }

        var deleted = Delete(file);
        if (!ours)
            return;
        lock (_lock)
        {
            _removing.Remove(file);
            if (!deleted && !_disposed)
                _failed.Add(file);
        }
    }

    /// <summary>Removes this data folder's redirect files a crash left in the run folder; other folders' are left alone.</summary>
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(_run))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_run, $"{prefix}.open-*.html"))
            {
                lock (_lock)
                {
                    if (_pending.ContainsKey(file) || _removing.Contains(file) || _failed.Contains(file))
                        continue;
                }

                Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not list the run folder for old redirect files ({Error})", ex.GetType().FullName);
        }
    }

    /// <summary>
    /// The page: a refresh to <paramref name="url"/> at once, and a link to it, with no referrer. Everything taken from the
    /// link is HTML-encoded.
    /// </summary>
    public static string Html(string url)
    {
        var target = WebUtility.HtmlEncode(url);
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <meta http-equiv="refresh" content="0;url={target}">
            <title>Opening InfoPanel</title>
            </head>
            <body>
            <p><a href="{target}">Open InfoPanel's editor</a></p>
            </body>
            </html>

            """;
    }

    /// <summary>
    /// Removes every file still waiting (the engine stops), those whose removal is under way included: that removal may
    /// fail after the file could be removed again (Windows: the browser let go of it), and nothing tries it after the stop.
    /// </summary>
    public void Dispose()
    {
        List<string> files;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            files = [.. _pending.Keys, .. _removing, .. _failed];
            foreach (var timer in _pending.Values)
                timer.Dispose();
            _pending.Clear();
            _failed.Clear();
        }

        foreach (var file in files)
            Delete(file);
    }

    /// <summary>Deletes <paramref name="file"/>: true when it is gone.</summary>
    private bool Delete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove a redirect file in the run folder ({Error})", ex.GetType().FullName);
            return false;
        }
    }
}
