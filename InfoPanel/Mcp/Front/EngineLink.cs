using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Persistence;

namespace InfoPanel.Mcp.Front;

/// <summary>Where the front finds the engine and how it starts one.</summary>
public sealed class EngineLinkOptions
{
    /// <summary>The data folder's channel, as worked out when the front started.</summary>
    public required LocalChannelEndpoint Endpoint { get; init; }

    /// <summary>
    /// Works the channel out again from the folder's stored spelling, throwing <see cref="IOException"/> when it can't
    /// (<see cref="LocalChannel.StoredEndpointFor"/>). The link does it on first use once the data folder exists, however
    /// it was when <see cref="Endpoint"/> was worked out (Codex p2 #3: the folder may appear in between): on a volume that
    /// folds case it may have been made in another spelling than the one given (by an engine started by hand), and the
    /// channel is named from the spelling stored. Settled only once it worked. Null: <see cref="Endpoint"/> always.
    /// </summary>
    public Func<LocalChannelEndpoint>? Rehash { get; init; }

    /// <summary>The data folder as given (null: the default one), passed to an engine the front starts.</summary>
    public string? DataDir { get; init; }

    /// <summary>The data folder's path, for its <c>runtime.json</c>.</summary>
    public required DataPaths Paths { get; init; }

    /// <summary>The run folder when it was set (<c>Engine:RunDir</c>), passed on so the engine listens where the front looks.</summary>
    public string? RunDir { get; init; }

    /// <summary>How long a start may take before the front gives up (<c>Engine:McpStartSeconds</c>; 30 s).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often it looks for the channel while an engine starts.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The tools and instructions this executable carries, with the text of a live folder over them (null: none): the
    /// launch record's <c>--mcp-text</c>, the folder the engine it starts would read (triage p2 #9).
    /// </summary>
    public required Func<string?, JsonObject> BuiltInList { get; init; }
}

/// <summary>
/// The front's link to the engine on its data folder (<see cref="IMcpBackend"/>). <c>initialize</c> and
/// <c>tools/list</c> only attach to a running engine; the first <c>tools/call</c> attaches or starts one:
/// <list type="number">
/// <item>connect to the channel: attach;</item>
/// <item>else start the engine with the folder's launch record (<see cref="LaunchRecord"/>), <c>--no-browser</c> and
/// <c>--fresh=false</c>, and
/// look for the channel every <see cref="EngineLinkOptions.PollInterval"/> until <see cref="EngineLinkOptions.StartTimeout"/>.
/// The data folder's lock decides: an engine already holding it (one starting, or one too old to have the channel) makes
/// the new one exit 3 having changed nothing. <c>runtime.json</c> never stops a start (a crash leaves it, and its pid
/// may be another process's by now);</item>
/// <item>still no channel: the answer says what was seen. A started engine that exited 3, or a <c>runtime.json</c>
/// whose pid is alive, is an older engine; anything else couldn't be started, with the exit code or the wait.</item>
/// </list>
/// An engine that stops between calls is started again on the next one, and that call's result says so.
/// </summary>
public sealed class EngineLink(EngineLinkOptions options, IEngineLauncher launcher, TextWriter log) : IMcpBackend, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalChannelClient? _client;
    private bool _attachedBefore;
    private LocalChannelEndpoint _endpoint = options.Endpoint;
    private bool _settled = options.Rehash == null;

    /// <summary>
    /// The channel: worked out again from the folder's stored spelling once the data folder exists
    /// (<see cref="EngineLinkOptions.Rehash"/>), and settled only once that worked; a try that fails is made again on
    /// the next use. Under the link's gate.
    /// </summary>
    private LocalChannelEndpoint Endpoint()
    {
        if (_settled || !Directory.Exists(options.Paths.Root))
            return _endpoint;
        try
        {
            var now = options.Rehash!();
            if (now != _endpoint)
                log.WriteLine($"infopanel-mcp: the data folder exists now; its channel is {now.Name}");
            _endpoint = now;
            _settled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"infopanel-mcp: the channel couldn't be worked out again ({ex.Message}); {_endpoint.Name} for now");
        }
        return _endpoint;
    }

    /// <summary>
    /// The list while no engine answers: this executable's tools, with the wording of the live text folder the folder's
    /// launch record names (<c>--mcp-text</c>), so a session that connects before the engine starts gets the same text
    /// as one that connects after. Read on every answer; a record without one gives the built-in text, and so does one
    /// the channel's checks wouldn't trust (<see cref="LaunchRecord.ReadTrusted"/>: the list comes here after a refused
    /// channel too, Codex p2-2 #1).
    /// </summary>
    public JsonObject BuiltInList()
    {
        var args = LaunchRecord.ReadTrusted(_endpoint, message => log.WriteLine($"infopanel-mcp: {message}"));
        var at = args.ToList().IndexOf("--mcp-text");
        return options.BuiltInList(at >= 0 && at + 1 < args.Count ? args[at + 1] : null);
    }

    public async Task<JsonObject?> TryListAsync(CancellationToken ct)
    {
        // A call starting the engine holds the link; the list doesn't wait for it.
        if (!await _gate.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false))
            return null;
        try
        {
            if (_client is not { IsAlive: true })
            {
                await DropAsync().ConfigureAwait(false);
                _client = await TryConnectAsync(ct).ConfigureAwait(false);
            }
            if (_client == null)
                return null;
            var answer = await _client.SendAsync(new JsonObject { ["op"] = "list" }, ct).ConfigureAwait(false);
            answer.Remove("id");
            _attachedBefore = true;
            return answer;
        }
        catch (Exception ex) when (ex is IOException or LocalChannelException)
        {
            await DropAsync().ConfigureAwait(false);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (client, note, failure) = await EnsureAsync(ct).ConfigureAwait(false);
            if (failure != null)
                return Failure(failure);

            JsonObject answer;
            try
            {
                answer = await client!.SendAsync(new JsonObject
                {
                    ["op"] = "call",
                    ["tool"] = tool,
                    ["arguments"] = JsonNode.Parse(arguments.GetRawText(), documentOptions: LocalChannel.MessageOptions),
                }, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await DropAsync().ConfigureAwait(false);
                await log.WriteLineAsync($"infopanel-mcp: the engine went away during {tool}: {ex.Message}").ConfigureAwait(false);
                return Failure("InfoPanel stopped while it was answering. Ask again: it is started again by itself.");
            }

            answer.Remove("id");
            if (answer["error"] is { } error)
                return Failure($"InfoPanel couldn't take the request ({error}). Ask again.");
            if (note != null)
            {
                var content = answer["content"] as JsonArray ?? [];
                content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = note });
                answer["content"] = content;
            }
            return answer;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A live connection, the note for the result when the engine was started, or why there is none.</summary>
    private async Task<(LocalChannelClient? Client, string? Note, string? Failure)> EnsureAsync(CancellationToken ct)
    {
        if (_client is { IsAlive: true })
            return (_client, null, null);

        var lost = _client != null || _attachedBefore;
        await DropAsync().ConfigureAwait(false);
        try
        {
            if (await TryConnectAsync(ct).ConfigureAwait(false) is { } attached)
            {
                _client = attached;
                _attachedBefore = true;
                return (attached, null, null);
            }
        }
        catch (LocalChannelException ex)
        {
            return (null, null, Refused(ex.Message));
        }

        // Nothing answers: start one, and let the data folder's lock decide.
        var args = new List<string>();
        if (options.DataDir != null)
            args.AddRange(["--data-dir", options.DataDir]);
        args.AddRange(LaunchRecord.Read(LaunchRecord.PathFor(Endpoint()), message => log.WriteLine($"infopanel-mcp: {message}")));
        args.Add("--no-browser");
        // Said outright: a start for a read call never wipes the folder, whatever the environment it inherits says.
        args.Add("--fresh=false");
        if (options.RunDir != null)
            args.Add($"--Engine:RunDir={options.RunDir}");

        StartedEngine started;
        try
        {
            started = launcher.Start(args);
        }
        catch (IOException ex)
        {
            await log.WriteLineAsync($"infopanel-mcp: starting the engine failed: {ex.Message}").ConfigureAwait(false);
            return (null, null, CouldNotStart(ex.Message));
        }

        await log.WriteLineAsync($"infopanel-mcp: started the engine (pid {started.Pid?.ToString() ?? "unknown"}) on {options.Paths.Root}").ConfigureAwait(false);
        var inUse = false;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await TryConnectAsync(ct).ConfigureAwait(false) is { } client)
                {
                    _client = client;
                    _attachedBefore = true;
                    var note = lost
                        ? "(InfoPanel had stopped, so it was started again, without opening a browser.)"
                        : "(InfoPanel wasn't running, so it was started, without opening a browser.)";
                    return (client, note, null);
                }
            }
            catch (LocalChannelException ex)
            {
                return (null, null, Refused(ex.Message));
            }

            if (started.ExitCode is { } code)
            {
                if (code == 3)
                {
                    inUse = true; // another engine holds the folder: it may be starting, so its channel may still come
                }
                else
                {
                    await log.WriteLineAsync($"infopanel-mcp: the engine stopped with exit code {code}").ConfigureAwait(false);
                    return (null, null, CouldNotStart($"it stopped with exit code {code}{(code == 2 ? ": a flag it was given is wrong" : "")}"));
                }
            }

            if (deadline.Elapsed >= options.StartTimeout)
                break;
            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }

        if (inUse || RecordedPidIsAlive())
            return (null, null, "The InfoPanel that's running is an older version that can't talk to AI apps. Quit it from the menu bar and open it again.");
        return (null, null, CouldNotStart($"it didn't answer within {options.StartTimeout.TotalSeconds:0} s"));
    }

    private static string CouldNotStart(string reason) =>
        $"InfoPanel isn't running and couldn't be started ({reason}). Open InfoPanel, then ask again.";

    private static string Refused(string reason) =>
        reason.Contains("too many", StringComparison.Ordinal)
            ? "InfoPanel has too many AI apps connected right now. Close one of them, then ask again."
            : reason.Contains("isn't trusted", StringComparison.Ordinal)
                ? $"InfoPanel's channel on this computer isn't this user's alone, so it wasn't used ({reason}) Use a run folder only you can write in."
                : "InfoPanel was updated while it was running. Quit it and open it again.";

    /// <summary>Whether <c>runtime.json</c> names a process that is alive: only to word a failure, never to skip a start.</summary>
    private bool RecordedPidIsAlive()
    {
        try
        {
            using var json = JsonDocument.Parse(RegularFile.ReadAtMost(options.Paths.RuntimeFile, 64 * 1024));
            if (!json.RootElement.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var id) || id <= 0)
                return false;
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private Task<LocalChannelClient?> TryConnectAsync(CancellationToken ct) =>
        LocalChannelClient.TryConnectAsync(Endpoint(), ConnectTimeout, ct);

    private static JsonObject Failure(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true,
    };

    private async Task DropAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
