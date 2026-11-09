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
/// no bundled plugin is published (there is no <c>plugins/</c> folder). Builds and tests run from <c>bin/</c>, which never
/// shows what a publish leaves out.
/// </summary>
public class EnginePublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APublish_HasTheEnginesOwnDependenciesInItsRoot_AndNoBundledPlugins()
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-");
        try
        {
            // Every restore and build artifact goes under this test's folder; the real projects' restore files are left as
            // they were (a later build or publish of theirs relies on them).
            var artifacts = Path.Combine(dir.FullName, "artifacts");
            var restoreFiles = RealRestoreFiles();
            var root = Path.Combine(dir.FullName, "out");
            await Publish(root, artifacts);

            var problems = new List<string>();
            CheckIsolated(problems, artifacts, restoreFiles);
            await CheckDeps(problems, "engine", root, "InfoPanel.deps.json");
            // The sensor helper is published for Windows runtimes only (LibreHardwareMonitor's assemblies are Windows ones).
            if (Directory.Exists(Path.Combine(root, "sensor-helper")))
                problems.Add("sensor-helper/: the sensor helper's folder, in a publish for no runtime");
            foreach (var helperFile in new[] { "InfoPanel.SensorHelper.dll", "InfoPanel.SensorHelper.deps.json", "LibreHardwareMonitorLib.dll" })
            {
                if (File.Exists(Path.Combine(root, helperFile)))
                    problems.Add($"{helperFile}: the sensor helper's, in a publish for no runtime");
            }

            // No package is bundled: the publish has no plugins folder, whatever the options.
            if (Directory.Exists(Path.Combine(root, "plugins")))
                problems.Add("plugins/: a bundled plugins folder, in a publish that bundles none");
            foreach (var option in new[] { "--no-restore", "--no-build" })
            {
                var optionRoot = Path.Combine(dir.FullName, "out" + option);
                await Publish(optionRoot, artifacts, option);
                if (Directory.Exists(Path.Combine(optionRoot, "plugins")))
                    problems.Add($"plugins/: a bundled plugins folder, in a publish with {option}");
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
        foreach (var project in new[] { "InfoPanel" })
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
