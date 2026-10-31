using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace InfoPanel.Hosting;

/// <summary>One entry of a folder's access list: whether it allows or denies, and whom (a SID).</summary>
public sealed record WindowsAce(bool Allow, string Sid);

/// <summary>A folder's owner (a SID; null when it can't be read), whether its access list is protected from inheritance, and its entries.</summary>
public sealed record WindowsFolderAcl(string? Owner, bool Protected, IReadOnlyList<WindowsAce> Entries);

/// <summary>
/// Windows' side of the run folder's privacy (<see cref="LocalChannel.CreateRunDirectory"/>, <see cref="RedirectFiles"/>):
/// on Windows a new file or folder takes its access list from the folder it is in, so the run folder and every redirect
/// file are created with a protected access list (no inherited entries) that grants only this user, and an existing run
/// folder's list is read and checked (<see cref="WindowsPrivateFiles.IsPrivate"/>). The seam tests replace: no Windows runs
/// the tests here, so the real one (<see cref="WindowsPrivateFiles"/>) is checked on Windows by hand.
/// </summary>
public interface IWindowsSecurity
{
    /// <summary>This user's SID.</summary>
    string CurrentUser { get; }

    /// <summary>The access list of <paramref name="folder"/>, which exists.</summary>
    WindowsFolderAcl Read(string folder);

    /// <summary>Creates <paramref name="folder"/> (its parents as usual) owned by <paramref name="user"/>, its list protected and granting only them.</summary>
    void CreateFolder(string folder, string user);

    /// <summary>Replaces the access list of <paramref name="folder"/> with a protected one granting only <paramref name="user"/> (the owner stays).</summary>
    void Protect(string folder, string user);

    /// <summary>Creates the new file <paramref name="path"/> (never an existing one) for writing, its list protected and granting only <paramref name="user"/>.</summary>
    FileStream CreateFile(string path, string user);
}

/// <summary>The real <see cref="IWindowsSecurity"/>, through .NET's access control types, and the check of a folder's list.</summary>
public sealed class WindowsPrivateFiles : IWindowsSecurity
{
    /// <summary>SYSTEM and the Administrators group: they can read every file on the computer anyway.</summary>
    public static readonly IReadOnlySet<string> Trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S-1-5-18", "S-1-5-32-544" };

    /// <summary>CREATOR OWNER: an entry for whoever creates an item inside, which is this user here.</summary>
    public const string CreatorOwner = "S-1-3-0";

    /// <summary>
    /// Whether <paramref name="acl"/> keeps a folder to <paramref name="user"/>: owned by them (or by SYSTEM or the
    /// Administrators group), protected from inheritance (otherwise an inheritable entry added to a folder above it later
    /// would reach it), and every entry that allows anything is for them, SYSTEM, the Administrators group or CREATOR
    /// OWNER. Entries that deny are fine.
    /// </summary>
    public static bool IsPrivate(WindowsFolderAcl acl, string user) =>
        OwnerIsTrusted(acl, user) && acl.Protected && acl.Entries.All(e => !e.Allow || Allowed(e.Sid, user));

    /// <summary>Whether the folder's owner is <paramref name="user"/>, SYSTEM or the Administrators group (who may change its list).</summary>
    public static bool OwnerIsTrusted(WindowsFolderAcl acl, string user) =>
        acl.Owner is { } owner && (string.Equals(owner, user, StringComparison.OrdinalIgnoreCase) || Trusted.Contains(owner));

    private static bool Allowed(string sid, string user) =>
        string.Equals(sid, user, StringComparison.OrdinalIgnoreCase) || Trusted.Contains(sid) || sid == CreatorOwner;

    [SupportedOSPlatform("windows")]
    public string CurrentUser => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("This process's user has no SID.");

    [SupportedOSPlatform("windows")]
    public WindowsFolderAcl Read(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new WindowsAce(r.AccessControlType == AccessControlType.Allow, r.IdentityReference.Value))
            .ToList();
        return new WindowsFolderAcl(owner, security.AreAccessRulesProtected, entries);
    }

    [SupportedOSPlatform("windows")]
    public void CreateFolder(string folder, string user)
    {
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } parent)
            Directory.CreateDirectory(parent);
        var security = FolderOnlyFor(user);
        security.SetOwner(new SecurityIdentifier(user));
        security.CreateDirectory(folder);
    }

    [SupportedOSPlatform("windows")]
    public void Protect(string folder, string user) => new DirectoryInfo(folder).SetAccessControl(FolderOnlyFor(user));

    [SupportedOSPlatform("windows")]
    public FileStream CreateFile(string path, string user)
    {
        var sid = new SecurityIdentifier(user);
        var security = new FileSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    /// <summary>A folder list protected from inheritance that grants <paramref name="user"/> full control, inherited by what is inside.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity FolderOnlyFor(string user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(user), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
