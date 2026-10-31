using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The engine as <c>dotnet publish</c> lays it out, in this test run's configuration, into a new folder: everything the
/// engine's <c>InfoPanel.deps.json</c> lists is in the publish root (or at its own path under it), at the versions it
/// names, so the published engine starts (and its plugin host mode, which loads plugins with the engine's own assemblies);
/// the bundled plugin is under <c>plugins/</c> and nowhere else, with what its own deps file lists and its whole assembly
/// closure resolvable as its host loads it. Builds and tests run from <c>bin/</c>, which never shows what a publish
/// leaves out.
/// </summary>
public class EnginePublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APublish_HasTheEnginesOwnDependenciesInItsRoot_AndTheBundledPluginsOnlyUnderPlugins()
    {
        var dir = Directory.CreateTempSubdirectory("infopanel-publish-");
        try
        {
            // Every restore and build artifact goes under this test's folder, the bundled plugin's too; the real projects'
            // restore files are left as they were (a later build or publish of theirs relies on them).
            var artifacts = Path.Combine(dir.FullName, "artifacts");
            var restoreFiles = RealRestoreFiles();
            var root = Path.Combine(dir.FullName, "out");
            await Publish(root, artifacts);

            var problems = new List<string>();
            CheckIsolated(problems, artifacts, restoreFiles);
            var listed = await CheckDeps(problems, "engine", root, "InfoPanel.deps.json");

            // The bundled plugin is published under plugins/, and none of its own files land in the root; its own deps
            // file holds in its folder, and its whole closure resolves as its host loads it.
            var extras = Path.Combine(root, "plugins", "InfoPanel.Extras");
            var extrasDll = Path.Combine(extras, "InfoPanel.Extras.dll");
            if (!File.Exists(extrasDll))
                problems.Add("plugins/InfoPanel.Extras/InfoPanel.Extras.dll is missing");
            else
            {
                foreach (var file in Directory.EnumerateFiles(extras))
                {
                    var name = Path.GetFileName(file);
                    if (!listed.Contains(name) && File.Exists(Path.Combine(root, name)))
                        problems.Add($"{name}: the bundled plugin's file, not the engine's, is in the publish root");
                }
                await CheckDeps(problems, "InfoPanel.Extras", extras, "InfoPanel.Extras.deps.json");
                CheckPluginClosure(problems, root, listed, extrasDll);
            }

            // A publish that leaves the restore out restores nothing, the bundled plugin included (an offline publish
            // mustn't reach a package source): NuGet's record of the plugin's restore, which every restore that runs
            // writes, is removed and stays absent.
            var extrasRestore = Path.Combine(artifacts, "obj", "InfoPanel.Extras", "project.nuget.cache");
            if (!File.Exists(extrasRestore))
                problems.Add($"InfoPanel.Extras: no restore record at {extrasRestore} after the publish");
            foreach (var option in new[] { "--no-restore", "--no-build" })
            {
                File.Delete(extrasRestore);
                await Publish(Path.Combine(dir.FullName, "out" + option), artifacts, option);
                if (File.Exists(extrasRestore))
                    problems.Add($"InfoPanel.Extras: restored by a publish with {option}");
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
    /// The engine and the bundled plugin were restored and built under <paramref name="artifacts"/>, and the real
    /// projects' restore files are as <paramref name="before"/> found them.
    /// </summary>
    private static void CheckIsolated(List<string> problems, string artifacts, Dictionary<string, string> before)
    {
        foreach (var project in new[] { "InfoPanel", "InfoPanel.Extras" })
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
    /// The plugin's whole closure resolves as the host loads it (<c>PluginLoadContext</c>): every assembly
    /// <paramref name="pluginDll"/> references, and every assembly those reference in turn, comes from the plugin's own
    /// folder through its deps file, or else (an assembly the host shares, or one its deps file doesn't name) from the
    /// host's default context: the engine's root (its deps file's runtime assemblies, <paramref name="engineAssemblies"/>)
    /// or the shared frameworks. Each at the version referenced or later. Assemblies from the root have their own closure
    /// checked by the engine's deps file; the frameworks are whole.
    /// </summary>
    private static void CheckPluginClosure(List<string> problems, string root, HashSet<string> engineAssemblies, string pluginDll)
    {
        // What PluginLoadContext always leaves to the host.
        string[] shared = ["InfoPanel.Plugins.Graphics", "SkiaSharp"];
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
                    problems.Add($"InfoPanel.Extras: {reference.FullName} (referenced by {by}) is neither in the plugin's folder, the engine's root nor the frameworks");
                    continue;
                }
                var version = AssemblyName.GetAssemblyName(found).Version;
                if (reference.Version != null && (version == null || version < reference.Version))
                    problems.Add($"InfoPanel.Extras: {reference.FullName} (referenced by {by}) resolves to {found}, version {version}");
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
