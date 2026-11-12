using System.Runtime.CompilerServices;
using InfoPanel.Panels.Testing;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The panel packages these tests reference (compiled against, not copied: <c>Private="false" PanelPackage="true"</c>),
/// loaded from their own build output, where each one's <c>panel.json</c> is (<see cref="PanelPackageOutput"/>), before
/// any test runs: the paths are MSBuild's own, written into this assembly by <c>PanelPackages.targets</c>.
/// </summary>
internal static class PanelPackagesUnderTest
{
    [ModuleInitializer]
    internal static void Use() => PanelPackageOutput.Use(typeof(PanelPackagesUnderTest).Assembly);
}
