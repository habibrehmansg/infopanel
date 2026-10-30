using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// A data folder for the harness: its own temp folder, a 1.x folder beside it that isn't created, its channel in the test
/// run folder, and every engine started on it killed when it is disposed (found through the channel's hello).
/// </summary>
public sealed class HarnessFolder : IDisposable
{
    private readonly List<Process> _engines = [];

    /// <param name="dataName">The data folder's name inside the temp folder (not created).</param>
    public HarnessFolder(string dataName = "data")
    {
        Root = Path.Combine(Path.GetTempPath(), "infopanel-mcp-" + Guid.NewGuid().ToString("N"));
        Data = Path.Combine(Root, dataName);
        Directory.CreateDirectory(Root);
        Endpoint = LocalChannel.EndpointFor(TestRunFolder.Path, Data);
    }

    public string Root { get; }
    public string Data { get; }
    public string V1 => Path.Combine(Root, "v1");
    public LocalChannelEndpoint Endpoint { get; }
    public string LaunchRecordPath => LaunchRecord.PathFor(Endpoint);

    /// <summary>The flags every harness engine has: no browser, no windows, mock sensors, its own 1.x folder.</summary>
    public string[] QuietFlags => ["--no-windows", "--mock-seed", "42", "--v1-dir", V1];

    /// <summary>Writes a launch record as a start by hand with <see cref="QuietFlags"/> would, so a started engine is quiet too.</summary>
    public void RecordQuietStart()
    {
        Directory.CreateDirectory(TestRunFolder.Path);
        File.WriteAllText(LaunchRecordPath, new JsonObject
        {
            ["launch"] = 1,
            ["args"] = new JsonArray([.. QuietFlags.Select(f => (JsonNode)JsonValue.Create(f))]),
        }.ToJsonString());
    }

    /// <summary>Starts the engine by hand on the folder and waits for its channel.</summary>
    public async Task<Process> StartEngineAsync(params string[] extra)
    {
        var start = new ProcessStartInfo(McpHarness.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] args = ["--data-dir", Data, "--port", "0", "--no-browser", .. QuietFlags, .. extra];
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        var engine = Process.Start(start)!;
        _engines.Add(engine);
        engine.OutputDataReceived += (_, _) => { };
        engine.ErrorDataReceived += (_, _) => { };
        engine.BeginOutputReadLine();
        engine.BeginErrorReadLine();
        await McpHarness.WaitUntil(async () => (await PidAsync()) != null, TimeSpan.FromSeconds(60), () => $"The engine didn't open its channel (exited: {engine.HasExited}).");
        return engine;
    }

    /// <summary>The pid of the engine answering on the folder's channel, or null.</summary>
    public async Task<int?> PidAsync()
    {
        try
        {
            await using var client = await LocalChannelClient.TryConnectAsync(Endpoint, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            return client?.Pid;
        }
        catch (LocalChannelException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Engines the front started aren't this test's children: find them through the channel.
        if (PidAsync().GetAwaiter().GetResult() is { } pid)
            McpHarness.Kill(pid);
        foreach (var engine in _engines)
        {
            try
            {
                if (!engine.HasExited)
                    engine.Kill(entireProcessTree: true);
                engine.WaitForExit(10_000);
            }
            catch (InvalidOperationException)
            {
            }
            engine.Dispose();
        }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(LaunchRecordPath); } catch (IOException) { }
    }
}

/// <summary>
/// <c>InfoPanel --mcp</c> as an AI app runs it: a child process with the trimmed environment MCP's reference client
/// passes (no <c>TMPDIR</c>), driven over its stdin and stdout. Every line it writes to stdout is kept, so a test can
/// check that all of them are JSON-RPC messages.
/// </summary>
public sealed class McpHarness : IAsyncDisposable
{
    public static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly ConcurrentQueue<JsonNode> _messages = new();
    private int _nextId;

    /// <param name="environment">Variables added to the trimmed environment (an AI app's own configuration, say).</param>
    public McpHarness(string dataDir, double startSeconds = 60, IReadOnlyDictionary<string, string>? environment = null, params string[] extra)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.Environment.Clear();
        foreach (var name in new[] { "HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment[TestRunFolder.Variable] = TestRunFolder.Path;
        start.Environment["Engine__McpStartSeconds"] = startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        string[] args = ["--mcp", "--data-dir", dataDir, .. extra];
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        _process = Process.Start(start)!;
        _process.StandardInput.AutoFlush = true;
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            _stdout.Enqueue(e.Data);
            try
            {
                if (JsonNode.Parse(e.Data) is { } node)
                    _messages.Enqueue(node);
            }
            catch (JsonException)
            {
            }
            _arrived.Release();
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (_stderr)
                    _stderr.AppendLine(e.Data);
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Pid => _process.Id;

    public string Stderr
    {
        get
        {
            lock (_stderr)
                return _stderr.ToString();
        }
    }

    /// <summary>Every line written to stdout so far.</summary>
    public IReadOnlyList<string> StdoutLines => [.. _stdout];

    private readonly List<JsonNode> _seen = [];

    /// <summary>Sends a request and waits for the response with its id (notifications that come meanwhile are kept).</summary>
    public async Task<JsonNode> RequestAsync(string method, JsonNode? parameters = null, TimeSpan? wait = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null)
            request["params"] = parameters;
        await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), Ct);
        var deadline = Environment.TickCount64 + (long)(wait ?? TimeSpan.FromSeconds(120)).TotalMilliseconds;
        while (true)
        {
            while (_messages.TryDequeue(out var message))
            {
                if (message["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
                    return message;
                lock (_seen)
                    _seen.Add(message);
            }
            var left = deadline - Environment.TickCount64;
            if (left <= 0 || !await _arrived.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(left, 1000)), Ct) && _process.HasExited)
                throw new TimeoutException($"No answer to {method}. stderr:\n{Stderr}");
        }
    }

    /// <summary>Notifications seen while waiting for answers.</summary>
    public IReadOnlyList<JsonNode> Notifications
    {
        get
        {
            lock (_seen)
                return [.. _seen.Where(m => m["method"] != null)];
        }
    }

    public Task<JsonNode> InitializeAsync(string version = "2025-06-18") =>
        RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "harness", ["version"] = "1" },
        });

    /// <summary>A tool call's result (<c>content</c>, <c>isError</c>).</summary>
    public async Task<JsonNode> CallAsync(string tool, JsonObject? arguments = null)
    {
        var answer = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });
        return answer["result"] ?? throw new InvalidOperationException($"{tool} answered with an error: {answer.ToJsonString()}");
    }

    /// <summary>All the text of a result's text items.</summary>
    public static string TextOf(JsonNode result) =>
        string.Join("\n", result["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));

    /// <summary>Ends stdin and waits for the front to exit: its exit code.</summary>
    public async Task<int> CloseAsync()
    {
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return _process.ExitCode;
    }

    /// <summary>Every stdout line is one JSON-RPC 2.0 message.</summary>
    public void AssertStdoutIsOnlyProtocol()
    {
        foreach (var line in StdoutLines)
        {
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
            }
            Assert.True(node is JsonObject o && o["jsonrpc"]?.GetValue<string>() == "2.0", $"Not a protocol message on stdout: {line}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5000))
                    _process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        await Task.CompletedTask;
        _process.Dispose();
    }

    public static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout, Func<string> why)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail(why());
            await Task.Delay(100, Ct);
        }
    }

    public static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The session a process leads or belongs to (Unix).</summary>
    [DllImport("libc", SetLastError = true)]
    public static extern int getsid(int pid);

    /// <summary>The arguments a process was started with, as <c>ps</c> shows them (Unix).</summary>
    public static string ArgumentsOf(int pid)
    {
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-o", "args=", "-p", pid.ToString() })
            start.ArgumentList.Add(arg);
        using var ps = Process.Start(start)!;
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return text;
    }
}
