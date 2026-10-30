namespace InfoPanel.Tests.Plugins;

/// <summary>
/// The plugins the build copies next to the tests (<c>plugin-fixtures/</c>, InfoPanel.Tests.csproj): the test plugin,
/// whose behaviour a <c>fixture-mode.txt</c> picks, and the bundled Extras. A test copies a fixture before writing a mode,
/// so the built copy stays as built.
/// </summary>
internal static class PluginFixtures
{
    public const string FixtureFolder = "InfoPanel.TestFixture";
    public const string FixturePluginId = "test-fixture";

    public static string BuiltFolder(string folder) => Path.Combine(AppContext.BaseDirectory, "plugin-fixtures", folder);

    /// <summary>The built engine, which runs a plugin host when started with <c>--plugin-host</c>.</summary>
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel");

    /// <summary>
    /// Copies the test plugin into <paramref name="parent"/>/InfoPanel.TestFixture with <paramref name="mode"/> as its
    /// fixture-mode.txt (none when empty) and returns the folder.
    /// </summary>
    public static string CopyFixture(string parent, params string[] mode) => Copy(FixtureFolder, parent, mode);

    public static string Copy(string folder, string parent, params string[] mode)
    {
        var source = BuiltFolder(folder);
        var target = Path.Combine(parent, folder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        if (mode.Length > 0)
            File.WriteAllLines(Path.Combine(target, "fixture-mode.txt"), mode);
        return target;
    }

    public static string DllOf(string packageFolder) => Path.Combine(packageFolder, Path.GetFileName(packageFolder) + ".dll");

    /// <summary>A fresh folder under the temp folder, short enough for a Unix socket path next to it.</summary>
    public static string NewTempRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }

    public static void DeleteQuietly(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
