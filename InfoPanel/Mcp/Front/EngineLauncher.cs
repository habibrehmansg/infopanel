using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Hosting;

namespace InfoPanel.Mcp.Front;

/// <summary>An engine the front started: its process id when known, and its exit code once it has exited (when it can tell).</summary>
public sealed class StartedEngine(int? pid, Func<int?> exitCode)
{
    public int? Pid { get; } = pid;

    /// <summary>The exit code once the process has exited; null while it runs or when the front can't tell.</summary>
    public int? ExitCode => exitCode();
}

/// <summary>Starts the engine for the front, detached from it.</summary>
public interface IEngineLauncher
{
    /// <exception cref="IOException">It couldn't be started.</exception>
    StartedEngine Start(IReadOnlyList<string> args);
}

/// <summary>
/// Starts this executable as the engine so that it outlives the front and the AI app that started the front:
/// <list type="bullet">
/// <item>macOS and Linux: <c>posix_spawn</c> in a new session (<c>POSIX_SPAWN_SETSID</c>), stdin, stdout and stderr on
/// <c>/dev/null</c> (the front's stdout is the protocol; the engine logs to its file), on macOS with every other
/// descriptor closed. Its exit code is read with <c>waitpid</c>, so an engine that finds the folder in use (exit 3) is
/// told apart. Inside an app bundle on macOS, <c>/usr/bin/open -g -j -a</c> instead, so LaunchServices parents it.</item>
/// <item>Windows: a shell start, hidden.</item>
/// </list>
/// </summary>
public sealed class EngineLauncher : IEngineLauncher
{
    public StartedEngine Start(IReadOnlyList<string> args)
    {
        var (program, prefix) = Self();
        if (OperatingSystem.IsWindows())
            return StartWindows(program, [.. prefix, .. args]);

        if (OperatingSystem.IsMacOS() && Bundle(program) is { } bundle)
        {
            // LaunchServices starts it; open's own exit says nothing about the engine's.
            Spawn("/usr/bin/open", ["-g", "-j", "-a", bundle, "--args", .. args]);
            return new StartedEngine(null, () => null);
        }

        var pid = Spawn(program, [.. prefix, .. args]);
        // The front is its parent until it exits: a thread waits for it, so an engine that stops is reaped at once (not
        // left a zombie that still looks alive) and its exit code is known.
        var exited = new StrongBox<int>(-1);
        new Thread(() =>
        {
            if (Posix.waitpid(pid, out var status, 0) == pid)
                Volatile.Write(ref exited.Value, (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f));
        })
        { IsBackground = true, Name = $"engine {pid}" }.Start();
        return new StartedEngine(pid, () => Volatile.Read(ref exited.Value) is var code && code >= 0 ? code : null);
    }

    /// <summary>
    /// An environment variable the engine would read as one of its own settings (<c>Engine__Fresh</c>, and the host's
    /// prefixed forms <c>DOTNET_Engine__…</c> and <c>ASPNETCORE_Engine__…</c>): never passed on, so the engine an AI app
    /// starts runs with the launch record's flags only. (A Windows shell start can't trim the environment; the flags the
    /// front passes, <c>--fresh=false</c> among them, outrank it there.)
    /// </summary>
    internal static bool IsEngineSetting(string name)
    {
        foreach (var prefix in new[] { "DOTNET_", "ASPNETCORE_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }
        return name.Equals(EngineOptions.Section, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(EngineOptions.Section + "__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(EngineOptions.Section + ":", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The executable to start and the arguments before the engine's (the entry assembly under <c>dotnet</c>).</summary>
    private static (string Program, string[] Prefix) Self()
    {
        var process = Environment.ProcessPath ?? throw new IOException("This process's executable isn't known.");
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = typeof(EngineLauncher).Assembly.Location;
            return (process, [assembly]);
        }
        return (process, []);
    }

    /// <summary>The <c>.app</c> folder an executable at <c>X.app/Contents/MacOS/…</c> belongs to.</summary>
    internal static string? Bundle(string program)
    {
        var macOS = Path.GetDirectoryName(program);
        var contents = macOS == null ? null : Path.GetDirectoryName(macOS);
        var app = contents == null ? null : Path.GetDirectoryName(contents);
        return app != null && Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents"
            && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    private static StartedEngine StartWindows(string program, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("The engine didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException(ex.Message, ex);
        }
        return new StartedEngine(process.Id, () => process.HasExited ? process.ExitCode : null);
    }

    /// <summary>posix_spawn in a new session with stdio on /dev/null; the child's pid.</summary>
    private static int Spawn(string program, IReadOnlyList<string> args)
    {
        var attr = Marshal.AllocHGlobal(1024);
        var actions = Marshal.AllocHGlobal(1024);
        var strings = new List<IntPtr>();
        try
        {
            Check(Posix.posix_spawnattr_init(attr), "posix_spawnattr_init");
            Check(Posix.posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            try
            {
                var flags = Posix.POSIX_SPAWN_SETSID | (OperatingSystem.IsMacOS() ? Posix.POSIX_SPAWN_CLOEXEC_DEFAULT : 0);
                Check(Posix.posix_spawnattr_setflags(attr, (short)flags), "posix_spawnattr_setflags");
                Check(Posix.posix_spawn_file_actions_addopen(actions, 0, "/dev/null", Posix.O_RDONLY, 0), "addopen 0");
                Check(Posix.posix_spawn_file_actions_addopen(actions, 1, "/dev/null", Posix.O_WRONLY, 0), "addopen 1");
                Check(Posix.posix_spawn_file_actions_addopen(actions, 2, "/dev/null", Posix.O_WRONLY, 0), "addopen 2");

                IntPtr Text(string s)
                {
                    var p = Marshal.StringToCoTaskMemUTF8(s);
                    strings.Add(p);
                    return p;
                }

                var argv = new IntPtr[args.Count + 2];
                argv[0] = Text(program);
                for (var i = 0; i < args.Count; i++)
                    argv[i + 1] = Text(args[i]);
                var environment = Environment.GetEnvironmentVariables();
                var envp = new IntPtr[environment.Count + 1];
                var n = 0;
                foreach (System.Collections.DictionaryEntry entry in environment)
                {
                    if (!IsEngineSetting((string)entry.Key))
                        envp[n++] = Text($"{entry.Key}={entry.Value}");
                }

                var error = Posix.posix_spawn(out var pid, program, actions, attr, argv, envp);
                if (error != 0)
                    throw new IOException($"posix_spawn of {program} failed: {Marshal.GetPInvokeErrorMessage(error)} ({error})");
                return pid;
            }
            finally
            {
                Posix.posix_spawn_file_actions_destroy(actions);
                Posix.posix_spawnattr_destroy(attr);
            }
        }
        finally
        {
            foreach (var p in strings)
                Marshal.FreeCoTaskMem(p);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
        }

        static void Check(int error, string what)
        {
            if (error != 0)
                throw new IOException($"{what} failed: {Marshal.GetPInvokeErrorMessage(error)} ({error})");
        }
    }

    private static class Posix
    {
        public static int POSIX_SPAWN_SETSID => OperatingSystem.IsMacOS() ? 0x0400 : 0x80;
        public const int POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000; // macOS only
        public const int O_RDONLY = 0;
        public const int O_WRONLY = 1;
        public const int WNOHANG = 1;

        [DllImport("libc", SetLastError = true)]
        public static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

        [DllImport("libc")]
        public static extern int posix_spawnattr_init(IntPtr attr);

        [DllImport("libc")]
        public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

        [DllImport("libc")]
        public static extern int posix_spawnattr_destroy(IntPtr attr);

        [DllImport("libc")]
        public static extern int posix_spawn_file_actions_init(IntPtr actions);

        [DllImport("libc")]
        public static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

        [DllImport("libc")]
        public static extern int posix_spawn_file_actions_destroy(IntPtr actions);

        [DllImport("libc", SetLastError = true)]
        public static extern int waitpid(int pid, out int status, int options);
    }
}
