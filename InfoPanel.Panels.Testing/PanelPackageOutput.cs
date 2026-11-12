using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace InfoPanel.Panels.Testing;

/// <summary>
/// A panel package's tests run its driver from the package's own build output, where its <c>panel.json</c> is, never
/// from a copy beside the test assembly: there a package's manifest would be read for the test assembly's own, and two
/// packages' manifests would overwrite each other. The test project references the package without copying it, marked
/// as a panel package (<c>&lt;ProjectReference Include="…\InfoPanel.Panels.Acme.csproj" Private="false"
/// PanelPackage="true" /&gt;</c>: built first and compiled against, its DLL and <c>panel.json</c> left where they are),
/// imports the kit's <c>PanelPackages.targets</c>, which writes the path MSBuild resolved for each such package into the
/// test assembly (<c>[AssemblyMetadata("InfoPanel.PanelPackage", path)]</c>), and once, before any test reaches a
/// driver type (a module initializer), calls <see cref="Use(Assembly)"/> with the test assembly: each package's assembly
/// then loads from its own folder, so <see cref="Sdk.PanelManifest.Read(Assembly)"/> finds its own <c>panel.json</c>.
/// </summary>
public static class PanelPackageOutput
{
    /// <summary>The key of the <see cref="AssemblyMetadataAttribute"/> that names a package's DLL.</summary>
    public const string MetadataKey = "InfoPanel.PanelPackage";

    private static readonly ConcurrentDictionary<string, string> Assemblies = new(StringComparer.OrdinalIgnoreCase);
    private static int _hooked;

    /// <summary>
    /// Loads each package <paramref name="tests"/> names (its <see cref="MetadataKey"/> metadata, written at build time)
    /// from that path whenever an assembly of its name is asked for. Never throws: a package that isn't built is a
    /// failure of the tests that reach it (<see cref="Use(string)"/>).
    /// </summary>
    public static void Use(Assembly tests)
    {
        ArgumentNullException.ThrowIfNull(tests);
        foreach (var dll in tests.GetCustomAttributes<AssemblyMetadataAttribute>().Where(a => a.Key == MetadataKey).Select(a => a.Value))
        {
            if (!string.IsNullOrEmpty(dll))
                Use(dll);
        }
    }

    /// <summary>
    /// Loads the package whose DLL is <paramref name="dll"/> (the assembly of its file's name) from there whenever it is
    /// asked for. Never throws for a file that isn't there: loading it then fails with a
    /// <see cref="FileNotFoundException"/> that says so, in the test that reached it.
    /// </summary>
    public static void Use(string dll)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dll);
        Assemblies[Path.GetFileNameWithoutExtension(dll)] = Path.GetFullPath(dll);
        if (Interlocked.Exchange(ref _hooked, 1) == 0)
            AssemblyLoadContext.Default.Resolving += Resolve;
    }

    /// <summary>The DLL registered for the package <paramref name="name"/> (its assembly's name), or null.</summary>
    public static string? Dll(string name) => Assemblies.TryGetValue(name, out var dll) ? dll : null;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name == null || !Assemblies.TryGetValue(name.Name, out var dll))
            return null;
        if (!File.Exists(dll))
            throw new FileNotFoundException($"The panel package {name.Name} isn't built at {dll}: build it (or the solution) first.", dll);
        return context.LoadFromAssemblyPath(dll);
    }
}
