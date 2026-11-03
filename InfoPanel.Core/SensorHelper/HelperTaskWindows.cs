using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.SensorHelper;

/// <summary>Windows' side of the helper's task, shared by the helper and the engine: known folders, final paths, users.</summary>
[SupportedOSPlatform("windows")]
public static class HelperWindows
{
    private static readonly Guid ProgramFilesFolder = new("905e63b6-c1bf-494e-b29c-65b732d3d21a"); // FOLDERID_ProgramFiles

    /// <summary>Program Files from the known-folder API (<c>SHGetKnownFolderPath(FOLDERID_ProgramFiles)</c>), never an environment variable; null when Windows can't say.</summary>
    public static string? ProgramFiles()
    {
        var id = ProgramFilesFolder;
        var hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
        try
        {
            return hr == 0 && path != IntPtr.Zero ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            if (path != IntPtr.Zero)
                Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>
    /// <c>GetFinalPathNameByHandleW</c> with <c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: <c>\\?\C:\…</c> or
    /// <c>\\?\UNC\server\share\…</c>. Throws <see cref="Win32Exception"/> when Windows can't say.
    /// </summary>
    public static string FinalPath(SafeFileHandle file)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[length];
        }
    }

    /// <summary>Whether <paramref name="sid"/> names a user here (<c>LookupAccountSid</c> says <c>SidTypeUser</c>).</summary>
    public static bool IsUserAccount(string sid)
    {
        SecurityIdentifier id;
        try
        {
            id = new SecurityIdentifier(sid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var bytes = new byte[id.BinaryLength];
        id.GetBinaryForm(bytes, 0);
        uint nameLength = 0, domainLength = 0;
        LookupAccountSid(null, bytes, null, ref nameLength, null, ref domainLength, out _);
        if (nameLength == 0)
            return false;
        var name = new char[nameLength];
        var domain = new char[Math.Max(1u, domainLength)];
        domainLength = (uint)domain.Length;
        if (!LookupAccountSid(null, bytes, name, ref nameLength, domain, ref domainLength, out var use))
            return false;
        return use == 1; // SidTypeUser
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(string? system, byte[] sid, [Out] char[]? name, ref uint nameLength, [Out] char[]? domain, ref uint domainLength, out int use);
}

/// <summary>
/// The Task Scheduler through its own COM object (<c>Schedule.Service</c>, late-bound: no package), the definition passed
/// in memory, never through a file.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsTaskScheduler
{
    private const int CreateOrUpdate = 6; // TASK_CREATE_OR_UPDATE
    private const int ServiceAccount = 5; // TASK_LOGON_SERVICE_ACCOUNT
    private const int ExecAction = 0; // TASK_ACTION_EXEC

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static (string Folder, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('\\');
        return (slash <= 0 ? @"\" : path[..slash], path[(slash + 1)..]);
    }

    private const int FileNotFound = unchecked((int)0x80070002); // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
    private const int PathNotFound = unchecked((int)0x80070003); // HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)

    /// <summary>Whether <paramref name="ex"/> says the task or its folder isn't there (not found), as opposed to the lookup failing.</summary>
    private static bool IsAbsence(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException || ex is COMException { HResult: FileNotFound or PathNotFound };

    /// <summary>
    /// The task at <paramref name="path"/>, or null when there is none (or its folder isn't there). Any other failure
    /// (the Task Scheduler busy or unavailable, access refused) is thrown: whether the task is there isn't known.
    /// </summary>
    private static dynamic? Find(dynamic service, string path)
    {
        var (folder, name) = Split(path);
        try
        {
            return service.GetFolder(folder).GetTask(name);
        }
        catch (Exception ex) when (IsAbsence(ex))
        {
            return null;
        }
    }

    public static bool Exists(string path) => Find(Connect(), path) is not null;

    public static void Delete(string path)
    {
        var (folder, name) = Split(path);
        Connect().GetFolder(folder).DeleteTask(name, 0);
    }

    /// <summary>The folder at <paramref name="path"/>, created (under the root) when it isn't there.</summary>
    private static dynamic FolderOf(dynamic service, string path)
    {
        try
        {
            return service.GetFolder(path);
        }
        catch (Exception ex) when (IsAbsence(ex))
        {
            return service.GetFolder(@"\").CreateFolder(path.TrimStart('\\'), Type.Missing);
        }
    }

    /// <summary>
    /// Registers <paramref name="xml"/> at <paramref name="path"/>, run as SYSTEM, with the security descriptor
    /// <paramref name="sddl"/>; its folder is created when missing. A task already there is updated, keeping its own
    /// security descriptor (<see cref="SetSecurity"/> replaces it).
    /// </summary>
    public static void Register(string path, string xml, string sddl)
    {
        var service = Connect();
        var (folderPath, name) = Split(path);
        FolderOf(service, folderPath).RegisterTask(name, xml, CreateOrUpdate, HelperTask.SystemSid, Type.Missing, ServiceAccount, sddl);
    }

    /// <summary>Sets the security descriptor (owner and DACL, from <paramref name="sddl"/>) of the task at <paramref name="path"/>.</summary>
    public static void SetSecurity(string path, string sddl)
    {
        var (folder, name) = Split(path);
        Connect().GetFolder(folder).GetTask(name).SetSecurityDescriptor(sddl, 0);
    }

    /// <summary>Creates the folder at <paramref name="path"/> when missing, and sets its security descriptor (owner and DACL, from <paramref name="sddl"/>).</summary>
    public static void SetFolderSecurity(string path, string sddl) => FolderOf(Connect(), path).SetSecurityDescriptor(sddl, 0);

    private const int EnumHidden = 1; // TASK_ENUM_HIDDEN

    /// <summary>Removes the folder at <paramref name="path"/> when it holds no task (hidden ones counted) and no folder; whether it did (false too when it isn't there).</summary>
    public static bool DeleteFolderIfEmpty(string path)
    {
        var service = Connect();
        dynamic folder;
        try
        {
            folder = service.GetFolder(path);
        }
        catch (Exception ex) when (IsAbsence(ex))
        {
            return false;
        }

        if ((int)folder.GetTasks(EnumHidden).Count > 0 || (int)folder.GetFolders(0).Count > 0)
            return false;
        var (parent, name) = Split(path);
        service.GetFolder(parent).DeleteFolder(name, 0);
        return true;
    }

    /// <summary>Has the Task Scheduler read <paramref name="xml"/> into a new definition, registering nothing: throws when its schema refuses it.</summary>
    public static void Validate(string xml)
    {
        dynamic definition = Connect().NewTask(0);
        definition.XmlText = xml;
    }

    /// <summary>The first action of the task at <paramref name="path"/> when it runs a program (its path and arguments), or null when there is no such task; throws when it can't be read (<see cref="Find"/>).</summary>
    public static (string Path, string Arguments)? ActionOf(string path) => ActionOf(Connect(), path);

    /// <summary><see cref="ActionOf(string)"/> on <paramref name="service"/>: the Task Scheduler's, or a test's stand-in.</summary>
    internal static (string Path, string Arguments)? ActionOf(object service, string path)
    {
        var task = Find(service, path);
        if (task is null)
            return null;
        dynamic actions = task.Definition.Actions;
        if ((int)actions.Count < 1)
            return null;
        dynamic action = actions.Item(1);
        if ((int)action.Type != ExecAction)
            return null;
        return ((string)(action.Path ?? ""), (string)(action.Arguments ?? ""));
    }

    /// <summary>The definition of the task at <paramref name="path"/> (its principal's SID and its actions), or null when there is no such task; throws when it can't be read (<see cref="Find"/>).</summary>
    public static HelperTaskDefinition? DefinitionOf(string path) => DefinitionOf((object)Connect(), path, SidOf);

    /// <summary><see cref="DefinitionOf(string)"/> on <paramref name="service"/> (the Task Scheduler's, or a test's stand-in), its principal told as a SID by <paramref name="sidOf"/>.</summary>
    internal static HelperTaskDefinition? DefinitionOf(object service, string path, Func<string, string?> sidOf)
    {
        var task = Find(service, path);
        if (task is null)
            return null;
        dynamic definition = task.Definition;
        var user = (string?)definition.Principal.UserId;
        dynamic actions = definition.Actions;
        var count = (int)actions.Count;
        var list = new HelperTaskAction[count];
        for (var i = 0; i < count; i++)
        {
            dynamic action = actions.Item(i + 1);
            list[i] = (int)action.Type == ExecAction
                ? new HelperTaskAction((string)(action.Path ?? ""), (string)(action.Arguments ?? ""))
                : new HelperTaskAction(null, "");
        }

        return new HelperTaskDefinition(string.IsNullOrEmpty(user) ? null : sidOf(user), list);
    }

    /// <summary>
    /// The SID of the principal <paramref name="name"/> as the Task Scheduler gives it (a SID as written, or a name:
    /// <c>SYSTEM</c>, which it gives for SYSTEM on the PC, <c>NT AUTHORITY\SYSTEM</c>, localised), null when Windows can't
    /// tell it.
    /// </summary>
    private static string? SidOf(string name)
    {
        try
        {
            return name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(name).Value
                : ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (SystemException) // not mapped (IdentityNotMappedException), malformed, or the lookup failed
        {
            return null;
        }
    }
}

/// <summary>The task's helper's <c>--register</c> and <c>--unregister</c> on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHelperTaskHost : IHelperTaskHost
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string? OwnPath()
    {
        if (Environment.ProcessPath is not { } path)
            return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return HelperWindows.FinalPath(file.SafeFileHandle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    public string? ProgramFiles() => HelperWindows.ProgramFiles();

    public bool IsUserAccount(string sid) => HelperWindows.IsUserAccount(sid);

    public bool TaskExists(string path) => WindowsTaskScheduler.Exists(path);

    public void DeleteTask(string path) => WindowsTaskScheduler.Delete(path);

    public void RegisterTask(string path, string xml, string securityDescriptor) => WindowsTaskScheduler.Register(path, xml, securityDescriptor);

    public void SetTaskSecurity(string path, string securityDescriptor) => WindowsTaskScheduler.SetSecurity(path, securityDescriptor);

    public void SetFolderSecurity(string folder, string securityDescriptor) => WindowsTaskScheduler.SetFolderSecurity(folder, securityDescriptor);

    public bool DeleteFolderIfEmpty(string folder) => WindowsTaskScheduler.DeleteFolderIfEmpty(folder);

    public HelperTaskDefinition? TaskDefinition(string path) => WindowsTaskScheduler.DefinitionOf(path);
}

/// <summary>The task's pipe on Windows: its security, its client's user, its owner.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsHelperPipe
{
    /// <summary>
    /// The pipe's security for the user <paramref name="sid"/>: owned by <paramref name="owner"/> (SYSTEM, the helper's
    /// task's principal; a test's own user in the Windows tests, which can't create a pipe SYSTEM owns), its DACL protected
    /// from inheritance, denying any network logon, granting SYSTEM full control and that user read and write, nobody else
    /// anything.
    /// </summary>
    public static PipeSecurity Security(string sid, string owner = HelperTask.SystemSid)
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(new SecurityIdentifier(owner));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The pipe's server end: one instance, created first (an existing pipe of that name, a squatter's, fails it with
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>), remote clients refused by its DACL,
    /// with <see cref="Security"/>.
    /// </summary>
    public static NamedPipeServerStream Create(string name, string sid, string owner = HelperTask.SystemSid) =>
        NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0,
            Security(sid, owner));

    /// <summary>
    /// The user connected to <paramref name="pipe"/> and their integrity level, read from their token through
    /// <see cref="HelperPipeClient.Read"/> (identification is enough); null when either can't be told.
    /// </summary>
    public static HelperClient? ClientOf(NamedPipeServerStream pipe) => HelperPipeClient.Read(pipe, new ClientToken());

    /// <summary>Windows' own calls for <see cref="HelperPipeClient.Read"/>.</summary>
    private sealed class ClientToken : IPipeClientToken
    {
        private const uint TokenQuery = 0x0008; // TOKEN_QUERY

        static ClientToken()
        {
            // The calls made while impersonating, made once before: nothing of theirs is loaded or prepared as the client.
            if (OpenThreadTokenNative(GetCurrentThread(), TokenQuery, true, out var none))
                CloseHandle(none);
            RevertToSelfNative();
        }

        public bool Impersonate(NamedPipeServerStream pipe) => ImpersonateNamedPipeClient(pipe.SafePipeHandle);

        public IntPtr OpenThreadToken() => OpenThreadTokenNative(GetCurrentThread(), TokenQuery, true, out var token) ? token : IntPtr.Zero;

        public void RevertToSelf()
        {
            if (!RevertToSelfNative())
                Environment.FailFast("The sensor helper couldn't stop impersonating its pipe's client.");
        }

        public HelperClient? Read(IntPtr token)
        {
            using var handle = new SafeAccessTokenHandle(token);
            return SidOf(handle, TokenUserClass) is { } user && IntegrityOf(handle) is { } integrity ? new HelperClient(user, integrity) : null;
        }
    }

    private const int TokenUserClass = 1; // TokenUser: TOKEN_USER, its SID_AND_ATTRIBUTES.Sid first
    private const int TokenIntegrityLevelClass = 25; // TokenIntegrityLevel: TOKEN_MANDATORY_LABEL, its Label.Sid first

    /// <summary>The SID a token information class holds first (<paramref name="informationClass"/>: <c>TokenUser</c>, <c>TokenIntegrityLevel</c>), null when Windows can't say.</summary>
    private static string? SidOf(SafeAccessTokenHandle token, int informationClass)
    {
        GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
            return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            return GetTokenInformation(token, informationClass, buffer, needed, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The integrity level of <paramref name="token"/>: the last sub-authority of its mandatory label (<c>TokenIntegrityLevel</c>), null when Windows can't say.</summary>
    public static int? IntegrityOf(SafeAccessTokenHandle token)
    {
        var label = SidOf(token, TokenIntegrityLevelClass); // S-1-16-<level>
        return label is not null && label.StartsWith("S-1-16-", StringComparison.Ordinal) && int.TryParse(label.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var level)
            ? level
            : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", EntryPoint = "OpenThreadToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadTokenNative(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "RevertToSelf", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelfNative();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    /// <summary>The owner of the pipe <paramref name="pipe"/> is open on (<c>GetSecurityInfo</c> on the handle), as a SID; null when Windows can't say.</summary>
    public static string? OwnerOf(SafePipeHandle pipe)
    {
        var error = GetSecurityInfo(pipe, 6 /* SE_KERNEL_OBJECT */, 1 /* OWNER_SECURITY_INFORMATION */, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0)
            return null;
        try
        {
            return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner).Value;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, int securityInfo, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);
}
