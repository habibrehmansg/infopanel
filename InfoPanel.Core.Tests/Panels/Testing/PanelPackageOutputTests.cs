using System.Reflection;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Virtual;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// A package's tests run its driver from the package's own output (<see cref="PanelPackageOutput"/>), at the path MSBuild
/// resolved for it (written into the test assembly at build time, so a runtime-specific or artifacts layout of the tests
/// doesn't move it): the driver's assembly and its <c>panel.json</c> stay in the package's folder, and nothing of it is
/// copied beside the test assembly.
/// </summary>
public sealed class PanelPackageOutputTests
{
    [Fact]
    public void A_referenced_package_runs_from_its_own_output_with_its_own_manifest()
    {
        var dll = PanelPackageOutput.Dll("InfoPanel.Panels.Virtual");
        var assembly = typeof(VirtualPanelDriver).Assembly;

        Assert.NotNull(dll);
        Assert.Equal(dll, assembly.Location);
        Assert.Equal("virtual", PanelManifest.Read(assembly).Family("virtual").Id);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "panel.json")), "no package's panel.json beside the test assembly");
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "InfoPanel.Panels.Virtual.dll")), "no package's DLL beside the test assembly");
    }

    [Fact]
    public void The_path_is_the_one_MSBuild_resolved_written_into_the_test_assembly()
    {
        var written = typeof(PanelPackageOutputTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == PanelPackageOutput.MetadataKey).Select(a => a.Value).ToList();

        // Every package the test project references with PanelPackage="true" is written; this test checks the virtual one's.
        var virtualDll = Assert.Single(written, p => Path.GetFileName(p) == "InfoPanel.Panels.Virtual.dll");
        Assert.True(File.Exists(virtualDll), $"{virtualDll} is built");
        Assert.Equal(virtualDll, PanelPackageOutput.Dll("InfoPanel.Panels.Virtual"));
    }

    [Fact]
    public void A_package_that_isnt_built_fails_the_test_that_loads_it_not_the_registration()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "no-such-folder", "InfoPanel.Panels.NotBuilt.dll");

        PanelPackageOutput.Use(missing); // no throw: a module initializer that threw would stop every test loading

        var thrown = Assert.ThrowsAny<Exception>(() => Assembly.Load(new AssemblyName("InfoPanel.Panels.NotBuilt")));
        Assert.Contains("InfoPanel.Panels.NotBuilt isn't built at", thrown.ToString());
    }
}
