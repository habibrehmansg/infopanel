using System.Text;
using InfoPanel.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A package's PluginInfo.ini, read by the engine when it finds the package: a regular file of at most 64 KiB, never a
// link, UTF-8 with or without a BOM; [PluginInfo]'s Name, Description, Author, Version, Website (http or https only) and
// Platforms; values cut; missing or unreadable, the folder's name and nulls.
public sealed class PluginPackageInfoTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-ppi-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    private string Folder => Path.Combine(_root, "InfoPanel.Sample");

    private PluginPackageInfo Read(byte[]? ini)
    {
        Directory.CreateDirectory(Folder);
        if (ini != null)
            File.WriteAllBytes(Path.Combine(Folder, "PluginInfo.ini"), ini);
        return PluginPackageInfo.Read(Folder, "InfoPanel.Sample", PluginLimits.Default, NullLogger.Instance);
    }

    private static byte[] Utf8(string text, bool bom = false) =>
        [.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)];

    [Fact]
    public void Its_keys_are_read_from_the_PluginInfo_section_with_or_without_a_BOM()
    {
        const string ini = """
            ; a comment
            [Other]
            Name=Not this one
            [PluginInfo]
            Name = Sample Plugin
            description=Reads things.
            Author=Someone Ünïcode
            Version=v2.1.0
            Website=https://example.com/sample
            Platforms= Windows, macOS ,LINUX,windows,
            """;

        foreach (var bom in new[] { false, true })
        {
            var info = Read(Utf8(ini, bom));

            Assert.Equal("Sample Plugin", info.Name);
            Assert.Equal("Reads things.", info.Description);
            Assert.Equal("Someone Ünïcode", info.Author);
            Assert.Equal("v2.1.0", info.Version);
            Assert.Equal("https://example.com/sample", info.Website);
            Assert.Equal(["windows", "macos", "linux"], info.Platforms);
            Assert.True(info.RunsOn("macos"));
        }
    }

    [Fact]
    public void Its_access_lines_are_every_Access_key_in_order_each_cut_empty_ones_left_out_and_at_most_eight()
    {
        var ini = new StringBuilder("[PluginInfo]\nName=Sample\nAccess=Reads usage numbers from its logs, never message text\naccess =  \n");
        ini.Append("ACCESS = ").Append(new string('a', 300)).Append('\n');
        for (var i = 3; i <= 10; i++)
            ini.Append("Access=Line ").Append(i).Append('\n');
        ini.Append("[Other]\nAccess=Not this section's\n");

        var info = Read(Utf8(ini.ToString()));

        Assert.Equal(
            ["Reads usage numbers from its logs, never message text", new string('a', 256), "Line 3", "Line 4", "Line 5", "Line 6", "Line 7", "Line 8"],
            info.Access);
        Assert.Empty(Read(Utf8("[PluginInfo]\nName=Sample\n")).Access);
    }

    [Fact]
    public void Without_a_file_the_folder_is_the_name_and_the_rest_null()
    {
        var info = Read(null);

        Assert.Equal(new PluginPackageInfo("InfoPanel.Sample", null, null, null, null, []), info);
        Assert.True(info.RunsOn("macos"));
        Assert.True(info.RunsOn("windows"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com")]
    [InlineData("https:no-host")]
    public void A_website_that_isnt_http_or_https_is_left_out(string website)
    {
        var info = Read(Utf8($"[PluginInfo]\nName=Sample\nWebsite={website}\n"));

        Assert.Null(info.Website);
        Assert.Equal("Sample", info.Name);
    }

    [Fact]
    public void Long_values_are_cut()
    {
        var info = Read(Utf8($"[PluginInfo]\nName={new string('n', 300)}\nDescription={new string('d', 5000)}\nAuthor={new string('a', 300)}\nVersion={new string('v', 300)}\nWebsite=https://example.com/{new string('w', 300)}\n"));

        Assert.Equal(256, info.Name.Length);
        Assert.Equal(4096, info.Description!.Length);
        Assert.Equal(256, info.Author!.Length);
        Assert.Equal(256, info.Version!.Length);
        // A website that doesn't fit isn't one: it isn't cut into another address.
        Assert.Null(info.Website);
    }

    [Fact]
    public void A_file_over_64_KiB_is_unreadable_and_the_defaults_apply()
    {
        var info = Read(Utf8("[PluginInfo]\nName=Sample\nPlatforms=windows\n; " + new string('x', 64 * 1024)));

        Assert.Equal(new PluginPackageInfo("InfoPanel.Sample", null, null, null, null, []), info);
    }

    [Fact]
    public void A_link_is_never_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        Directory.CreateDirectory(Folder);
        var elsewhere = Path.Combine(_root, "elsewhere.ini");
        File.WriteAllText(elsewhere, "[PluginInfo]\nName=Through a link\nPlatforms=windows\n");
        File.CreateSymbolicLink(Path.Combine(Folder, "PluginInfo.ini"), elsewhere);

        var info = PluginPackageInfo.Read(Folder, "InfoPanel.Sample", PluginLimits.Default, NullLogger.Instance);

        Assert.Equal(new PluginPackageInfo("InfoPanel.Sample", null, null, null, null, []), info);
    }

    [Theory]
    [InlineData("windows", "windows", true)]
    [InlineData("windows", "macos", false)]
    [InlineData("WINDOWS, Linux", "linux", true)]
    [InlineData("windows,linux", "macos", false)]
    [InlineData("", "macos", true)]
    [InlineData("freebsd", "macos", false)]
    public void Platforms_say_where_it_runs(string platforms, string here, bool runs)
    {
        var info = Read(Utf8($"[PluginInfo]\nPlatforms={platforms}\n"));

        Assert.Equal(runs, info.RunsOn(here));
    }

    [Fact]
    public void Find_reads_each_packages_info()
    {
        Read(Utf8("[PluginInfo]\nName=Sample\nPlatforms=linux\n"));
        File.WriteAllText(Path.Combine(Folder, "InfoPanel.Sample.dll"), "");

        var package = Assert.Single(PluginPackages.Find(_root, Path.Combine(_root, "none"), NullLogger.Instance));

        Assert.Equal("Sample", package.Info.Name);
        Assert.Equal(["linux"], package.Info.Platforms);
    }
}
