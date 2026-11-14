using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using InfoPanel.Sensors.Libre;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The engine as <c>dotnet publish</c> lays it out, in this test run's configuration, into a new folder: everything the
/// engine's <c>InfoPanel.deps.json</c> lists is in the publish root (or at its own path under it), at the versions it
/// names, so the published engine starts (and its plugin host mode, which loads plugins with the engine's own assemblies);
/// the bundled plugins (AI usage) are under <c>plugins/</c> and nowhere else, each with what its own deps file lists and
/// its whole assembly closure resolvable as its host loads it. Builds and tests run from <c>bin/</c>, which never shows
/// what a publish leaves out.
/// </summary>
public class EnginePublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The plugins the engine bundles (InfoPanel.csproj's BundledPlugin list), by folder.</summary>
    private static readonly string[] BundledPlugins = ["InfoPanel.AiUsage"];

    [Fact]
    public async Task APublish_HasTheEnginesOwnDependenciesInItsRoot_AndTheBundledPluginsOnlyUnderPlugins()
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-");
        try
        {
            // Every restore and build artifact goes under this test's folder, the bundled plugins' too; the real projects'
            // restore files are left as they were (a later build or publish of theirs relies on them).
            var artifacts = Path.Combine(dir.FullName, "artifacts");
            var restoreFiles = RealRestoreFiles();
            var root = Path.Combine(dir.FullName, "out");
            await Publish(root, artifacts);

            var problems = new List<string>();
            CheckIsolated(problems, artifacts, restoreFiles);
            var listed = await CheckDeps(problems, "engine", root, "InfoPanel.deps.json");
            // The engine's own files stay in the root, whatever a plugin carries (a bundled plugin's file of the same name
            // once displaced the engine's Serilog.dll from it).
            foreach (var own in new[] { "InfoPanel.dll", "InfoPanel.Plugins.dll", "Serilog.dll" })
            {
                if (!File.Exists(Path.Combine(root, own)))
                    problems.Add($"{own}: the engine's own, missing from the publish root");
            }
            // A desktop app, not a server: the concurrent workstation garbage collector (the Web SDK's default is the server one).
            using (var config = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "InfoPanel.runtimeconfig.json"), Ct)))
            {
                // An absent key means the workstation collector (the default).
                if (config.RootElement.GetProperty("runtimeOptions").TryGetProperty("configProperties", out var configProperties)
                    && configProperties.TryGetProperty("System.GC.Server", out var serverGc)
                    && serverGc.ValueKind == JsonValueKind.True)
                    problems.Add("InfoPanel.runtimeconfig.json: System.GC.Server is true, the server garbage collector");
            }
            // The sensor helper is published for Windows runtimes only (LibreHardwareMonitor's assemblies are Windows ones).
            if (Directory.Exists(Path.Combine(root, "sensor-helper")))
                problems.Add("sensor-helper/: the sensor helper's folder, in a publish for no runtime");
            foreach (var helperFile in new[] { "InfoPanel.SensorHelper.dll", "InfoPanel.SensorHelper.deps.json", "LibreHardwareMonitorLib.dll" })
            {
                if (File.Exists(Path.Combine(root, helperFile)))
                    problems.Add($"{helperFile}: the sensor helper's, in a publish for no runtime");
            }

            // Each bundled plugin is published under plugins/, and none of its own files land in the root; its own deps file
            // holds in its folder, and its whole closure resolves as its host loads it. It carries no SDK of its own (the
            // host hands every plugin the engine's).
            foreach (var plugin in BundledPlugins)
            {
                var folder = Path.Combine(root, "plugins", plugin);
                var dll = Path.Combine(folder, plugin + ".dll");
                if (!File.Exists(dll))
                {
                    problems.Add($"plugins/{plugin}/{plugin}.dll is missing");
                    continue;
                }
                if (!File.Exists(Path.Combine(folder, "PluginInfo.ini")))
                    problems.Add($"plugins/{plugin}/PluginInfo.ini is missing");
                if (File.Exists(Path.Combine(folder, "InfoPanel.Plugins.dll")))
                    problems.Add($"plugins/{plugin}/InfoPanel.Plugins.dll: a copy of the SDK beside the plugin");
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    if (!listed.Contains(name) && File.Exists(Path.Combine(root, name)))
                        problems.Add($"{name}: the bundled plugin's file, not the engine's, is in the publish root");
                }
                await CheckDeps(problems, plugin, folder, plugin + ".deps.json");
                CheckPluginClosure(problems, plugin, root, listed, dll);
            }

            // A publish that leaves the restore out restores nothing, the bundled plugins included (an offline publish
            // mustn't reach a package source): NuGet's record of a plugin's restore, which every restore that runs writes,
            // is removed and stays absent. Either way the plugins are published.
            foreach (var plugin in BundledPlugins)
            {
                if (!File.Exists(RestoreRecord(artifacts, plugin)))
                    problems.Add($"{plugin}: no restore record at {RestoreRecord(artifacts, plugin)} after the publish");
            }
            foreach (var option in new[] { "--no-restore", "--no-build" })
            {
                foreach (var plugin in BundledPlugins)
                    File.Delete(RestoreRecord(artifacts, plugin));
                var optionRoot = Path.Combine(dir.FullName, "out" + option);
                await Publish(optionRoot, artifacts, option);
                foreach (var plugin in BundledPlugins)
                {
                    if (File.Exists(RestoreRecord(artifacts, plugin)))
                        problems.Add($"{plugin}: restored by a publish with {option}");
                    if (!File.Exists(Path.Combine(optionRoot, "plugins", plugin, plugin + ".dll")))
                        problems.Add($"plugins/{plugin}/{plugin}.dll is missing from a publish with {option}");
                }
            }

            Assert.True(problems.Count == 0,
                $"The published engine in {root} doesn't match its deps files:\n" + string.Join("\n", problems));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A bundled plugin that carries a file of the same name as one of the engine's own (a test-only package, made here,
    /// whose dependency is an assembly named <c>Serilog</c> at version 99) has it published in its own folder, and the
    /// engine's file stays in the publish root as the engine's deps file names it: the SDK's conflict resolution, which
    /// goes by file name whatever the folder, never weighs a plugin's files against the engine's.
    /// </summary>
    [Fact]
    public async Task APublish_KeepsTheEnginesOwnFiles_WhenABundledPluginCarriesOneOfTheSameName()
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-collide-");
        try
        {
            var collide = WriteCollidingPlugin(dir.FullName);
            var root = Path.Combine(dir.FullName, "out");
            await Publish(root, Path.Combine(dir.FullName, "artifacts"), $"-p:CustomAfterMicrosoftCommonTargets={collide}");

            var problems = new List<string>();
            await CheckDeps(problems, "engine", root, "InfoPanel.deps.json");
            var own = Path.Combine(root, "Serilog.dll");
            if (!File.Exists(own))
                problems.Add("Serilog.dll: the engine's own, missing from the publish root");
            else if (AssemblyName.GetAssemblyName(own).Version is { Major: 99 })
                problems.Add("Serilog.dll in the publish root is the bundled plugin's (version 99), not the engine's");
            var folder = Path.Combine(root, "plugins", "InfoPanel.TestCollide");
            if (!File.Exists(Path.Combine(folder, "InfoPanel.TestCollide.dll")))
                problems.Add("plugins/InfoPanel.TestCollide/InfoPanel.TestCollide.dll is missing");
            var theirs = Path.Combine(folder, "Serilog.dll");
            if (!File.Exists(theirs) || AssemblyName.GetAssemblyName(theirs).Version is not { Major: 99 })
                problems.Add("plugins/InfoPanel.TestCollide/Serilog.dll isn't the plugin's own (version 99)");
            if (File.Exists(Path.Combine(root, "InfoPanel.TestCollide.dll")))
                problems.Add("InfoPanel.TestCollide.dll: the bundled plugin's, in the publish root");
            Assert.True(problems.Count == 0, $"The publish in {root} with a plugin carrying a Serilog.dll of its own:\n" + string.Join("\n", problems));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A test-only bundled plugin in <paramref name="dir"/>: <c>InfoPanel.TestCollide</c>, which depends on an assembly
    /// named <c>Serilog</c> at version 99 (so its output carries a <c>Serilog.dll</c> newer than the engine's); answers a
    /// targets file that adds it to the engine's BundledPlugin list, for <c>CustomAfterMicrosoftCommonTargets</c>.
    /// </summary>
    private static string WriteCollidingPlugin(string dir)
    {
        var serilog = Path.Combine(dir, "collide-serilog");
        Directory.CreateDirectory(serilog);
        File.WriteAllText(Path.Combine(serilog, "Serilog.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Serilog</AssemblyName>
                <AssemblyVersion>99.0.0.0</AssemblyVersion>
                <FileVersion>99.0.0.0</FileVersion>
                <Version>99.0.0</Version>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(serilog, "Collide.cs"), "namespace Serilog.Collide { public static class Marker { public const int Value = 99; } }\n");

        var plugin = Path.Combine(dir, "collide-plugin");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "InfoPanel.TestCollide.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDynamicLoading>true</EnableDynamicLoading>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../collide-serilog/Serilog.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), "namespace InfoPanel.TestCollide { public static class Uses { public const int Value = Serilog.Collide.Marker.Value; } }\n");

        var targets = Path.Combine(dir, "collide.targets");
        File.WriteAllText(targets, $"""
            <Project>
              <ItemGroup Condition="'$(MSBuildProjectName)' == 'InfoPanel'">
                <BundledPlugin Include="{Path.Combine(plugin, "InfoPanel.TestCollide.csproj")}" />
              </ItemGroup>
            </Project>
            """);
        return targets;
    }

    /// <summary>
    /// A publish for Windows (<c>-r win-x64</c>, framework-dependent) carries the sensor helper in its own folder,
    /// <c>sensor-helper/</c> beside the engine, where the Libre source starts it (<see cref="LibreHelperProcess.DefaultPath"/>):
    /// <c>InfoPanel.SensorHelper.exe</c> with its own deps and runtimeconfig files, everything its deps file lists in that
    /// folder, LibreHardwareMonitor among them, and none of its files in the engine's root (in a self-contained publish the
    /// helper's apphost would take the engine's <c>hostfxr.dll</c> for its .NET). (A publish for no runtime or another
    /// carries none: the test above.)
    /// </summary>
    [Fact]
    public async Task AWindowsPublish_CarriesTheSensorHelperInItsOwnFolder()
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-win-");
        try
        {
            var artifacts = Path.Combine(dir.FullName, "artifacts");
            var root = Path.Combine(dir.FullName, "win-x64");
            await Publish(root, artifacts, "-r", "win-x64", "--self-contained", "false");

            var folder = Path.Combine(root, "sensor-helper");
            var problems = new List<string>();
            if (!File.Exists(Path.Combine(root, "InfoPanel.exe")))
                problems.Add("InfoPanel.exe is missing from the Windows publish");
            foreach (var file in new[] { "InfoPanel.SensorHelper.exe", "InfoPanel.SensorHelper.dll", "InfoPanel.SensorHelper.runtimeconfig.json", "LibreHardwareMonitorLib.dll" })
            {
                if (!File.Exists(Path.Combine(folder, file)))
                    problems.Add($"sensor-helper/{file} is missing from the Windows publish");
                if (File.Exists(Path.Combine(root, file)))
                    problems.Add($"{file}: the sensor helper's, in the engine's root");
            }
            // The engine starts the helper from where the publish puts it.
            if (LibreHelperProcess.DefaultPath != Path.Combine(AppContext.BaseDirectory, "sensor-helper", OperatingSystem.IsWindows() ? "InfoPanel.SensorHelper.exe" : "InfoPanel.SensorHelper"))
                problems.Add($"LibreHelperProcess.DefaultPath isn't in sensor-helper/: {LibreHelperProcess.DefaultPath}");
            await CheckDeps(problems, "engine", root, "InfoPanel.deps.json");
            var helper = await CheckDeps(problems, "sensor helper", folder, "InfoPanel.SensorHelper.deps.json");
            if (!helper.Contains("LibreHardwareMonitorLib.dll"))
                problems.Add("the sensor helper's deps file doesn't list LibreHardwareMonitorLib.dll");
            // The helper is published through the test's own intermediate folder, never into the project's.
            if (Directory.Exists(Path.Combine(LiveEngine.RepoRoot(), "InfoPanel", "sensor-helper")))
                problems.Add("the sensor helper was published into InfoPanel/sensor-helper/ in the checkout");
            if (!Directory.EnumerateFiles(artifacts, "InfoPanel.SensorHelper.dll", SearchOption.AllDirectories).Any())
                problems.Add($"the sensor helper wasn't built under the test's folder {artifacts}");
            Assert.True(problems.Count == 0, $"The Windows publish in {root}:\n" + string.Join("\n", problems));

            // The helper runs on the runtime the engine needs anyway, rolling forward from its own.
            using var config = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, "InfoPanel.SensorHelper.runtimeconfig.json"), Ct));
            Assert.Equal("Major", config.RootElement.GetProperty("runtimeOptions").GetProperty("rollForward").GetString());
            // Startup hooks off: DOTNET_STARTUP_HOOKS loads nothing into the helper, its task's or an elevated one.
            Assert.False(config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").GetProperty("System.StartupHookProvider.IsSupported").GetBoolean());
            // A Windows-subsystem program (IMAGE_SUBSYSTEM_WINDOWS_GUI, 2): a task's launch shows no console window.
            Assert.Equal(2, Subsystem(Path.Combine(folder, "InfoPanel.SensorHelper.exe")));

        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AWindowsPublish_OnWindows_GivesInfoPanelExeTheProgramsIcon()
    {
        // The SDK writes the icon into the apphost only when it is built on Windows, so this runs on Windows alone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only a publish on Windows writes resources into InfoPanel.exe.");
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-icon-");
        try
        {
            var root = Path.Combine(dir.FullName, "win-x64");
            await Publish(root, Path.Combine(dir.FullName, "artifacts"), "-r", "win-x64", "--self-contained", "false");

            // packaging/windows/InfoPanel.ico's sizes, as the icon group in InfoPanel.exe lists them (256 is written as 0).
            Assert.Equal([16, 20, 24, 32, 40, 48, 64, 256], IconGroupSizes(Path.Combine(root, "InfoPanel.exe")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The image sizes of the first icon group (<c>RT_GROUP_ICON</c>) in the executable at <paramref name="path"/>; none when it has none.</summary>
    private static int[] IconGroupSizes(string path)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var module = NativeResources.LoadLibraryExW(path, IntPtr.Zero, NativeResources.AsDataFileExclusive | NativeResources.AsImageResource);
        Assert.True(module != IntPtr.Zero, $"{path} couldn't be loaded for its resources: error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        try
        {
            // The first group's name, taken while enumerating (an id, or a string that lives only as long as the module).
            var resource = IntPtr.Zero;
            NativeResources.EnumResourceNamesW(module, NativeResources.GroupIcon, (m, type, name, _) =>
            {
                resource = NativeResources.FindResourceW(m, name, type);
                return false;
            }, IntPtr.Zero);
            if (resource == IntPtr.Zero)
                return [];
            var size = NativeResources.SizeofResource(module, resource);
            var data = NativeResources.LockResource(NativeResources.LoadResource(module, resource));
            var bytes = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, (int)size);
            // GRPICONDIR: reserved, type 1, count; then one 14-byte GRPICONDIRENTRY each, its width first.
            var count = BitConverter.ToUInt16(bytes, 4);
            return Enumerable.Range(0, count).Select(i => bytes[6 + 14 * i] is 0 ? 256 : bytes[6 + 14 * i]).ToArray();
        }
        finally
        {
            NativeResources.FreeLibrary(module);
        }
    }

    private static class NativeResources
    {
        public const uint AsDataFileExclusive = 0x40; // LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE
        public const uint AsImageResource = 0x20; // LOAD_LIBRARY_AS_IMAGE_RESOURCE
        public static readonly IntPtr GroupIcon = 14; // RT_GROUP_ICON

        public delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        public static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumNames callback, IntPtr param);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        public static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        public static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [System.Runtime.InteropServices.DllImport("kernel32")]
        public static extern IntPtr LockResource(IntPtr data);

        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr module);
    }

    /// <summary>The PE subsystem of the executable at <paramref name="path"/>: its optional header's <c>Subsystem</c>.</summary>
    private static int Subsystem(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pe = BitConverter.ToInt32(bytes, 0x3C);
        Assert.Equal("PE\0\0", System.Text.Encoding.ASCII.GetString(bytes, pe, 4));
        return BitConverter.ToUInt16(bytes, pe + 4 + 20 + 68); // signature, file header, then Subsystem at 68 in the optional header
    }

    /// <summary>
    /// The engine was restored and built under <paramref name="artifacts"/>, and the real projects' restore files are as
    /// <paramref name="before"/> found them.
    /// </summary>
    private static void CheckIsolated(List<string> problems, string artifacts, Dictionary<string, string> before)
    {
        foreach (var project in new[] { "InfoPanel" }.Concat(BundledPlugins))
        {
            if (!File.Exists(Path.Combine(artifacts, "obj", project, "project.assets.json")))
                problems.Add($"{project}: not restored under the test's folder {artifacts}");
            if (!Directory.Exists(Path.Combine(artifacts, "bin", project)))
                problems.Add($"{project}: not built under the test's folder {artifacts}");
        }

        var after = RealRestoreFiles();
        foreach (var (file, state) in before)
        {
            if (!after.TryGetValue(file, out var now))
                problems.Add($"{file}: removed by the publish");
            else if (now != state)
                problems.Add($"{file}: rewritten by the publish");
        }
        foreach (var file in after.Keys.Except(before.Keys))
            problems.Add($"{file}: written by the publish");
    }

    /// <summary>
    /// The real projects' restore files (<c>obj/project.assets.json</c> and NuGet's files beside it), each with its
    /// length, time and hash.
    /// </summary>
    private static Dictionary<string, string> RealRestoreFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(LiveEngine.RepoRoot()))
        {
            var obj = Path.Combine(folder, "obj");
            if (!Directory.EnumerateFiles(folder, "*.csproj").Any() || !Directory.Exists(obj))
                continue;
            foreach (var file in Directory.EnumerateFiles(obj))
            {
                var name = Path.GetFileName(file);
                if (name is not ("project.assets.json" or "project.nuget.cache") && !name.EndsWith(".nuget.dgspec.json", StringComparison.Ordinal)
                    && !name.EndsWith(".nuget.g.props", StringComparison.Ordinal) && !name.EndsWith(".nuget.g.targets", StringComparison.Ordinal))
                    continue;
                var info = new FileInfo(file);
                files[file] = $"{info.Length} {info.LastWriteTimeUtc:O} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))}";
            }
        }
        return files;
    }

    /// <summary>
    /// Everything <paramref name="depsFile"/> in <paramref name="folder"/> lists is there: runtime assemblies in the folder,
    /// satellites in their culture's folder, RID-specific assets at their own paths (runtimes/{rid}/…). Answers the
    /// runtime assemblies' file names.
    /// </summary>
    private static async Task<HashSet<string>> CheckDeps(List<string> problems, string what, string folder, string depsFile)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(folder, depsFile);
        if (!File.Exists(path))
        {
            problems.Add($"{what}: {path} is missing");
            return listed;
        }
        using var deps = JsonDocument.Parse(await File.ReadAllBytesAsync(path, Ct));
        var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
        {
            var label = $"{what}: {library.Name}";
            if (library.Value.TryGetProperty("runtime", out var runtime))
            {
                foreach (var asset in runtime.EnumerateObject())
                {
                    var name = Path.GetFileName(asset.Name);
                    listed.Add(name);
                    Check(problems, label, Path.Combine(folder, name), asset.Value);
                }
            }
            if (library.Value.TryGetProperty("resources", out var resources))
            {
                foreach (var asset in resources.EnumerateObject())
                {
                    var locale = asset.Value.GetProperty("locale").GetString()!;
                    Check(problems, label, Path.Combine(folder, locale, Path.GetFileName(asset.Name)), asset.Value);
                }
            }
            if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
            {
                foreach (var asset in runtimeTargets.EnumerateObject())
                    Check(problems, label, Path.Combine(folder, asset.Name.Replace('/', Path.DirectorySeparatorChar)), asset.Value);
            }
        }
        return listed;
    }

    /// <summary>NuGet's record of <paramref name="project"/>'s restore under the test's folder.</summary>
    private static string RestoreRecord(string artifacts, string project) => Path.Combine(artifacts, "obj", project, "project.nuget.cache");

    /// <summary>
    /// The plugin's whole closure resolves as the host loads it (<c>PluginLoadContext</c>): every assembly
    /// <paramref name="pluginDll"/> references, and every assembly those reference in turn, comes from the plugin's own
    /// folder through its deps file, or else (an assembly the host shares, or one its deps file doesn't name) from the
    /// host's default context: the engine's root (its deps file's runtime assemblies, <paramref name="engineAssemblies"/>)
    /// or the shared frameworks. Each at the version referenced or later. Assemblies from the root have their own closure
    /// checked by the engine's deps file; the frameworks are whole.
    /// </summary>
    private static void CheckPluginClosure(List<string> problems, string plugin, string root, HashSet<string> engineAssemblies, string pluginDll)
    {
        // What PluginLoadContext always leaves to the host.
        string[] shared = ["InfoPanel.Plugins", "InfoPanel.Plugins.Graphics", "SkiaSharp"];
        // The engine runs on Microsoft.NETCore.App and Microsoft.AspNetCore.App, as this test process does.
        string[] frameworks =
        [
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)!,
        ];
        var resolver = new System.Runtime.Loader.AssemblyDependencyResolver(pluginDll);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([pluginDll]);
        while (queue.TryDequeue(out var path))
        {
            foreach (var reference in References(path))
            {
                if (!seen.Add(reference.FullName))
                    continue;
                var name = reference.Name!;
                var fromPlugin = shared.Contains(name, StringComparer.OrdinalIgnoreCase) ? null : resolver.ResolveAssemblyToPath(reference);
                var found = fromPlugin
                    ?? (engineAssemblies.Contains(name + ".dll") ? Path.Combine(root, name + ".dll") : null)
                    ?? frameworks.Select(f => Path.Combine(f, name + ".dll")).FirstOrDefault(File.Exists);
                var by = Path.GetFileName(path);
                if (found == null || !File.Exists(found))
                {
                    problems.Add($"{plugin}: {reference.FullName} (referenced by {by}) is neither in the plugin's folder, the engine's root nor the frameworks");
                    continue;
                }
                var version = AssemblyName.GetAssemblyName(found).Version;
                if (reference.Version != null && (version == null || version < reference.Version))
                    problems.Add($"{plugin}: {reference.FullName} (referenced by {by}) resolves to {found}, version {version}");
                if (fromPlugin != null)
                    queue.Enqueue(fromPlugin);
            }
        }
    }

    /// <summary>The assemblies <paramref name="path"/> references.</summary>
    private static List<AssemblyName> References(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return [.. metadata.AssemblyReferences.Select(h => metadata.GetAssemblyReference(h).GetAssemblyName())];
    }

    /// <summary>
    /// The file a deps entry names is there, at the assembly and file version the entry gives (when it gives them; RID
    /// assets of other platforms, which a portable publish still carries, are checked for presence only).
    /// </summary>
    private static void Check(List<string> problems, string library, string path, JsonElement asset)
    {
        if (!File.Exists(path))
        {
            problems.Add($"{library}: {path} is missing");
            return;
        }
        if (asset.TryGetProperty("assemblyVersion", out var assemblyVersion) && assemblyVersion.GetString() is { } expected)
        {
            string actual;
            try
            {
                actual = AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "none";
            }
            catch (BadImageFormatException)
            {
                actual = "not an assembly";
            }
            if (actual != expected)
                problems.Add($"{library}: {path} is assembly version {actual}, the deps file names {expected}");
        }
        if (asset.TryGetProperty("fileVersion", out var fileVersion) && fileVersion.GetString() is { } expectedFile
            && !asset.TryGetProperty("rid", out _))
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var actual = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            if (actual != expectedFile)
                problems.Add($"{library}: {path} is file version {actual}, the deps file names {expectedFile}");
        }
    }

    /// <summary>
    /// Publishes the engine project, as a user would, in this test run's configuration into <paramref name="output"/>,
    /// with every restore and build artifact under <paramref name="artifacts"/>. The packages are already in NuGet's
    /// global folder (this test run was restored), and the audit is off, so the restore needs no network.
    /// </summary>
    private static async Task Publish(string output, string artifacts, params string[] options)
    {
        var project = Path.Combine(LiveEngine.RepoRoot(), "InfoPanel", "InfoPanel.csproj");
        var configuration = typeof(EnginePublishTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var start = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
        foreach (var arg in new[] { "publish", project, "-c", configuration, "-o", output, "--artifacts-path", artifacts, "-p:NuGetAudit=false",
                     "--nologo", "--disable-build-servers" }.Concat(options))
            start.ArgumentList.Add(arg);
        // The test run's own MSBuild settings stay out of the publish.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);

        using var publish = Process.Start(start)!;
        var stdout = publish.StandardOutput.ReadToEndAsync(Ct);
        var stderr = publish.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await publish.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            publish.Kill(entireProcessTree: true);
            await publish.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(publish.ExitCode == 0, $"dotnet publish exited {publish.ExitCode}: {await stdout}{await stderr}");
    }

    /// <summary>The dotnet host running these tests.</summary>
    private static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
            return host;
        // The shared runtime is {dotnet root}/shared/Microsoft.NETCore.App/{version}/.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnet = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", dotnet));
    }
}
