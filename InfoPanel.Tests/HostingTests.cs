using System.Text.Json;
using InfoPanel.Hosting;
using Xunit;

namespace InfoPanel.Tests;

public sealed class HostingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infopanel-hosting-" + Guid.NewGuid().ToString("N"));

    public HostingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void InstanceLock_RefusesASecondHolder()
    {
        var path = Path.Combine(_dir, "engine.lock");
        using (var first = InstanceLock.TryAcquire(path, out var error))
        {
            Assert.NotNull(first);
            Assert.Null(error);

            Assert.Null(InstanceLock.TryAcquire(path, out var secondError));
            Assert.Contains("already using", secondError);
        }

        using var again = InstanceLock.TryAcquire(path, out _);
        Assert.NotNull(again);
    }

    [Fact]
    public void RuntimeFile_IsOwnerOnlyJson()
    {
        var path = Path.Combine(_dir, "runtime.json");
        RuntimeFile.Write(path, new RuntimeFile.Content(123, 5199, "http://127.0.0.1:5199"));
        RuntimeFile.Write(path, new RuntimeFile.Content(124, 5200, "http://127.0.0.1:5200"));

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(124, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(5200, json.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("http://127.0.0.1:5200", json.RootElement.GetProperty("url").GetString());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Single(Directory.GetFiles(_dir));

        RuntimeFile.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Wipe_KeepsOnlyTheLock()
    {
        var lockPath = Path.Combine(_dir, "engine.lock");
        using var held = InstanceLock.TryAcquire(lockPath, out _);
        Directory.CreateDirectory(Path.Combine(_dir, "profiles"));
        File.WriteAllText(Path.Combine(_dir, "profiles", "a.xml"), "x");
        File.WriteAllText(Path.Combine(_dir, "profiles.xml"), "x");
        File.WriteAllText(Path.Combine(_dir, $"profiles.xml.{Guid.NewGuid():N}.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        File.WriteAllText(Path.Combine(_dir, "runtime.json"), "{}");

        DataFolder.Wipe(_dir, lockPath);

        Assert.Equal([lockPath], Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Wipe_OfAV2Folder_KeepsOnlyTheLock()
    {
        // --fresh on a folder the 2.0 store wrote: its order file and temp files, profile folders, blobs.
        var lockPath = Path.Combine(_dir, "engine.lock");
        using var held = InstanceLock.TryAcquire(lockPath, out _);
        var profile = Path.Combine(_dir, "profiles", Guid.NewGuid().ToString());
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "document.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), """{"order":[]}""");
        File.WriteAllText(Path.Combine(_dir, $"profiles.json.{Guid.NewGuid():N}.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "blobs"));
        File.WriteAllText(Path.Combine(_dir, "blobs", new string('a', 64)), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        Directory.CreateDirectory(Path.Combine(_dir, "fonts"));

        Assert.Null(DataFolder.CheckWipeable(_dir));
        DataFolder.Wipe(_dir, lockPath);

        Assert.Equal([lockPath], Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Wipe_RefusesAFolderWithForeignFiles_AndDeletesNothing()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.xml"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "mine");

        Assert.Contains("notes.txt", DataFolder.CheckWipeable(_dir));
        Assert.Throws<InvalidOperationException>(() => DataFolder.Wipe(_dir, Path.Combine(_dir, "engine.lock")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "profiles.xml")));
    }

    [Fact]
    public void Wipe_MissingOrEmptyFolder_IsAllowed()
    {
        Assert.Null(DataFolder.CheckWipeable(_dir));
        Assert.Null(DataFolder.CheckWipeable(Path.Combine(_dir, "not-yet")));
    }

    [Fact]
    public void Wipe_UnlinksSymlinksInsideWithoutFollowingThem()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "profiles"));
        Directory.CreateSymbolicLink(Path.Combine(data, "assets"), outside);
        Directory.CreateSymbolicLink(Path.Combine(data, "profiles", "nested"), outside);
        File.CreateSymbolicLink(Path.Combine(data, "profiles.xml"), Path.Combine(outside, "keep.txt"));

        DataFolder.Wipe(data, Path.Combine(data, "engine.lock"));

        Assert.Empty(Directory.GetFileSystemEntries(data));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Wipe_ThroughALinkToHome_IsRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var link = Path.Combine(_dir, "link-to-home");
        Directory.CreateSymbolicLink(link, home);
        var nested = Path.Combine(_dir, "nested-link");
        Directory.CreateSymbolicLink(nested, _dir);

        try
        {
            Assert.Contains("home folder", DataFolder.CheckWipeable(link));
            Assert.Throws<InvalidOperationException>(() => DataFolder.Wipe(link, Path.Combine(link, "engine.lock")));
            // Through a linked ancestor: every hop is resolved.
            Assert.Contains("home folder", DataFolder.CheckWipeable(Path.Combine(nested, "link-to-home")));
            Assert.True(Directory.Exists(home));
        }
        finally
        {
            // Unlink before Dispose's recursive delete, whatever it does with links.
            File.Delete(link);
            File.Delete(nested);
        }
    }

    [Fact]
    public void ResolveRealPath_ResolvesLinksInAncestors()
    {
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(Path.Combine(target, "inner"));
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, "target"); // relative to the link's folder

        Assert.Equal(DataFolder.ResolveRealPath(Path.Combine(target, "inner")), DataFolder.ResolveRealPath(Path.Combine(link, "inner")));
        Assert.Equal(DataFolder.ResolveRealPath(Path.Combine(target, "inner", "missing")), DataFolder.ResolveRealPath(Path.Combine(link, "inner", "missing")));
    }

    [Fact]
    public void CreatePrivate_IsOwnerOnlyOnUnix()
    {
        var path = Path.Combine(_dir, "new", "logs");
        DataFolder.CreatePrivate(path);

        Assert.True(Directory.Exists(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public void EngineRuntime_ChecksOriginsAndTokens()
    {
        var runtime = new EngineRuntime(5199, "secret", ["http://localhost:5180"]);

        Assert.True(runtime.IsAllowedOrigin("http://127.0.0.1:5199"));
        Assert.True(runtime.IsAllowedOrigin("http://localhost:5199"));
        Assert.True(runtime.IsAllowedOrigin("http://localhost:5180"));
        Assert.False(runtime.IsAllowedOrigin("http://localhost:5181"));
        Assert.False(runtime.IsAllowedOrigin("http://[::1]:5199"));
        Assert.True(runtime.IsValidToken("secret"));
        Assert.False(runtime.IsValidToken("secre"));
        Assert.False(runtime.IsValidToken(null));
        Assert.Equal("http://127.0.0.1:5199/auth/secret", runtime.AuthUrl);
        Assert.Equal("http://localhost:5180/auth/<token>", runtime.AuthUrlFor("http://localhost:5180", redacted: true));
        Assert.False(runtime.IsCrossOrigin("http://localhost:5180"));
        Assert.Empty(runtime.CrossOrigins);
    }

    [Fact]
    public void EngineRuntime_CrossOrigins_AreAllowed_AndGetPairingLinks()
    {
        var runtime = new EngineRuntime(5313, "secret", ["http://localhost:5180"], ["https://client.example"]);

        Assert.True(runtime.IsAllowedOrigin("https://client.example"));
        Assert.True(runtime.IsAllowedOrigin("HTTPS://CLIENT.EXAMPLE"));
        Assert.True(runtime.IsCrossOrigin("https://client.example"));
        Assert.False(runtime.IsCrossOrigin("http://localhost:5180"));
        Assert.False(runtime.IsCrossOrigin("http://127.0.0.1:5313"));
        Assert.False(runtime.IsAllowedOrigin("https://client.example:8443"));
        Assert.Contains("https://client.example", runtime.AllowedOrigins);
        Assert.Equal("https://client.example/#engine=http://127.0.0.1:5313&token=secret", runtime.PairingUrlFor("https://client.example"));
        Assert.Equal("https://client.example/#engine=http://127.0.0.1:5313&token=<token>", runtime.PairingUrlFor("https://client.example", redacted: true));
    }
}
