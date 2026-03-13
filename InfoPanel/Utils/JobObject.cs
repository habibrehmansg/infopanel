using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InfoPanel.Utils
{
    /// <summary>
    /// Wraps a Win32 Job Object so that child processes are automatically killed
    /// when the parent process (or the job handle) is closed.
    /// </summary>
    internal sealed class JobObject : IDisposable
    {
        private IntPtr _handle;

        public JobObject()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int size = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Creates a child process assigned to this job object so it is automatically
        /// killed when the job handle is closed (i.e. when the parent process exits).
        /// </summary>
        public Process CreateChildProcess(string exePath, string arguments, bool redirectOutput = true)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(JobObject));

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            IntPtr lpAttributeList = IntPtr.Zero;
            IntPtr jobHandleBuffer = IntPtr.Zero;

            // Pipes for stdout/stderr redirection
            IntPtr hStdOutRead = IntPtr.Zero, hStdOutWrite = IntPtr.Zero;
            IntPtr hStdErrRead = IntPtr.Zero, hStdErrWrite = IntPtr.Zero;

            try
            {
                // --- Set up redirected stdout/stderr ---
                if (redirectOutput)
                {
                    var sa = new SECURITY_ATTRIBUTES
                    {
                        nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                        bInheritHandle = true,
                        lpSecurityDescriptor = IntPtr.Zero
                    };

                    if (!CreatePipe(out hStdOutRead, out hStdOutWrite, ref sa, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    SetHandleInformation(hStdOutRead, HANDLE_FLAG_INHERIT, 0);

                    if (!CreatePipe(out hStdErrRead, out hStdErrWrite, ref sa, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    SetHandleInformation(hStdErrRead, HANDLE_FLAG_INHERIT, 0);

                    si.StartupInfo.hStdOutput = hStdOutWrite;
                    si.StartupInfo.hStdError = hStdErrWrite;
                    si.StartupInfo.hStdInput = IntPtr.Zero;
                    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                }

                // --- Initialize proc thread attribute list (job assignment only) ---
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

                lpAttributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                si.lpAttributeList = lpAttributeList;

                // Assign to job atomically at creation
                jobHandleBuffer = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobHandleBuffer, _handle);

                if (!UpdateProcThreadAttribute(
                        lpAttributeList, 0,
                        PROC_THREAD_ATTRIBUTE_JOB_LIST,
                        jobHandleBuffer, (IntPtr)IntPtr.Size,
                        IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // --- CreateProcess ---
                string commandLine = $"\"{exePath}\" {arguments}";
                uint flags = CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT;

                if (!CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        redirectOutput,  // inherit handles for pipe redirection
                        flags,
                        IntPtr.Zero,
                        null,
                        ref si,
                        out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // Close the write ends of our pipes so reads will eventually EOF
                if (redirectOutput)
                {
                    CloseHandle(hStdOutWrite); hStdOutWrite = IntPtr.Zero;
                    CloseHandle(hStdErrWrite); hStdErrWrite = IntPtr.Zero;
                }

                // Wrap in a .NET Process object for lifetime management
                CloseHandle(pi.hThread);
                var process = Process.GetProcessById(pi.dwProcessId);
                CloseHandle(pi.hProcess);

                return process;
            }
            finally
            {
                if (lpAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(lpAttributeList);
                    Marshal.FreeHGlobal(lpAttributeList);
                }
                if (jobHandleBuffer != IntPtr.Zero) Marshal.FreeHGlobal(jobHandleBuffer);

                // Clean up any pipe handles that weren't transferred
                if (hStdOutRead != IntPtr.Zero) CloseHandle(hStdOutRead);
                if (hStdOutWrite != IntPtr.Zero) CloseHandle(hStdOutWrite);
                if (hStdErrRead != IntPtr.Zero) CloseHandle(hStdErrRead);
                if (hStdErrWrite != IntPtr.Zero) CloseHandle(hStdErrWrite);
            }
        }

        public void AssignProcess(Process process)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(JobObject));

            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        // --- Constants ---

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        private const uint STARTF_USESTDHANDLES = 0x00000100;

        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = (IntPtr)0x0002000D;

        // --- Structs ---

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
            public IntPtr lpSecurityDescriptor;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        // --- P/Invoke ---

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
    }
}
