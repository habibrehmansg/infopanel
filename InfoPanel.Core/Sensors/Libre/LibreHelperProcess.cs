using InfoPanel.SensorHelper;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// Starts the sensor helper as a child process of the engine: <c>InfoPanel.SensorHelper --pipe &lt;name&gt; --parent
/// &lt;the engine's process id&gt; --parent-start &lt;its start time&gt;</c> (<see cref="HelperCommandLine"/>), on a pipe
/// name of its own each start, with no window, its input and output its own (what it writes is logged), then connects
/// within <see cref="ConnectTimeout"/>. A helper that ends before it serves (a wrong parent ends it with 0, a failed
/// open with 4) is a start that failed. The tests run the helper's stand-in through it, with its own leading arguments.
/// </summary>
public sealed class LibreHelperProcess : ILibreHelperLauncher
{
    /// <summary>How long a start waits for the helper to serve its pipe.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a stop waits for the helper's process to end once its pipe closed (the helper bounds its own clean-up
    /// at 2 s) before it kills it.
    /// </summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>The most lines of a helper's output the log keeps, per process.</summary>
    private const int MaxLoggedLines = 50;

    private readonly string _path;
    private readonly IReadOnlyList<string> _leading;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <param name="path">The helper's executable.</param>
    /// <param name="leadingArguments">Arguments before the helper's command line (the tests' stand-in takes its behaviour first).</param>
    public LibreHelperProcess(string path, IReadOnlyList<string>? leadingArguments = null, TimeProvider? time = null, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _leading = leadingArguments ?? [];
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<LibreHelperProcess>();
    }

    /// <summary>The helper the engine's publish carries: <c>sensor-helper/InfoPanel.SensorHelper.exe</c>, in a folder of its own beside the engine.</summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "sensor-helper", OperatingSystem.IsWindows() ? "InfoPanel.SensorHelper.exe" : "InfoPanel.SensorHelper");

    /// <summary>The process id of the helper started last (0 before any). For tests.</summary>
    internal int LastProcessId { get; private set; }

    /// <summary>The helper's executable.</summary>
    public string ExecutablePath => _path;

    /// <summary>A pipe name of this start's own: short (Unix keeps pipes as socket paths, which are), never reused.</summary>
    public static string NewPipeName() => "iplibre" + Guid.NewGuid().ToString("N")[..16];

    public async Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
    {
        if (!File.Exists(_path))
            throw new LibreHelperMissingException(_path);

        var pipe = NewPipeName();
        var line = new HelperCommandLine(pipe, Environment.ProcessId, HelperParent.CurrentStartTime());
        var info = new ProcessStartInfo(_path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // nothing writes it: the helper never takes the engine's input
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in _leading)
            info.ArgumentList.Add(argument);
        foreach (var argument in line.Arguments)
            info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info };
        var logged = 0;
        void Logged(string? text, bool error)
        {
            if (text is null || Interlocked.Increment(ref logged) > MaxLoggedLines)
                return;
            if (error)
                _log.Warning("The sensor helper {Id} wrote: {Line}", process.Id, text);
            else
                _log.Debug("The sensor helper {Id} wrote: {Line}", process.Id, text);
        }

        process.OutputDataReceived += (_, e) => Logged(e.Data, error: false);
        process.ErrorDataReceived += (_, e) => Logged(e.Data, error: true);
        process.Start();
        LastProcessId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = ExitOf(process);

        using var gaveUp = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var connect = SensorHelperClient.ConnectAsync(pipe, ConnectTimeout, _time, observer, gaveUp.Token);
        try
        {
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited && !connect.IsCompletedSuccessfully)
            {
                await gaveUp.CancelAsync().ConfigureAwait(false);
                var code = await exited.ConfigureAwait(false);
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The sensor helper ended with exit code {code} before it served."));
            }

            var client = await connect.ConfigureAwait(false);
            _log.Debug("Sensor helper {Id} started on pipe {Pipe}", process.Id, pipe);
            return new Connection(client, exited, process, _log);
        }
        catch
        {
            await gaveUp.CancelAsync().ConfigureAwait(false);
            try
            {
                var client = await connect.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection that failed, or was given up.
            }

            await EndAsync(process, exited, _log).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int?> ExitOf(Process process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Waits <see cref="ExitWait"/> (on the wall clock: a test's manual clock never moves it) for the process to end, then kills it.</summary>
    private static async Task EndAsync(Process process, Task<int?> exited, ILogger log)
    {
        try
        {
            try
            {
                await exited.WaitAsync(ExitWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log.Warning("The sensor helper {Id} didn't end within {Wait} of its pipe closing; killed", process.Id, ExitWait);
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It ended meanwhile.
                }

                await exited.WaitAsync(ExitWait).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception)
        {
            log.Error(ex, "The sensor helper {Id} couldn't be ended", process.Id);
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class Connection(SensorHelperClient client, Task<int?> exited, Process process, ILogger log) : LibreHelperConnection(client, exited)
    {
        private int _disposed;

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await Client.DisposeAsync().ConfigureAwait(false);
            await EndAsync(process, Exited, log).ConfigureAwait(false);
        }
    }
}
