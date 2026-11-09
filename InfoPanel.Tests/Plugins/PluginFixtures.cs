namespace InfoPanel.Tests.Plugins;

/// <summary>
/// The plugins the build copies next to the tests (<c>plugin-fixtures/</c>, InfoPanel.Tests.csproj): the test plugin,
/// whose behaviour a <c>fixture-mode.txt</c> picks. A test copies a fixture before writing a mode,
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

    /// <summary>
    /// The test plugin as another package, <paramref name="folder"/> (as long as <see cref="FixtureFolder"/>), copied into
    /// <paramref name="parent"/> with <paramref name="mode"/> as its fixture-mode.txt; returns the folder. A package's DLL
    /// is loaded by its folder's name, so the copy's assembly is renamed: its files' names, and the name's UTF-8 bytes in
    /// the DLL and its .json files, replaced by one as long (nothing in them moves).
    /// </summary>
    public static string CopyFixtureAs(string parent, string folder, params string[] mode)
    {
        if (folder.Length != FixtureFolder.Length || !folder.StartsWith("InfoPanel.", StringComparison.Ordinal))
            throw new ArgumentException($"A copy's name is InfoPanel.{{Name}}, as long as {FixtureFolder}.", nameof(folder));
        var (from, to) = (System.Text.Encoding.UTF8.GetBytes(FixtureFolder), System.Text.Encoding.UTF8.GetBytes(folder));
        var source = BuiltFolder(FixtureFolder);
        var target = Path.Combine(parent, folder);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(FixtureFolder + ".", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(target, name));
                continue;
            }
            if (name.EndsWith(".pdb", StringComparison.Ordinal))
                continue;
            var bytes = File.ReadAllBytes(file);
            for (var at = bytes.AsSpan().IndexOf(from); at >= 0;)
            {
                to.CopyTo(bytes, at);
                var next = bytes.AsSpan(at + from.Length).IndexOf(from);
                at = next < 0 ? -1 : at + from.Length + next;
            }
            File.WriteAllBytes(Path.Combine(target, folder + name[FixtureFolder.Length..]), bytes);
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
