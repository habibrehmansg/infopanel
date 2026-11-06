using System.Text;
using InfoPanel.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// plugin-state.json (docs/2.0/data-folder.md): only the user's "off", by folder name ignoring case; read tolerantly, its
// unknown members (the 1.4 upgrade's v1Migration included) kept as written through a rewrite, and a file the engine can't
// hold whole never rewritten.
public sealed class PluginStateFileTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-pst-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    private string Path => System.IO.Path.Combine(_root, "plugin-state.json");

    private PluginStateFile Read() => PluginStateFile.Read(Path, NullLogger.Instance);

    private void Write(string text) => File.WriteAllText(Path, text, new UTF8Encoding(false));

    private string Text() => File.ReadAllText(Path, Encoding.UTF8);

    [Fact]
    public void Without_a_file_every_package_is_on_and_turning_one_off_writes_it()
    {
        var state = Read();

        Assert.Null(state.Problem);
        Assert.False(state.IsOff("InfoPanel.A"));
        Assert.False(File.Exists(Path));

        state.Set("InfoPanel.A", on: false);

        Assert.True(state.IsOff("InfoPanel.A"));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""", Text());
        Assert.True(Read().IsOff("InfoPanel.A"));
    }

    [Fact]
    public void Unknown_members_and_v1Migration_are_kept_as_written_through_a_rewrite()
    {
        Write("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"],"v1Migration":{"completed":"2026-11-01T09:30:00Z"},"later":[1,{"x":"é"}],"format2":null}""");
        var state = Read();

        state.Set("InfoPanel.B", on: false);
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A","InfoPanel.B"],"v1Migration":{"completed":"2026-11-01T09:30:00Z"},"later":[1,{"x":"é"}],"format2":null}""", Text());

        state.Set("InfoPanel.A", on: true);
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.B"],"v1Migration":{"completed":"2026-11-01T09:30:00Z"},"later":[1,{"x":"é"}],"format2":null}""", Text());
    }

    [Fact]
    public void Folder_names_match_ignoring_case_and_a_name_with_no_package_is_kept()
    {
        Write("""{"format":"infopanel.plugin-state/1","off":["infopanel.a","InfoPanel.Gone"]}""");
        var state = Read();

        Assert.True(state.IsOff("InfoPanel.A"));
        Assert.True(state.IsOff("INFOPANEL.GONE"));

        // Off again in another case: nothing to write.
        state.Set("INFOPANEL.A", on: false);
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["infopanel.a","InfoPanel.Gone"]}""", Text());

        state.Set("InfoPanel.A", on: true);
        Assert.False(state.IsOff("infopanel.A"));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.Gone"]}""", Text());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{ not json", "not JSON")]
    [InlineData("[]", "not an object")]
    [InlineData("""{"off":["InfoPanel.A"]}""", "no format")]
    [InlineData("""{"format":"infopanel.plugin-state/2","off":["InfoPanel.A"]}""", "another major version")]
    [InlineData("""{"format":"infopanel.plugin-state/1","off":"InfoPanel.A"}""", "off not an array")]
    [InlineData("""{"format":"infopanel.plugin-state/1","off":[1]}""", "a name not a string")]
    [InlineData("""{"format":7,"off":[]}""", "format not a string")]
    public void A_file_the_engine_cant_hold_leaves_every_package_on_and_is_never_rewritten(string text, string why)
    {
        Write(text);
        var state = Read();

        Assert.True(state.Problem != null, why);
        Assert.False(state.IsOff("InfoPanel.A"));
        Assert.Throws<PluginStateUnavailableException>(() => state.Set("InfoPanel.A", on: false));
        Assert.Throws<PluginStateUnavailableException>(() => state.Set("InfoPanel.A", on: true));
        Assert.Equal(text, Text());
    }

    [Fact]
    public void A_file_over_1_MiB_is_left_as_it_is()
    {
        var text = "{\"format\":\"infopanel.plugin-state/1\",\"off\":[\"InfoPanel.A\"],\"pad\":\"" + new string('x', 1024 * 1024) + "\"}";
        Write(text);
        var state = Read();

        Assert.NotNull(state.Problem);
        Assert.False(state.IsOff("InfoPanel.A"));
        Assert.Throws<PluginStateUnavailableException>(() => state.Set("InfoPanel.B", on: false));
        Assert.Equal(text, Text());
    }

    [Fact]
    public void A_file_nested_deeper_than_the_engine_reads_is_left_as_it_is()
    {
        var text = """{"format":"infopanel.plugin-state/1","off":[],"deep":""" + new string('[', 300) + new string(']', 300) + "}";
        Write(text);
        var state = Read();

        Assert.NotNull(state.Problem);
        Assert.Throws<PluginStateUnavailableException>(() => state.Set("InfoPanel.A", on: false));
        Assert.Equal(text, Text());
    }

    [Fact]
    public void A_link_is_never_read_or_written_through()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        var target = System.IO.Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(target, """{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""");
        File.CreateSymbolicLink(Path, target);
        var state = Read();

        Assert.NotNull(state.Problem);
        Assert.False(state.IsOff("InfoPanel.A"));
        Assert.Throws<PluginStateUnavailableException>(() => state.Set("InfoPanel.B", on: false));
        Assert.NotNull(new FileInfo(Path).LinkTarget);
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""", File.ReadAllText(target));
    }

    [Fact]
    public void A_write_that_fails_changes_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A read-only folder is a Unix test.");
        var folder = System.IO.Path.Combine(_root, "locked");
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "plugin-state.json");
        var state = PluginStateFile.Read(path, NullLogger.Instance);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => state.Set("InfoPanel.A", on: false));
            Assert.False(state.IsOff("InfoPanel.A"));
            Assert.Null(state.Problem);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(File.Exists(path));
    }
}
