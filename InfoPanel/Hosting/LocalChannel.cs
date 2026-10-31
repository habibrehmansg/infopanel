using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Api;
using InfoPanel.Persistence;

namespace InfoPanel.Hosting;

/// <summary>
/// Where the engine that holds a data folder listens for local companions (an AI app's front, later the tray's): a Unix
/// socket <c>{run}/{hash16}.sock</c> on macOS and Linux, the named pipe <c>InfoPanel-{hash16}</c> on Windows.
/// <see cref="Hash"/> is the first 16 hex of the SHA-256 of the data folder's real path.
/// </summary>
public sealed record LocalChannelEndpoint(string RunDirectory, string Hash, string? SocketPath, string? PipeName)
{
    /// <summary>The socket's path or the pipe's name, for messages.</summary>
    public string Name => SocketPath ?? $@"\\.\pipe\{PipeName}";
}

/// <summary>A channel that can't be used: a run folder or socket path the engine refuses, or a hello refused.</summary>
public sealed class LocalChannelException(string message) : IOException(message);

/// <summary>
/// The local channel: a current-user-only connection between the engine that holds a data folder and the companions on
/// the same computer, one per data folder. Internal to the engine, not part of <c>spec/</c>.
/// <list type="bullet">
/// <item><b>Where.</b> The run folder is <c>{LocalApplicationData}/InfoPanel/run</c> (the <c>Engine:RunDir</c> setting
/// replaces it), created owner-only (0700) on Unix; its mode is the boundary, as the data folder's is. Not the temp folder:
/// a companion started by another app may be given a trimmed environment without <c>TMPDIR</c>. The socket is 0600; a
/// socket path over <see cref="MaxSocketPathBytes"/> bytes is refused. On Windows the pipe is
/// <see cref="PipeOptions.CurrentUserOnly"/>.</item>
/// <item><b>Who listens.</b> Only the engine holding <c>engine.lock</c>, which removes whatever stale socket a crash left
/// at its path first; a companion never removes anything. A companion trusts the channel only when the run folder is this
/// user's and no one else's to write in, and the process at the other end of the socket is this user's (its user id, as
/// the system says it); the engine never takes over a run folder another user owns.</item>
/// <item><b>Protocol.</b> JSON lines. The companion's hello <c>{"channel":1,"client":"mcp"}</c> (at most
/// <see cref="MaxHelloBytes"/>, within <see cref="HelloTimeout"/>); the engine's <c>{"channel":1,"engineVersion",
/// "engineBuild","pid"}</c>, or <c>{"channel":1,"refused":"…"}</c> for another channel version, a client it doesn't know
/// or too many connections, and the connection is closed. Then requests <c>{"id","op":"list"}</c> → <c>{"id","tools",
/// "instructions"}</c> and <c>{"id","op":"call","tool","arguments"}</c> → <c>{"id","content","isError"}</c>, answered in
/// order on each connection. A second launch of the engine says hello as <c>"launch"</c> and may only ask
/// <c>{"id","op":"open-editor"}</c> → <c>{"id","opened":true}</c> (the engine opens the editor as its menu does); an AI app's
/// front may not ask that, nor a launch for tools. Lines are at most <see cref="MaxLineBytes"/>; a longer one closes that connection, and an
/// answer that would be longer is sent as an error result that says so. At most
/// <see cref="MaxConnections"/> connections at once.</item>
/// </list>
/// </summary>
public static class LocalChannel
{
    public const int Version = 1;

    /// <summary>The longest line either side sends (a preview picture, base64, fits).</summary>
    public const int MaxLineBytes = 32 * 1024 * 1024;

    public const int MaxConnections = 8;
    public const int MaxHelloBytes = 1024;
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The longest socket path, in UTF-8 bytes: macOS's <c>sun_path</c> holds 104 with its terminating zero.</summary>
    public const int MaxSocketPathBytes = 103;

    /// <summary>The companion the front of an AI app is (the only one wired so far).</summary>
    public const string McpClient = "mcp";

    /// <summary>The companion a second launch of the engine is (<see cref="SecondLaunch"/>): it may only ask <see cref="OpenEditorOp"/>.</summary>
    public const string LaunchClient = "launch";

    /// <summary>A launch's one request: open the editor.</summary>
    public const string OpenEditorOp = "open-editor";

    /// <summary><c>{LocalApplicationData}/InfoPanel/run</c>: on macOS <c>~/Library/Application Support/InfoPanel/run</c>.</summary>
    public static string DefaultRunDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "InfoPanel", "run");

    /// <summary>
    /// The first 16 hex of the SHA-256 of the data folder's real path: links resolved; on macOS spelled as the filesystem
    /// stores it (<see cref="AsStored"/>), so on a volume that folds case every spelling of one folder names one channel,
    /// while a volume that keeps case apart keeps two folders apart; case folded on Windows.
    /// </summary>
    public static string HashOf(string dataRoot) => HashOf(dataRoot, whole: false);

    /// <param name="whole">
    /// Refuse a path that isn't there whole, or whose stored spelling can't be read (<see cref="IOException"/>), rather
    /// than hash a spelling that may not be the folder's own.
    /// </param>
    private static string HashOf(string dataRoot, bool whole)
    {
        var real = Path.TrimEndingDirectorySeparator(DataFolder.ResolveRealPath(Path.GetFullPath(dataRoot)));
        if (OperatingSystem.IsMacOS())
            real = AsStored(real, whole);
        else if (whole && !Directory.Exists(real))
            throw new IOException($"{real} isn't there.");
        if (OperatingSystem.IsWindows())
            real = real.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(real)))[..16];
    }

    /// <summary>
    /// macOS: <paramref name="path"/> with the part of it that exists spelled as the filesystem stores it (<c>realpath</c>,
    /// which on macOS returns each name's stored spelling), and the rest, not made yet, as given: once it is made with that
    /// spelling the whole path reads the same. A path whose stored spelling can't be read is kept as it is.
    /// </summary>
    private static string AsStored(string path, bool whole)
    {
        var existing = path;
        while (!Path.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            existing = parent;
        if (whole && existing != path)
            throw new IOException($"{path} isn't there.");
        var buffer = new byte[4096]; // PATH_MAX is 1024
        if (realpath(existing, buffer) == IntPtr.Zero)
            return whole ? throw new IOException($"The stored spelling of {path} can't be read.") : path;
        var stored = Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end && end >= 0 ? end : buffer.Length);
        return Path.TrimEndingDirectorySeparator(stored + path[existing.Length..]);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);

    /// <summary>The channel of the data folder <paramref name="dataRoot"/> in <paramref name="runDirectory"/>.</summary>
    /// <exception cref="LocalChannelException">The socket's path would be over <see cref="MaxSocketPathBytes"/> bytes.</exception>
    public static LocalChannelEndpoint EndpointFor(string runDirectory, string dataRoot) => EndpointFor(runDirectory, dataRoot, whole: false);

    /// <summary>
    /// <see cref="EndpointFor(string, string)"/> of a data folder that exists, hashed from its stored spelling whole
    /// (what a front settles on once the folder exists: <c>EngineLinkOptions.Rehash</c>).
    /// </summary>
    /// <exception cref="IOException">The folder isn't there whole, or its stored spelling can't be read.</exception>
    public static LocalChannelEndpoint StoredEndpointFor(string runDirectory, string dataRoot) => EndpointFor(runDirectory, dataRoot, whole: true);

    private static LocalChannelEndpoint EndpointFor(string runDirectory, string dataRoot, bool whole)
    {
        var run = Path.GetFullPath(runDirectory);
        var hash = HashOf(dataRoot, whole);
        if (OperatingSystem.IsWindows())
            return new LocalChannelEndpoint(run, hash, null, $"InfoPanel-{hash}");

        var socket = Path.Combine(run, hash + ".sock");
        var bytes = Encoding.UTF8.GetByteCount(socket);
        if (bytes > MaxSocketPathBytes)
            throw new LocalChannelException($"The channel's socket path {socket} is {bytes} bytes, over the {MaxSocketPathBytes} a socket allows.");
        return new LocalChannelEndpoint(run, hash, socket, null);
    }

    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates the run folder owner-only when it isn't there, and makes an existing one owner-only. A link, anything but a
    /// folder, or a folder another user owns (<paramref name="user"/> stands in for this one in tests) is refused.
    /// <list type="bullet">
    /// <item>Unix: mode 0700, checked again after it is set.</item>
    /// <item>Windows (or <paramref name="windows"/>, the seam tests replace): created with a protected access list granting
    /// only this user, since a new folder otherwise takes its parent's; an existing folder that lets anyone else in or
    /// inherits its list (<see cref="WindowsPrivateFiles.IsPrivate"/>) gets that list when this user may change it, and is
    /// refused when, read again, it still does.</item>
    /// </list>
    /// </summary>
    /// <exception cref="LocalChannelException">Something other than this user's private folder is there.</exception>
    internal static void CreateRunDirectory(string run, uint? user = null, IWindowsSecurity? windows = null)
    {
        var info = new DirectoryInfo(run);
        var exists = info.Exists || info.LinkTarget != null || File.Exists(run);
        if (exists && (info.LinkTarget != null || !info.Exists))
            throw new LocalChannelException($"The run folder {run} is a link or not a folder.");

        if (windows != null || OperatingSystem.IsWindows())
        {
            CreateWindowsRunDirectory(run, exists, windows ?? new WindowsPrivateFiles());
            return;
        }

        if (!exists)
            Directory.CreateDirectory(run, OwnerOnlyFolder);
        // A folder someone else made (a shared one named by Engine:RunDir) is never taken over.
        if (UnixIdentity.StatOf(run) is not { } stat || (stat.Mode & UnixIdentity.TypeMask) != UnixIdentity.TypeFolder)
            throw new LocalChannelException($"The run folder {run} is a link or not a folder.");
        if (stat.Owner != (user ?? UnixIdentity.CurrentUser))
            throw new LocalChannelException($"The run folder {run} belongs to another user (uid {stat.Owner}).");
        if (File.GetUnixFileMode(run) != OwnerOnlyFolder)
            File.SetUnixFileMode(run, OwnerOnlyFolder);
        if (File.GetUnixFileMode(run) != OwnerOnlyFolder)
            throw new LocalChannelException($"The run folder {run} couldn't be made owner-only.");
    }

    private static void CreateWindowsRunDirectory(string run, bool exists, IWindowsSecurity windows)
    {
        var user = windows.CurrentUser;
        if (!exists)
            windows.CreateFolder(run, user);
        var acl = windows.Read(run);
        if (!WindowsPrivateFiles.IsPrivate(acl, user) && WindowsPrivateFiles.OwnerIsTrusted(acl, user))
        {
            try
            {
                windows.Protect(run, user);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Checked below: the list read again refuses the folder.
            }

            acl = windows.Read(run);
        }

        // A folder someone else owns is never taken over, and one that still lets others in, or still inherits its list,
        // is never used.
        if (!WindowsPrivateFiles.IsPrivate(acl, user))
            throw new LocalChannelException($"The run folder {run} isn't this user's alone (owner {acl.Owner ?? "unknown"}), so it isn't used.");
    }

    /// <summary>
    /// A companion's check before it connects (Unix): the run folder, when there is one, must be a folder (not a link)
    /// owned by this user (<paramref name="user"/> stands in for it in tests) that no one else may write in, or whatever
    /// listens there could be another user's. Nothing there passes: nothing listens.
    /// </summary>
    /// <exception cref="LocalChannelException">The folder isn't one to trust.</exception>
    internal static void CheckRunDirectory(string run, uint? user = null)
    {
        if (OperatingSystem.IsWindows())
            return;
        if (UnixIdentity.StatOf(run) is not { } stat)
            return;
        if ((stat.Mode & UnixIdentity.TypeMask) != UnixIdentity.TypeFolder)
            throw new LocalChannelException($"The run folder {run} is a link or not a folder, so its channel isn't trusted.");
        if (stat.Owner != (user ?? UnixIdentity.CurrentUser))
            throw new LocalChannelException($"The run folder {run} belongs to another user (uid {stat.Owner}), so its channel isn't trusted.");
        if ((stat.Mode & (int)(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new LocalChannelException($"The run folder {run} is one others can write in (mode {Convert.ToString(stat.Mode & 0x1FF, 8)}), so its channel isn't trusted.");
    }

    internal static void MakeOwnerOnly(string file)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, OwnerOnlyFile);
    }

    /// <summary>
    /// How deeply every JSON message on the channel and on the MCP transport is read and written: the formats'
    /// <c>jsonDepth</c> (a document or request body, 256 levels) and the envelope around a tool's arguments (an MCP
    /// request puts them three levels down, a channel call two), with room to spare (Codex p2 #7). Bounded, so a message
    /// can't nest without end; content deeper than the formats allow reaches the engine, which refuses it with
    /// <c>limit.jsonDepth</c>.
    /// </summary>
    public const int MessageDepth = InfoPanel.Spec.SpecJson.SchemaDepth + 16;

    /// <summary>Reading a message at <see cref="MessageDepth"/>.</summary>
    public static JsonDocumentOptions MessageOptions => new() { MaxDepth = MessageDepth };

    /// <summary>Writing a message at <see cref="MessageDepth"/>.</summary>
    internal static readonly JsonSerializerOptions MessageWriter = new() { MaxDepth = MessageDepth };

    internal static byte[] Line(JsonNode node)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node, MessageWriter);
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }
}

/// <summary>
/// Who is at the other end of a Unix socket, and who owns a path: what a companion checks before it trusts a channel
/// (macOS and Linux).
/// </summary>
internal static class UnixIdentity
{
    /// <summary>This process's effective user id.</summary>
    public static uint CurrentUser => geteuid();

    /// <summary>
    /// The user id of the process at the other end of a connected Unix socket, as the system says it; null when it can't
    /// be told. macOS: <c>LOCAL_PEERCRED</c> (<c>SOL_LOCAL</c>), a <c>struct xucred</c>; Linux: <c>SO_PEERCRED</c>
    /// (<c>SOL_SOCKET</c>), a <c>struct ucred</c>. The user id is at offset 4 in both.
    /// </summary>
    public static uint? PeerOf(Socket socket)
    {
        var buffer = new byte[128];
        try
        {
            var length = OperatingSystem.IsMacOS()
                ? socket.GetRawSocketOption(0, 0x001, buffer) // SOL_LOCAL, LOCAL_PEERCRED
                : socket.GetRawSocketOption(1, 17, buffer); // SOL_SOCKET, SO_PEERCRED
            return length >= 8 ? BitConverter.ToUInt32(buffer, 4) : null;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner, permission bits and type of <paramref name="path"/> itself (a link is not followed); null when nothing is
    /// there. macOS: <c>lstat</c> (<c>lstat$INODE64</c> on x64), <c>st_mode</c> (16 bits) at offset 4 and <c>st_uid</c>
    /// at 16. Linux: <c>statx</c> without following links, <c>stx_uid</c> at 20 and <c>stx_mode</c> (16 bits) at 28.
    /// </summary>
    /// <exception cref="IOException">It can't be asked about.</exception>
    public static (uint Owner, int Mode)? StatOf(string path)
    {
        var buffer = new byte[512];
        int result;
        if (OperatingSystem.IsMacOS())
            result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? lstat_inode64(path, buffer) : lstat(path, buffer);
        else
            result = statx(-100, path, 0x100, 0x1 | 0x2 | 0x8, buffer); // AT_FDCWD, AT_SYMLINK_NOFOLLOW; TYPE, MODE, UID
        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == 2) // ENOENT
                return null;
            throw new IOException($"Could not ask about {path} (errno {errno}).");
        }

        return OperatingSystem.IsMacOS()
            ? (BitConverter.ToUInt32(buffer, 16), BitConverter.ToUInt16(buffer, 4))
            : (BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt16(buffer, 28));
    }

    /// <summary>The file type bits of a mode, a folder's and a regular file's (the same on macOS and Linux).</summary>
    public const int TypeMask = 0xF000, TypeFolder = 0x4000, TypeFile = 0x8000;

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
}

/// <summary>A line longer than its reader allows.</summary>
public sealed class LineTooLongException(int max) : IOException($"A line is over {max} bytes.");

/// <summary>Reads <c>\n</c>-ended lines of at most a given size from a stream.</summary>
public sealed class LineReader(Stream stream, int maxBytes)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>The longest line it returns, in bytes, not counting the <c>\n</c> (and a <c>\r</c> before it).</summary>
    public int MaxBytes { get; set; } = maxBytes;

    /// <summary>The next line without its end, or null at the end of the stream; a last line without an end counts.</summary>
    /// <exception cref="LineTooLongException">The line is over <see cref="MaxBytes"/>.</exception>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            var pending = _buffer.AsMemory(_start, _end - _start);
            var end = pending.Span.IndexOf((byte)'\n');
            var take = end >= 0 ? end : pending.Length;
            if (line.Length + take > MaxBytes + 1L)
                throw new LineTooLongException(MaxBytes);
            line.Write(pending.Span[..take]);
            if (end >= 0)
            {
                _start += end + 1;
                return Trimmed(line);
            }

            _start = _end = 0;
            var read = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return line.Length > 0 ? Trimmed(line) : null;
            _end = read;
        }

        byte[] Trimmed(MemoryStream content)
        {
            var bytes = content.ToArray();
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                bytes = bytes[..^1];
            if (bytes.Length > MaxBytes)
                throw new LineTooLongException(MaxBytes);
            return bytes;
        }
    }
}

/// <summary>What a companion asks the engine through the channel: the tools, and a call to one.</summary>
public interface ILocalChannelHandler
{
    /// <summary><c>{"tools":[…],"instructions":"…"}</c>.</summary>
    Task<JsonObject> ListAsync(CancellationToken ct);

    /// <summary><c>{"content":[…],"isError":…}</c>; a failure is a result too.</summary>
    Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct);
}

/// <summary>The channel's limits (tests lower them).</summary>
public sealed class LocalChannelServerOptions
{
    public int MaxLineBytes { get; init; } = LocalChannel.MaxLineBytes;
    public int MaxConnections { get; init; } = LocalChannel.MaxConnections;
    public TimeSpan HelloTimeout { get; init; } = LocalChannel.HelloTimeout;
}

/// <summary>
/// The engine's side of the channel (<see cref="LocalChannel"/>): listens at an endpoint and answers each connection's
/// requests through an <see cref="ILocalChannelHandler"/>. Only the engine holding the data folder's lock starts one.
/// </summary>
public sealed class LocalChannelServer : IAsyncDisposable
{
    private readonly LocalChannelEndpoint _endpoint;
    private readonly ILocalChannelHandler _handler;
    private readonly Func<CancellationToken, Task>? _openEditor;
    private readonly ILogger _logger;
    private readonly LocalChannelServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Stream, Task> _connections = new();
    private readonly Socket? _listener;
    private Task _accepting = Task.CompletedTask;
    private int _disposed;

    private LocalChannelServer(LocalChannelEndpoint endpoint, ILocalChannelHandler handler, Func<CancellationToken, Task>? openEditor, ILogger logger,
        LocalChannelServerOptions options, Socket? listener)
    {
        _endpoint = endpoint;
        _handler = handler;
        _openEditor = openEditor;
        _logger = logger;
        _options = options;
        _listener = listener;
    }

    public LocalChannelEndpoint Endpoint => _endpoint;

    /// <summary>
    /// Listens at <paramref name="endpoint"/>: the run folder created or made owner-only, whatever is at the socket's path
    /// removed (a crash's stale socket: the caller holds the data folder's lock), the socket bound and made 0600.
    /// </summary>
    /// <exception cref="LocalChannelException">The run folder can't be used.</exception>
    /// <exception cref="IOException">The socket can't be bound.</exception>
    /// <exception cref="SocketException">The socket can't be bound.</exception>
    /// <param name="openEditor">What a launch's <see cref="LocalChannel.OpenEditorOp"/> runs; null: it is refused.</param>
    public static LocalChannelServer Start(LocalChannelEndpoint endpoint, ILocalChannelHandler handler, ILogger logger, LocalChannelServerOptions? options = null,
        Func<CancellationToken, Task>? openEditor = null)
    {
        options ??= new LocalChannelServerOptions();
        LocalChannel.CreateRunDirectory(endpoint.RunDirectory);
        Socket? listener = null;
        if (endpoint.SocketPath is { } path)
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null)
                throw new LocalChannelException($"A folder is at the channel's socket path {path}.");
            if (File.Exists(path) || new FileInfo(path).LinkTarget != null)
                File.Delete(path);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                LocalChannel.MakeOwnerOnly(path);
                listener.Listen(16);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        var server = new LocalChannelServer(endpoint, handler, openEditor, logger, options, listener);
        server._accepting = listener != null ? server.AcceptSocketsAsync(listener) : server.AcceptPipesAsync();
        return server;
    }

    private async Task AcceptSocketsAsync(Socket listener)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "The local channel couldn't accept a connection");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            Admit(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task AcceptPipesAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_endpoint.PipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(ex, "The local channel couldn't accept a connection");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            Admit(pipe);
        }
    }

    private void Admit(Stream stream)
    {
        if (_connections.Count >= _options.MaxConnections)
        {
            _ = RefuseAsync(stream, "too many connections");
            return;
        }

        var served = new TaskCompletionSource();
        _connections[stream] = served.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                await ServeAsync(stream).ConfigureAwait(false);
            }
            finally
            {
                _connections.TryRemove(stream, out _);
                await stream.DisposeAsync().ConfigureAwait(false);
                served.TrySetResult();
            }
        });
    }

    private static async Task RefuseAsync(Stream stream, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await stream.WriteAsync(LocalChannel.Line(new JsonObject { ["channel"] = LocalChannel.Version, ["refused"] = reason }), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        var ct = _stop.Token;
        var reader = new LineReader(stream, LocalChannel.MaxHelloBytes);
        try
        {
            byte[]? hello;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_options.HelloTimeout);
                try
                {
                    hello = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return; // no hello in time
                }
            }

            if (hello == null)
                return;
            if (HelloRefusal(hello, out var client) is { } refusal)
            {
                await stream.WriteAsync(LocalChannel.Line(new JsonObject { ["channel"] = LocalChannel.Version, ["refused"] = refusal }), ct).ConfigureAwait(false);
                return;
            }

            await stream.WriteAsync(LocalChannel.Line(new JsonObject
            {
                ["channel"] = LocalChannel.Version,
                ["engineVersion"] = ReadEndpoints.EngineVersion(),
                ["engineBuild"] = ReadEndpoints.EngineBuild(),
                ["pid"] = Environment.ProcessId,
            }), ct).ConfigureAwait(false);

            reader.MaxBytes = _options.MaxLineBytes;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                var answer = LocalChannel.Line(await AnswerAsync(line, client, ct).ConfigureAwait(false));
                // An answer longer than a line may be would close the companion's side, which would take the engine for
                // gone: it gets an error result that says so instead.
                if (answer.Length - 1 > _options.MaxLineBytes)
                {
                    _logger.LogWarning("A local channel answer of {Bytes} bytes is over the {Max} a line may be; an error result was sent instead",
                        answer.Length - 1, _options.MaxLineBytes);
                    answer = LocalChannel.Line(TooLong(line, answer.Length - 1, _options.MaxLineBytes));
                }
                await stream.WriteAsync(answer, ct).ConfigureAwait(false);
            }
        }
        catch (LineTooLongException ex)
        {
            _logger.LogWarning("A local channel connection sent a line over its limit and was closed: {Message}", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The companion went away, or the engine stops.
        }
    }

    /// <summary>The error result for a request whose answer is <paramref name="bytes"/> long, over <paramref name="max"/>.</summary>
    private static JsonObject TooLong(byte[] request, int bytes, int max)
    {
        JsonNode? id = null;
        try
        {
            using var json = JsonDocument.Parse(request, LocalChannel.MessageOptions);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var given))
                id = JsonNode.Parse(given.GetRawText(), documentOptions: LocalChannel.MessageOptions);
        }
        catch (JsonException)
        {
        }

        return new JsonObject
        {
            ["id"] = id,
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"InfoPanel's answer was too long to send ({bytes} bytes; at most {max}). Ask for less: one item, or a smaller picture.",
            }),
            ["isError"] = true,
        };
    }

    /// <summary>Why a hello is refused (another channel version, a client this engine doesn't know), or null and the client.</summary>
    private static string? HelloRefusal(byte[] line, out string client)
    {
        client = "";
        try
        {
            using var json = JsonDocument.Parse(line, LocalChannel.MessageOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("channel", out var channel) || !channel.TryGetInt32(out var version))
                return "not a hello";
            if (version != LocalChannel.Version)
                return $"channel {version} isn't spoken here; this engine speaks channel {LocalChannel.Version}";
            if (!root.TryGetProperty("client", out var name) || name.ValueKind != JsonValueKind.String
                || name.GetString() is not (LocalChannel.McpClient or LocalChannel.LaunchClient))
                return "unknown client";
            client = name.GetString()!;
            return null;
        }
        catch (JsonException)
        {
            return "not a hello";
        }
    }

    private async Task<JsonObject> AnswerAsync(byte[] line, string client, CancellationToken ct)
    {
        JsonNode? id = null;
        string? tool = null;
        try
        {
            using var json = JsonDocument.Parse(line, LocalChannel.MessageOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Error(null, "a request is a JSON object");
            if (root.TryGetProperty("id", out var idElement))
                id = JsonNode.Parse(idElement.GetRawText(), documentOptions: LocalChannel.MessageOptions);
            var op = root.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String ? opElement.GetString() : null;
            // A launch only opens the editor; an AI app's front never does.
            if (client == LocalChannel.LaunchClient || op == LocalChannel.OpenEditorOp)
            {
                if (client != LocalChannel.LaunchClient || op != LocalChannel.OpenEditorOp)
                    return Error(id, $"op '{op}' isn't for a {client} client");
                if (_openEditor == null)
                    return Error(id, "this engine can't open the editor");
                await _openEditor(ct).ConfigureAwait(false);
                return new JsonObject { ["id"] = id, ["opened"] = true };
            }

            JsonObject answer;
            switch (op)
            {
                case "list":
                    answer = await _handler.ListAsync(ct).ConfigureAwait(false);
                    break;
                case "call":
                    if (!root.TryGetProperty("tool", out var toolElement) || toolElement.ValueKind != JsonValueKind.String)
                        return Error(id, "a call names its tool");
                    tool = toolElement.GetString()!;
                    var arguments = root.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null
                        ? args.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    answer = await _handler.CallAsync(tool, arguments, ct).ConfigureAwait(false);
                    break;
                default:
                    return Error(id, $"unknown op '{op}'");
            }

            answer["id"] = id;
            return answer;
        }
        catch (JsonException)
        {
            return Error(id, "a request is a JSON object");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The local channel's {Tool} call failed", tool ?? "list");
            return new JsonObject
            {
                ["id"] = id,
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"InfoPanel hit an error ({ex.GetType().Name}). Its log has the details; ask again, and if it repeats, restart InfoPanel.",
                }),
                ["isError"] = true,
            };
        }

        static JsonObject Error(JsonNode? id, string message) => new() { ["id"] = id?.DeepClone(), ["error"] = message };
    }

    /// <summary>Stops listening, closes every connection and removes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        foreach (var stream in _connections.Keys)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        try
        {
            await Task.WhenAll([_accepting, .. _connections.Values]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The local channel's connections didn't close within 5 s");
        }

        if (_endpoint.SocketPath is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the channel's socket {Path}", path);
            }
        }

        _stop.Dispose();
    }
}

/// <summary>A companion's connection to the engine's channel (<see cref="LocalChannel"/>), after the hello.</summary>
public sealed class LocalChannelClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Socket? _socket;
    private readonly LineReader _reader;
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _nextId;

    private LocalChannelClient(Stream stream, Socket? socket, JsonObject hello)
    {
        _stream = stream;
        _socket = socket;
        _reader = new LineReader(stream, LocalChannel.MaxLineBytes);
        Hello = hello;
    }

    /// <summary>The engine's hello: <c>engineVersion</c>, <c>engineBuild</c>, <c>pid</c>.</summary>
    public JsonObject Hello { get; }

    /// <summary>The engine's process id, from its hello.</summary>
    public int Pid => Hello["pid"]?.GetValue<int>() ?? 0;

    /// <summary>
    /// Connects and says hello. Null when nothing listens there (no socket, or one a crash left); throws
    /// <see cref="LocalChannelException"/> when the engine refuses the hello (another channel version, too many
    /// connections) with its reason, and before any hello when the channel isn't this user's: a run folder that isn't
    /// this user's alone (<see cref="LocalChannel.CheckRunDirectory"/>), or a peer of another user.
    /// <paramref name="peerOf"/> stands in for the system's answer to who the peer is (<see cref="UnixIdentity.PeerOf"/>) in tests;
    /// <paramref name="client"/> is the companion it says it is (an AI app's front unless said).
    /// </summary>
    public static async Task<LocalChannelClient?> TryConnectAsync(LocalChannelEndpoint endpoint, TimeSpan timeout, CancellationToken ct,
        Func<Socket, uint?>? peerOf = null, string client = LocalChannel.McpClient)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        if (endpoint.SocketPath != null)
            LocalChannel.CheckRunDirectory(endpoint.RunDirectory);
        Stream stream;
        Socket? socket = null;
        try
        {
            if (endpoint.SocketPath is { } path)
            {
                if (!File.Exists(path) && new FileInfo(path).LinkTarget == null)
                    return null;
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token).ConfigureAwait(false);
                stream = new NetworkStream(socket, ownsSocket: true);
            }
            else
            {
                var pipe = new NamedPipeClientStream(".", endpoint.PipeName!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds), limit.Token).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                stream = pipe;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket?.Dispose();
            return null;
        }

        try
        {
            // Only this user's own process is trusted: a socket another user bound first (a shared run folder) is refused
            // before anything is sent or read. On Windows the pipe's CurrentUserOnly checks the server's owner.
            if (socket != null && (peerOf ?? UnixIdentity.PeerOf)(socket) is var peer && peer != UnixIdentity.CurrentUser)
                throw new LocalChannelException($"The channel {endpoint.Name} is held by another user's process (uid {peer?.ToString() ?? "unknown"}), so it isn't trusted.");

            await stream.WriteAsync(LocalChannel.Line(new JsonObject { ["channel"] = LocalChannel.Version, ["client"] = client }), limit.Token).ConfigureAwait(false);
            var reader = new LineReader(stream, LocalChannel.MaxHelloBytes);
            var line = await reader.ReadLineAsync(limit.Token).ConfigureAwait(false);
            if (line == null)
                throw new LocalChannelException("The engine closed the channel before it said hello.");
            var hello = JsonNode.Parse(line, documentOptions: LocalChannel.MessageOptions) as JsonObject ?? throw new LocalChannelException("The engine's hello isn't a JSON object.");
            if (hello["refused"]?.GetValue<string>() is { } refused)
                throw new LocalChannelException(refused);
            return new LocalChannelClient(stream, socket, hello);
        }
        catch (Exception ex) when (ex is IOException and not LocalChannelException or SocketException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// False once the engine is known to have closed the connection (a Unix socket that reads as ended); a pipe, or a
    /// socket that can't tell, counts as alive until a request fails.
    /// </summary>
    public bool IsAlive
    {
        get
        {
            if (_socket == null)
                return _stream is PipeStream { IsConnected: true };
            try
            {
                return !(_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>Sends <paramref name="request"/> (an <c>id</c> is added) and returns the engine's answer to it.</summary>
    /// <exception cref="IOException">The connection failed or closed before the answer came.</exception>
    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = ++_nextId;
            request["id"] = id;
            await _stream.WriteAsync(LocalChannel.Line(request), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("The engine closed the channel.");
                if (JsonNode.Parse(line, documentOptions: LocalChannel.MessageOptions) is JsonObject answer && answer["id"] is JsonValue value && value.TryGetValue<long>(out var answered) && answered == id)
                    return answer;
            }
        }
        catch (Exception ex) when (ex is SocketException or JsonException)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            _one.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _one.Dispose();
    }
}

/// <summary>
/// Opens the data folder's channel once the engine is up (it holds the lock by then) and closes it when the engine
/// stops. A channel that can't be opened is logged and the engine runs on without it.
/// </summary>
public sealed class LocalChannelService(
    IHostApplicationLifetime lifetime,
    EngineOptions options,
    DataPaths paths,
    ILocalChannelHandler handler,
    EditorOpener editor,
    ILogger<LocalChannelService> logger) : IHostedService
{
    private CancellationTokenRegistration _started;
    private LocalChannelServer? _server;
    private readonly Lock _lock = new();
    private bool _stopped;

    /// <summary>The channel, once it is open.</summary>
    public LocalChannelServer? Server
    {
        get
        {
            lock (_lock)
                return _server;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = lifetime.ApplicationStarted.Register(Open);
        return Task.CompletedTask;
    }

    private void Open()
    {
        try
        {
            var endpoint = LocalChannel.EndpointFor(options.RunDir ?? LocalChannel.DefaultRunDirectory(), paths.Root);
            LocalChannel.CreateRunDirectory(endpoint.RunDirectory);
            // This start's flags, for an engine an AI app starts later on the same folder (LaunchRecord).
            try
            {
                LaunchRecord.Write(LaunchRecord.PathFor(endpoint), options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not write the launch record for AI apps: {Message}", ex.Message);
            }

            // A second launch asks it to open the editor, off the channel's thread.
            var server = LocalChannelServer.Start(endpoint, handler, logger, openEditor: ct => Task.Run(editor.Open, ct));
            lock (_lock)
            {
                if (!_stopped)
                {
                    _server = server;
                    server = null;
                }
            }

            if (server != null)
            {
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }

            logger.LogInformation("AI apps on this computer can reach this engine through {Channel}", endpoint.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or ArgumentException)
        {
            logger.LogWarning(ex, "The local channel couldn't be opened, so AI apps can't reach this engine: {Message}", ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        LocalChannelServer? server;
        lock (_lock)
        {
            _stopped = true;
            server = _server;
            _server = null;
        }

        if (server != null)
            await server.DisposeAsync().ConfigureAwait(false);
    }
}
