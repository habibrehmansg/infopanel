using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Hosting;

/// <summary>
/// The terminal a Windows engine was started from. On Windows the engine is a Windows-subsystem program
/// (<c>InfoPanel/InfoPanel.csproj</c>: <c>WinExe</c> for a Windows runtime), so Windows gives it no console window: a
/// shortcut, a double-click, the installer's finish page or the sign-in Run value start it with none, and nothing closes
/// it but the engine itself. Started from <c>cmd</c> or PowerShell it has no console either, so its output (<c>--help</c>,
/// argument errors, the console log, the sign-in and pairing links) would go nowhere: <see cref="AttachIfNeeded()"/>,
/// first thing in <c>Program.cs</c> (after the plugin and panel hosts' modes, which never come here), attaches it to the
/// console of the process that started it, when there is one.
/// </summary>
/// <remarks>
/// The rule (<see cref="AttachIfNeeded(bool, IConsoleApi)"/>): only on Windows; only when standard output or standard
/// error can't be written to (<see cref="IsWritable"/>: not 0, not -1, and a write of zero bytes to it succeeds, the
/// .NET runtime's own test of whether <see cref="Console.Out"/> gets a real stream; a pipe, a file and <c>NUL</c> are
/// writable, a stale value, a read-only handle and a console this process isn't attached to are not). A parent that
/// sent both somewhere writable, as the tests, the release workflow's smoke run and the engine's own hosts do, is left
/// alone: nothing is called but those two probes. Otherwise every slot that is unusable (an output stream that isn't
/// writable; standard input, judged apart by <see cref="IsUsableInput"/>, never a reason to attach) is cleared to 0,
/// whatever its value, -1 included, never closed; then <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>, and nothing else.
/// No parent console (a double-click): the attach fails, nothing is attached, the cleared slots stay 0, no console is
/// ever created (<c>AllocConsole</c> is never called), and .NET's <see cref="Console"/> writes to an absent handle go
/// nowhere without throwing. Attached: Windows gives the console's handles to the slots that hold 0 (to all three when
/// the parent passed no handles); a usable one the attach changed is put back as it was, so a partly redirected start
/// (<c>InfoPanel.exe --help 2&gt;err.txt</c>) writes each stream where the parent sent it, and a cleared slot keeps the
/// console's. A terminal doesn't wait for a Windows-subsystem program: the prompt comes
/// back at once and the exit code isn't the shell's unless the caller waits (<c>start /wait</c>, PowerShell's
/// <c>Start-Process -Wait</c>, or a redirect); and attached, the engine shares that console, so closing it or Ctrl+C
/// there stops the engine as it would a console program (docs/agents/hosting.md).
/// </remarks>
internal static partial class ParentConsole
{
    /// <summary><c>STD_INPUT_HANDLE</c>.</summary>
    internal const int StdInput = -10;

    /// <summary><c>STD_OUTPUT_HANDLE</c>.</summary>
    internal const int StdOutput = -11;

    /// <summary><c>STD_ERROR_HANDLE</c>.</summary>
    internal const int StdError = -12;

    /// <summary><c>ATTACH_PARENT_PROCESS</c>: the console of the process that started this one.</summary>
    internal const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>What the attach asks of Windows: the tests' seam.</summary>
    internal interface IConsoleApi
    {
        /// <summary><c>GetStdHandle</c>: the handle, 0 when the process has none, -1 (<c>INVALID_HANDLE_VALUE</c>) on failure.</summary>
        nint GetStdHandle(int which);

        /// <summary><c>SetStdHandle</c>.</summary>
        bool SetStdHandle(int which, nint handle);

        /// <summary><c>AttachConsole</c>: false when the process has no such console to attach to.</summary>
        bool AttachConsole(uint processId);

        /// <summary>
        /// <c>GetFileType</c>: <see cref="FileTypeUnknown"/> (0) for a value that is no open handle in this process (an
        /// error) or one Windows can't type; otherwise disk (1), character (2, a console) or pipe (3).
        /// </summary>
        uint GetFileType(nint handle);

        /// <summary>
        /// <c>WriteFile</c> of zero bytes to <paramref name="handle"/>: true when Windows takes the write. .NET's own test
        /// of a standard output stream (<c>ConsolePal.Windows</c>'s <c>ConsoleHandleIsWritable</c>), made the same way.
        /// </summary>
        bool WritesZeroBytes(nint handle);
    }

    /// <summary><c>FILE_TYPE_UNKNOWN</c>.</summary>
    internal const uint FileTypeUnknown = 0;

    /// <summary>
    /// A standard output stream (stdout, stderr) the process can write to: neither 0 (none given) nor -1
    /// (<c>INVALID_HANDLE_VALUE</c>), and a <c>WriteFile</c> of zero bytes to it succeeds, as .NET's
    /// <c>ConsolePal.Windows.ConsoleHandleIsWritable</c> asks before giving <see cref="Console.Out"/> a real stream. A
    /// parent can hand a Windows-subsystem child values that are no open handle in it (stale), a read-only handle, or
    /// handles to its own console, which Windows types as a character device but which take no write until this process
    /// is attached: all fail it. The probe writes no data: a file is neither truncated nor extended, and a byte-mode
    /// pipe's reader, <c>NUL</c> and a console get nothing. On a message-mode named pipe the reader sees one empty
    /// message for each stream probed, which .NET's own set-up of <see cref="Console.Out"/> or
    /// <see cref="Console.Error"/> also sends, the same write to the same handle, when a program first uses that stream.
    /// </summary>
    internal static bool IsWritable(IConsoleApi api, nint handle) =>
        handle != 0 && handle != -1 && api.WritesZeroBytes(handle);

    /// <summary>
    /// Standard input, judged apart (it is only read, and only ever kept, never a reason to attach): neither 0 nor -1,
    /// and one Windows knows a type for. <c>GetFileType</c> fails on a stale value, <see cref="FileTypeUnknown"/>, and so
    /// does a handle it can't type, error or not.
    /// </summary>
    internal static bool IsUsableInput(IConsoleApi api, nint handle) =>
        handle != 0 && handle != -1 && api.GetFileType(handle) != FileTypeUnknown;

    /// <summary>
    /// Attaches this process to its parent's console when it runs on Windows and its standard output or error can't be
    /// written to; true when it attached. Called before anything touches <see cref="Console"/>.
    /// </summary>
    internal static bool AttachIfNeeded() => OperatingSystem.IsWindows() && AttachIfNeeded(windows: true, new Win32ConsoleApi());

    /// <summary>The rule, over <paramref name="api"/>: see the class's remarks.</summary>
    internal static bool AttachIfNeeded(bool windows, IConsoleApi api)
    {
        if (!windows)
            return false;
        var input = api.GetStdHandle(StdInput);
        var output = api.GetStdHandle(StdOutput);
        var error = api.GetStdHandle(StdError);
        var outputUsable = IsWritable(api, output);
        var errorUsable = IsWritable(api, error);
        // Both writable, redirected (or inherited) by the parent: they stay exactly as they are, the probes the only calls.
        if (outputUsable && errorUsable)
            return false;
        var streams = new[] { (StdInput, input, IsUsableInput(api, input)), (StdOutput, output, outputUsable), (StdError, error, errorUsable) };
        // Every unusable slot is cleared to 0, -1 included: Windows' attach fills only a slot that holds 0 (GetStdHandle,
        // "Attach/detach behavior"). The value itself is never closed or used: it isn't this process's.
        foreach (var (which, handle, usable) in streams)
        {
            if (!usable && handle != 0)
                api.SetStdHandle(which, 0);
        }
        // No console in the parent (a shortcut, a double-click): nothing is attached, none is made, cleared slots stay 0.
        if (!api.AttachConsole(AttachParentProcess))
            return false;
        // What the parent sent to a usable handle stays where it sent it; a cleared slot keeps what the attach gave it.
        foreach (var (which, handle, usable) in streams)
        {
            if (usable && api.GetStdHandle(which) != handle)
                api.SetStdHandle(which, handle);
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private sealed class Win32ConsoleApi : IConsoleApi
    {
        public nint GetStdHandle(int which) => Native.GetStdHandle(which);

        public bool SetStdHandle(int which, nint handle) => Native.SetStdHandle(which, handle) != 0;

        public bool AttachConsole(uint processId) => Native.AttachConsole(processId) != 0;

        public uint GetFileType(nint handle) => Native.GetFileType(handle);

        public unsafe bool WritesZeroBytes(nint handle)
        {
            byte unused = 0x41;
            return Native.WriteFile(handle, &unused, 0, out _, 0) != 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class Native
    {
        // HANDLE GetStdHandle(DWORD nStdHandle)
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint GetStdHandle(int which);

        // BOOL SetStdHandle(DWORD nStdHandle, HANDLE hHandle)
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int SetStdHandle(int which, nint handle);

        // BOOL AttachConsole(DWORD dwProcessId)
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int AttachConsole(uint processId);

        // DWORD GetFileType(HANDLE hFile): FILE_TYPE_UNKNOWN, with an error or without one, for a handle it can't type
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial uint GetFileType(nint handle);

        // BOOL WriteFile(HANDLE hFile, LPCVOID lpBuffer, DWORD nNumberOfBytesToWrite, LPDWORD lpNumberOfBytesWritten, LPOVERLAPPED lpOverlapped)
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static unsafe partial int WriteFile(nint handle, byte* buffer, uint count, out uint written, nint overlapped);
    }
}
