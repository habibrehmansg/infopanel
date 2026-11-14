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

    /// <summary>PLUGINS.md has a package write one Access line a thing it reads or changes: the same key more than once.</summary>
    [Fact]
    public void A_PluginInfo_ini_with_several_Access_lines_is_read_and_the_engine_keeps_every_line()
    {
        var root = PluginFixtures.NewTempRoot("ip-dup-");
        try
        {
            File.WriteAllText(Path.Combine(root, "PluginInfo.ini"), "[PluginInfo]\nName=Sample\nAccess=Reads its logs\nAccess=Changes its settings\n");

            var info = PluginLoader.GetPluginInfo(root);

            Assert.Equal("Sample", info?.Name);
            Assert.Equal(
                ["Reads its logs", "Changes its settings"],
                InfoPanel.Plugins.PluginPackageInfo.Read(root, "Sample", InfoPanel.Plugins.PluginLimits.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Access);
        }
        finally
        {
            PluginFixtures.DeleteQuietly(root);
        }
    }

    /// <summary>
    /// A package built with a plain project reference to the SDK, as PLUGINS.md's step 2 shows: its output folder holds
    /// its own copy of InfoPanel.Plugins.dll, which its deps.json lists. The plugin still implements the host's
    /// <see cref="InfoPanel.Plugins.IPlugin"/>, so its plugins load.
    /// </summary>
    [Fact]
    public void A_package_carrying_its_own_copy_of_the_SDK_loads_its_plugins()
    {
        var root = PluginFixtures.NewTempRoot("ip-sdk-");
        try
        {
            var package = PluginFixtures.CopyFixture(root);
            File.Copy(typeof(InfoPanel.Plugins.IPlugin).Assembly.Location, Path.Combine(package, "InfoPanel.Plugins.dll"));
            // What a plain <ProjectReference> writes: the SDK as a dependency, with its DLL as a runtime asset.
            File.WriteAllText(Path.Combine(package, "InfoPanel.TestFixture.deps.json"), """
                {
                  "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0", "signature": "" },
                  "compilationOptions": {},
                  "targets": {
                    ".NETCoreApp,Version=v8.0": {
                      "InfoPanel.TestFixture/1.0.0": {
                        "dependencies": { "InfoPanel.Plugins": "1.0.0" },
                        "runtime": { "InfoPanel.TestFixture.dll": {} }
                      },
                      "InfoPanel.Plugins/1.0.0": {
                        "runtime": { "InfoPanel.Plugins.dll": {} }
                      }
                    }
                  },
                  "libraries": {
                    "InfoPanel.TestFixture/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
                    "InfoPanel.Plugins/1.0.0": { "type": "project", "serviceable": false, "sha512": "" }
                  }
                }
                """);

            var loaded = PluginLoader.Load(PluginFixtures.DllOf(package));

            Assert.Contains("test-fixture", loaded.Plugins.Select(p => p.Id));
        }
        finally
        {
            PluginFixtures.DeleteQuietly(root);
        }
    }
}
