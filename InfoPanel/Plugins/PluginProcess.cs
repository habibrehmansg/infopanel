using System.Diagnostics;
using InfoPanel.Plugins.Host;

namespace InfoPanel.Plugins;

/// <summary>What a plugin host process is started with (the engine executable's host mode, <see cref="PluginHostEntry"/>).</summary>
public sealed record PluginHostCommand(string PipeName, string PluginDll, string LogDirectory, string SettingsDirectory, string LogLevel)
{
    /// <summary>The arguments after the executable: <c>--plugin-host --pipe … --plugin … --log-dir … --settings-dir … --log-level …</c>.</summary>
    public IReadOnlyList<string> Arguments =>
    [
        PluginHostEntry.Flag, "--pipe", PipeName, "--plugin", PluginDll,
        "--log-dir", LogDirectory, "--settings-dir", SettingsDirectory, "--log-level", LogLevel,
    ];
}

/// <summary>Starts plugin host processes (tests give the manager one of their own).</summary>
public interface IPluginProcessLauncher
{
    IPluginProcess Start(PluginHostCommand command);
}

/// <summary>A running plugin host process.</summary>
public interface IPluginProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>The exit code once it has exited.</summary>
    int? ExitCode { get; }

    /// <summary>Completes when the process has exited.</summary>
    Task Exited { get; }

    /// <summary>The end of what it wrote to stderr, at most 4 KiB.</summary>
    string ErrorTail { get; }

    /// <summary>Kills the process and its children; nothing when it has exited.</summary>
    void Kill();
}

/// <summary>
/// Starts plugin hosts as the engine's own executable in its host mode: <c>{BaseDirectory}/InfoPanel</c> (the app host),
/// else <c>dotnet {BaseDirectory}/InfoPanel.dll</c> when the engine itself runs through <c>dotnet</c>. Output and errors are
/// read as they come, in chunks of at most 4 KiB, never as lines (a host writing without a newline would make a line
/// reader hold all of it): stdout is dropped (the host logs to its file), and only stderr's last 4 KiB are kept, for the
/// reason a host failed.
/// </summary>
public sealed class PluginProcessLauncher : IPluginProcessLauncher
{
    public IPluginProcess Start(PluginHostCommand command)
    {
        var (file, prefix) = EngineCommand();
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in prefix.Concat(command.Arguments))
            start.ArgumentList.Add(arg);

        var process = Process.Start(start) ?? throw new InvalidOperationException($"The plugin host {file} didn't start.");
        return new HostProcess(process);
    }

    /// <summary>The engine executable and the arguments that come before the host's own.</summary>
    public static (string File, IReadOnlyList<string> Prefix) EngineCommand()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel");
        if (File.Exists(appHost))
            return (appHost, []);

        var processPath = Environment.ProcessPath ?? "dotnet";
        return (processPath, [Path.Combine(AppContext.BaseDirectory, "InfoPanel.dll")]);
    }

    private sealed class HostProcess : IPluginProcess
    {
        private const int ChunkBytes = 4096;
        private const int TailBytes = 4096;

        /// <summary>How long an exited host's stderr is read on, so its last words are in the reason (wall clock).</summary>
        private static readonly TimeSpan StderrGrace = TimeSpan.FromMilliseconds(500);

        private readonly Process _process;
        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;

        public HostProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            _ = DrainAsync(process.StandardOutput.BaseStream, keep: false);
            var stderr = DrainAsync(process.StandardError.BaseStream, keep: true);
            Exited = ExitAsync(stderr);
        }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? SafeExitCode() : null;

        public Task Exited { get; }

        public string ErrorTail
        {
            get
            {
                lock (_tail)
                    return System.Text.Encoding.UTF8.GetString(_tail, 0, _tailLength);
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It has exited, or can't be killed (gone already).
            }
        }

        public void Dispose() => _process.Dispose();

        private int? SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch (InvalidOperationException) { return null; }
        }

        private async Task ExitAsync(Task stderr)
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            // Its stderr to its end, briefly: a child that kept it open doesn't hold the exit.
            await Task.WhenAny(stderr, Task.Delay(StderrGrace)).ConfigureAwait(false);
        }

        /// <summary>Reads <paramref name="stream"/> to its end in fixed chunks, keeping stderr's last bytes when <paramref name="keep"/>.</summary>
        private Task DrainAsync(Stream stream, bool keep) => Task.Run(async () =>
        {
            var buffer = new byte[ChunkBytes];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    if (keep)
                        Keep(buffer.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process was disposed, or its stream ended badly: nothing more to read.
            }
        });

        /// <summary>Appends <paramref name="chunk"/> to the tail, keeping its last <see cref="TailBytes"/>.</summary>
        private void Keep(ReadOnlySpan<byte> chunk)
        {
            lock (_tail)
            {
                if (chunk.Length >= TailBytes)
                {
                    chunk[^TailBytes..].CopyTo(_tail);
                    _tailLength = TailBytes;
                    return;
                }

                var drop = Math.Max(0, _tailLength + chunk.Length - TailBytes);
                if (drop > 0)
                {
                    _tail.AsSpan(drop, _tailLength - drop).CopyTo(_tail);
                    _tailLength -= drop;
                }
                chunk.CopyTo(_tail.AsSpan(_tailLength));
                _tailLength += chunk.Length;
            }
        }
    }
}
