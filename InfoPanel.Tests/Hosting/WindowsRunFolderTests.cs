using System.Diagnostics;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The run folder and the redirect files on Windows, where a new file takes its access list from its folder: through the
/// Windows seam (<see cref="IWindowsSecurity"/>), replaced here by a fake that keeps each folder's list, so every OS runs
/// them. The folder is created owned by this user with a protected list granting only them, each file likewise; an
/// existing folder others may read, or whose list is inherited (so a later change above it would reach it), is made
/// private when it is this user's, and refused otherwise or when that doesn't take, nothing written and nothing opened. The real seam (<see cref="WindowsPrivateFiles"/>) runs only on Windows, which no runner here has.
/// </summary>
public sealed class WindowsRunFolderTests : IDisposable
{
    private const string User = "S-1-5-21-1000-2000-3000-1001";
    private const string OtherUser = "S-1-5-21-1000-2000-3000-1002";
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";

    private readonly string _run = Path.Combine(Path.GetTempPath(), "ipw-" + Guid.NewGuid().ToString("N")[..8], "run");
    private readonly FakeWindowsSecurity _windows = new(User);
    private readonly RedirectFiles _redirects;

    public WindowsRunFolderTests()
    {
        _redirects = new RedirectFiles(_run, "0123456789abcdef", new ManualTime(DateTimeOffset.UnixEpoch), NullLogger<RedirectFiles>.Instance, _windows);
    }

    public void Dispose()
    {
        _redirects.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_run)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ANewRunFolder_AndEachRedirectFile_AreCreatedForThisUserOnly()
    {
        var file = _redirects.Write("http://127.0.0.1:5300/auth/secret");

        AssertOnlyFor(User, _windows.Folders[Path.GetFullPath(_run)]);
        Assert.Equal(User, _windows.Files[file]);
        Assert.Equal(RedirectFiles.Html("http://127.0.0.1:5300/auth/secret"), File.ReadAllText(file));
        Assert.Empty(_windows.Protected);
    }

    [Fact]
    public void AnExistingRunFolderOthersCanRead_IsMadePrivate_BeforeAnythingIsWritten()
    {
        _windows.Existing(_run, new WindowsFolderAcl(User, false, [new(true, User), new(true, "S-1-5-18"), new(true, Users), new(false, Everyone)]));

        var file = _redirects.Write("http://127.0.0.1:5300/auth/secret");

        Assert.Equal([Path.GetFullPath(_run)], _windows.Protected);
        Assert.True(WindowsPrivateFiles.IsPrivate(_windows.Folders[Path.GetFullPath(_run)], User));
        Assert.Equal(User, _windows.Files[file]);
    }

    [Fact]
    public void AnExistingRunFolderWithAnInheritedList_IsProtected_EvenWhenOnlyThisUserAndTheSystemGetIn()
    {
        // %LOCALAPPDATA%'s usual list, inherited: this user, SYSTEM and the Administrators group. Not protected, so an
        // inheritable entry added to a folder above it later would reach it.
        _windows.Existing(_run, new WindowsFolderAcl(User, false, [new(true, User), new(true, "S-1-5-18"), new(true, "S-1-5-32-544")]));

        var file = _redirects.Write("http://127.0.0.1:5300/auth/secret");

        Assert.Equal([Path.GetFullPath(_run)], _windows.Protected);
        AssertOnlyFor(User, _windows.Folders[Path.GetFullPath(_run)]);
        Assert.Equal(User, _windows.Files[file]);
    }

    [Fact]
    public void AnExistingProtectedPrivateRunFolder_IsLeftAsItIs()
    {
        _windows.Existing(_run, new WindowsFolderAcl(User, true, [new(true, User), new(true, "S-1-5-18"), new(true, "S-1-5-32-544")]));

        _redirects.Write("http://127.0.0.1:5300/auth/secret");

        Assert.Empty(_windows.Protected);
    }

    public static TheoryData<WindowsFolderAcl, ProtectOutcome> Refused => new()
    {
        // Another user's folder is never taken over.
        { new WindowsFolderAcl(OtherUser, true, [new(true, OtherUser), new(true, User)]), ProtectOutcome.Works },
        // Another user's, its list inherited: not taken over either.
        { new WindowsFolderAcl(OtherUser, false, [new(true, User)]), ProtectOutcome.Works },
        // This user's, but making it private didn't take.
        { new WindowsFolderAcl(User, false, [new(true, User), new(true, Everyone)]), ProtectOutcome.Ignored },
        // This user's and only theirs, but its list stays inherited.
        { new WindowsFolderAcl(User, false, [new(true, User), new(true, "S-1-5-18")]), ProtectOutcome.Ignored },
        // This user's, and changing its list fails.
        { new WindowsFolderAcl(User, false, [new(true, User), new(true, "S-1-5-18")]), ProtectOutcome.Throws },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void ARunFolderThatCantBeMadePrivate_IsRefused_NothingWritten_AndNothingOpens(WindowsFolderAcl acl, ProtectOutcome protect)
    {
        _windows.Existing(_run, acl);
        _windows.ProtectDoes = protect;
        var started = new List<ProcessStartInfo>();
        var launcher = new BrowserLauncher(NullLogger<BrowserLauncher>.Instance, _redirects, command => { started.Add(command); return 0; }, () => null,
            BrowserOS.Windows, () => null);

        Assert.Throws<LocalChannelException>(() => _redirects.Write("http://127.0.0.1:5300/auth/secret"));
        launcher.Open(new EditorLink("http://127.0.0.1:5300/auth/secret", WebClient: false));

        Assert.Empty(started);
        Assert.Empty(_windows.Files);
        Assert.Empty(Directory.GetFiles(_run));
    }

    [Fact]
    public void TheChannelsRunFolder_IsCreatedForThisUserOnly_Too()
    {
        LocalChannel.CreateRunDirectory(_run, windows: _windows);

        AssertOnlyFor(User, _windows.Folders[Path.GetFullPath(_run)]);
    }

    [Theory]
    [InlineData(User, new[] { User }, true)]
    [InlineData(User, new[] { User, "S-1-5-18", "S-1-5-32-544", "S-1-3-0" }, true)]
    [InlineData("S-1-5-32-544", new[] { User, "S-1-5-18" }, true)]
    [InlineData(User, new[] { User, Users }, false)]
    [InlineData(User, new[] { User, Everyone }, false)]
    [InlineData(User, new[] { User, "S-1-5-11" }, false)] // Authenticated Users
    [InlineData(OtherUser, new[] { User }, false)]
    [InlineData(null, new[] { User }, false)]
    public void AFolderIsPrivate_WhenOnlyThisUserAndTheSystemMayGetIn(string? owner, string[] allowed, bool expected)
    {
        var acl = new WindowsFolderAcl(owner, true, [.. allowed.Select(s => new WindowsAce(true, s)), new(false, Everyone)]);

        Assert.Equal(expected, WindowsPrivateFiles.IsPrivate(acl, User));
    }

    [Fact]
    public void AFolderWhoseListIsInherited_IsNotPrivate_WhoeverItLetsIn()
    {
        Assert.False(WindowsPrivateFiles.IsPrivate(new WindowsFolderAcl(User, false, [new(true, User)]), User));
        Assert.True(WindowsPrivateFiles.IsPrivate(new WindowsFolderAcl(User, true, [new(true, User)]), User));
    }

    /// <summary>What the fake does when asked to protect a folder's list.</summary>
    public enum ProtectOutcome
    {
        /// <summary>The list becomes protected and grants only the user.</summary>
        Works,

        /// <summary>The call returns, but the list is unchanged.</summary>
        Ignored,

        /// <summary>The call fails (access denied).</summary>
        Throws,
    }

    private static void AssertOnlyFor(string user, WindowsFolderAcl acl)
    {
        Assert.Equal(user, acl.Owner);
        Assert.True(acl.Protected);
        Assert.Equal([new WindowsAce(true, user)], acl.Entries);
    }

    /// <summary>The Windows seam over the real file system, each folder's access list kept here.</summary>
    private sealed class FakeWindowsSecurity(string user) : IWindowsSecurity
    {
        public Dictionary<string, WindowsFolderAcl> Folders { get; } = new(StringComparer.Ordinal);

        /// <summary>Each file created through the seam, and whom its list grants.</summary>
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Protected { get; } = [];

        public ProtectOutcome ProtectDoes { get; set; }

        public string CurrentUser => user;

        public void Existing(string folder, WindowsFolderAcl acl)
        {
            Directory.CreateDirectory(folder);
            Folders[Path.GetFullPath(folder)] = acl;
        }

        public WindowsFolderAcl Read(string folder) => Folders[Path.GetFullPath(folder)];

        public void CreateFolder(string folder, string owner)
        {
            Directory.CreateDirectory(folder);
            Folders[Path.GetFullPath(folder)] = new WindowsFolderAcl(owner, true, [new(true, owner)]);
        }

        public void Protect(string folder, string owner)
        {
            Protected.Add(Path.GetFullPath(folder));
            if (ProtectDoes == ProtectOutcome.Throws)
                throw new UnauthorizedAccessException("Access to the path is denied.");
            if (ProtectDoes == ProtectOutcome.Works)
                Folders[Path.GetFullPath(folder)] = Folders[Path.GetFullPath(folder)] with { Protected = true, Entries = [new(true, owner)] };
        }

        public FileStream CreateFile(string path, string owner)
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Files[path] = owner;
            return stream;
        }
    }
}
