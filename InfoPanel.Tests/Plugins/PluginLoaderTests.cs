using InfoPanel.Plugins.Loader;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The loader creates every plugin type in a DLL; a type marked for other operating systems (a Windows-only library
// behind it) is skipped and listed instead, so it can't fail the DLL's other plugins.
public class PluginLoaderTests
{
    [Fact]
    public void A_type_marked_for_another_OS_is_skipped_and_listed_and_the_rest_of_the_DLL_loads()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.BuiltFolder(PluginFixtures.FixtureFolder));

        var loaded = PluginLoader.Load(dll);

        // Whatever the OS, exactly one of the two platform fixtures is for another.
        var (other, mine) = OperatingSystem.IsWindows()
            ? ("InfoPanel.TestFixture.UnixOnlyFixturePlugin", "windows-only-fixture")
            : ("InfoPanel.TestFixture.WindowsOnlyFixturePlugin", "unix-only-fixture");
        var skipped = Assert.Single(loaded.Skipped);
        Assert.Equal(other, skipped.TypeName);
        Assert.NotEmpty(skipped.Platforms);
        Assert.Equal(["test-fixture", mine], loaded.Plugins.Select(p => p.Id).Order().ToArray());
    }
}
