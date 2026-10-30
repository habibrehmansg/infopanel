using System.Text;
using InfoPanel.Hosting;
using InfoPanel.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// Where the engine finds plugins and what it reads from their hosts: the bundled and installed folders, the flags that
// turn plugins off or point at another bundled folder, and the bound on a host's messages.
public sealed class PluginPackagesTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-pkg-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public void Packages_are_InfoPanel_Name_folders_holding_their_dll_bundled_first_and_a_bundled_name_wins()
    {
        var bundled = Path.Combine(_root, "bundled");
        var installed = Path.Combine(_root, "installed");
        Package(bundled, "InfoPanel.Extras");
        Package(installed, "InfoPanel.extras");               // the bundled one's name, another case: skipped
        Package(installed, "InfoPanel.Spotify");
        Package(installed, "InfoPanel.My_Plugin-2");
        Package(installed, "NotAPlugin");                     // not InfoPanel.{Name}
        Directory.CreateDirectory(Path.Combine(installed, "InfoPanel.NoDll"));
        Directory.CreateDirectory(Path.Combine(installed, ".staging-0123"));
        File.WriteAllText(Path.Combine(installed, "InfoPanel.File.dll"), "");

        var packages = PluginPackages.Find(bundled, installed, NullLogger.Instance);

        Assert.Equal(["InfoPanel.Extras", "InfoPanel.My_Plugin-2", "InfoPanel.Spotify"], packages.Select(p => p.Folder).ToArray());
        Assert.Equal([true, false, false], packages.Select(p => p.Bundled).ToArray());
        Assert.Equal(Path.Combine(installed, "InfoPanel.Spotify", "InfoPanel.Spotify.dll"), packages[2].DllPath);
    }

    [Fact]
    public void An_installed_package_that_is_a_link_is_never_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Directory links need privileges on Windows.");
        var installed = Path.Combine(_root, "installed");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Package(elsewhere, "InfoPanel.Linked");
        Directory.CreateDirectory(installed);
        Directory.CreateSymbolicLink(Path.Combine(installed, "InfoPanel.Linked"), Path.Combine(elsewhere, "InfoPanel.Linked"));

        Assert.Empty(PluginPackages.Find(null, installed, NullLogger.Instance));
    }

    [Fact]
    public void A_package_that_cant_be_read_is_skipped_and_the_others_start()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        var bundled = Path.Combine(_root, "bundled");
        var installed = Path.Combine(_root, "installed");
        Package(bundled, "InfoPanel.Extras");
        Package(installed, "InfoPanel.Alpha");
        Package(installed, "InfoPanel.Locked");
        Package(installed, "InfoPanel.Zulu");
        var locked = Path.Combine(installed, "InfoPanel.Locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var packages = PluginPackages.Find(bundled, installed, NullLogger.Instance);

            Assert.Equal(["InfoPanel.Extras", "InfoPanel.Alpha", "InfoPanel.Zulu"], packages.Select(p => p.Folder).ToArray());
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_bundled_folder_that_cant_be_listed_leaves_the_installed_packages()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        var bundled = Path.Combine(_root, "bundled");
        var installed = Path.Combine(_root, "installed");
        Package(bundled, "InfoPanel.Extras");
        Package(installed, "InfoPanel.Alpha");
        File.SetUnixFileMode(bundled, UnixFileMode.None);
        try
        {
            var packages = PluginPackages.Find(bundled, installed, NullLogger.Instance);

            Assert.Equal(["InfoPanel.Alpha"], packages.Select(p => p.Folder).ToArray());
        }
        finally
        {
            File.SetUnixFileMode(bundled, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void No_plugins_folder_is_no_packages()
    {
        Assert.Empty(PluginPackages.Find(Path.Combine(_root, "none"), Path.Combine(_root, "none-either"), NullLogger.Instance));
    }

    [Fact]
    public void No_plugins_and_bundled_plugins_are_flags_and_a_bundled_folder_must_exist()
    {
        var bundled = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(bundled);

        var options = Load(["--no-plugins", "--bundled-plugins", bundled], out var errors);
        Assert.Empty(errors);
        Assert.True(options!.NoPlugins);
        Assert.Equal(Path.GetFullPath(bundled), options.BundledPlugins);

        var defaults = Load([], out _)!;
        Assert.False(defaults.NoPlugins);
        Assert.Null(defaults.BundledPlugins);

        Assert.Null(Load(["--bundled-plugins", Path.Combine(_root, "missing")], out var missing));
        Assert.Contains(missing, e => e.Contains("--bundled-plugins"));
        Assert.Contains("--no-plugins", EngineCommandLine.Usage);
        Assert.Contains("--bundled-plugins", EngineCommandLine.Usage);
    }

    [Fact]
    public void A_message_that_says_it_is_over_the_limit_fails_the_read_before_its_body()
    {
        var ok = Message(new string('a', 100));
        var big = Encoding.ASCII.GetBytes("Content-Length: 17825792\r\n\r\n{");

        Assert.Equal(ok, ReadAll(ok, limit: 1000));
        var error = Assert.Throws<IOException>(() => ReadAll([.. ok, .. big], limit: 16 * 1024 * 1024));
        Assert.Contains("17825792", error.Message);
        Assert.Throws<IOException>(() => ReadAll(Encoding.ASCII.GetBytes(new string('h', 2000) + "\r\n"), limit: 1000));
    }

    private static byte[] Message(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), .. bytes];
    }

    private static byte[] ReadAll(byte[] input, long limit)
    {
        using var stream = new MessageLimitStream(new MemoryStream(input), limit);
        var output = new MemoryStream();
        var buffer = new byte[7];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    private static EngineOptions? Load(string[] args, out IReadOnlyList<string> errors)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        return EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out errors);
    }

    private static void Package(string parent, string name)
    {
        Directory.CreateDirectory(Path.Combine(parent, name));
        File.WriteAllText(Path.Combine(parent, name, name + ".dll"), "");
    }
}
