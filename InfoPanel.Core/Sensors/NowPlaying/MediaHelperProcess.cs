using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// What the media helper is started as: <paramref name="FileName"/> (a full path) with <paramref name="Arguments"/>, the
/// engine's environment less <paramref name="RemovedVariables"/>.
/// </summary>
public sealed record MediaHelperStart(string FileName, IReadOnlyList<string> Arguments, IReadOnlyList<string> RemovedVariables);

/// <summary>Starts the media helper's process (<see cref="MediaHelperProcess"/>, or a test's).</summary>
public interface IMediaHelperProcess
{
    /// <summary>Starts it, to end when the engine does however it ends; throws when it can't be started.</summary>
    IMediaHelperRun Start(MediaHelperStart start);
}

/// <summary>
/// One run of the media helper. Its two streams are the same objects for its whole life, disposed or not; their reader
/// disposes each once it has read it to its end, and the run is disposed only after both have.
/// </summary>
public interface IMediaHelperRun : IDisposable
{
    /// <summary>Its standard output, read to its end.</summary>
    Stream Output { get; }

    /// <summary>Its standard error, read to its end.</summary>
    Stream Error { get; }

    /// <summary>Completes with its exit code when it has ended.</summary>
    Task<int> Exited { get; }

    /// <summary>Asks it to end (SIGTERM).</summary>
    void Terminate();

    /// <summary>Ends it at once, with what it started (SIGKILL).</summary>
    void Kill();
}

/// <summary>
/// The media helper as a child process of the engine's (macOS), kept to the engine's lifetime: it runs under
/// <see cref="Lifetime"/>, a few lines of Apple's Perl that start it, then wait for the end of their standard input, a
/// pipe only the engine holds open. However the engine ends (a crash or SIGKILL included), the system closes that pipe,
/// and the lines end the helper (SIGTERM, then SIGKILL after 3 s) and themselves. They pass SIGTERM on, and end with the
/// helper's own exit code when it ends first (128 and the signal's number when a signal ended it).
/// </summary>
public sealed class MediaHelperProcess : IMediaHelperProcess
{
    /// <summary>
    /// The lifetime lines, run as <c>/usr/bin/perl -e {Lifetime} -- {file} {arguments…}</c>: they start the helper as
    /// their child (its standard input <c>/dev/null</c>), watch their own standard input and the child once a second,
    /// and on its end, or SIGTERM, SIGINT or SIGHUP, end the child and then themselves.
    /// </summary>
    public const string Lifetime = """
        use POSIX ();
        my $stop = 0;
        $SIG{$_} = sub { $stop = 1 } for qw(TERM INT HUP);
        my $pid = fork;
        exit 70 unless defined $pid;
        if (!$pid) {
          $SIG{$_} = 'DEFAULT' for qw(TERM INT HUP);
          open STDIN, '<', '/dev/null';
          exec { $ARGV[0] } @ARGV;
          POSIX::_exit(127);
        }
        sub code { my $s = shift; ($s & 127) ? 128 + ($s & 127) : $s >> 8 }
        my $bits = '';
        vec($bits, fileno(STDIN), 1) = 1;
        until ($stop) {
          exit code($?) if waitpid($pid, POSIX::WNOHANG()) == $pid;
          my $ready = select(my $out = $bits, undef, undef, 1);
          $stop = 1 if $ready > 0 && !sysread(STDIN, my $buffer, 512);
        }
        kill 'TERM', $pid;
        $SIG{ALRM} = sub { kill 'KILL', $pid };
        alarm 3;
        waitpid($pid, 0);
        exit code($?);
        """;

    public IMediaHelperRun Start(MediaHelperStart start)
    {
        var info = new ProcessStartInfo(MacNowPlayingReader.Perl)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = "/",
        };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(Lifetime);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(start.FileName);
        foreach (var argument in start.Arguments)
            info.ArgumentList.Add(argument);
        foreach (var variable in start.RemovedVariables)
            info.Environment.Remove(variable);

        var process = Process.Start(info) ?? throw new InvalidOperationException("The media helper didn't start");
        // Taken once: a Process's stream getters throw once it is disposed, which can be while a reader reads on.
        return new Run(process, process.StandardInput, process.StandardOutput.BaseStream, process.StandardError.BaseStream);
    }

    /// <param name="lifetime">The helper's standard input, held open while it runs: closed, it ends.</param>
    /// <param name="output">Its standard output, taken at its start; disposed by its reader, not the run.</param>
    /// <param name="error">Its standard error, taken at its start; disposed by its reader, not the run.</param>
    private sealed class Run(Process process, StreamWriter lifetime, Stream output, Stream error) : IMediaHelperRun
    {
        private readonly Task<int> _exited = WaitAsync(process);

        public Stream Output => output;

        public Stream Error => error;

        public Task<int> Exited => _exited;

        private static async Task<int> WaitAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Terminate()
        {
            try
            {
                if (!process.HasExited)
                    _ = kill(process.Id, SigTerm);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            try
            {
                lifetime.Dispose();
            }
            catch (IOException)
            {
            }

            process.Dispose();
        }
    }

    private const int SigTerm = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
