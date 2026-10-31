using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The web client origin a build carries (<c>InfoPanel/WebClientOrigin.targets</c>, imported by the engine's project):
/// the MSBuild property <c>InfoPanelAllowOrigin</c> becomes the assembly metadata <c>InfoPanel.AllowOrigin</c>, which the
/// engine reads as the default for <c>--allow-origin</c>; without it the build carries nothing. Read from the engine's
/// own project (<c>InfoPanel/InfoPanel.csproj</c>: the assembly attributes its build compiles into the engine, the target
/// <c>GetAssemblyAttributes</c>), so a project that stops importing the targets fails here, and from the engine these
/// tests run, built without the property.
/// </summary>
public sealed class WebClientOriginTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheProperty_IsCompiledIntoTheEngine_AsAssemblyMetadata()
    {
        var (recorded, generated) = await Recorded("https://client.example");

        Assert.Equal(["https://client.example"], recorded);
        // The attributes listed are the ones the build writes into the engine's assembly.
        Assert.Equal("true", generated);
    }

    [Fact]
    public async Task WithoutTheProperty_NothingIsRecorded()
    {
        Assert.Empty((await Recorded(null)).Recorded);
    }

    [Fact]
    public void TheEngineTheseTestsRun_BuiltWithoutTheProperty_CarriesNoOrigin()
    {
        var metadata = typeof(EngineOptions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();

        Assert.DoesNotContain(metadata, a => a.Key == "InfoPanel.AllowOrigin");
        Assert.Contains(metadata, a => a.Key == "InfoPanel.Build");
        Assert.Null(EngineOptions.BuiltInAllowOrigin);
    }

    /// <summary>
    /// The engine project's assembly attributes with <paramref name="origin"/> as <c>InfoPanelAllowOrigin</c> (none when
    /// null): the <c>InfoPanel.AllowOrigin</c> values, and the project's <c>GenerateAssemblyInfo</c>. Nothing is built.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Recorded, string? Generated)> Recorded(string? origin)
    {
        var project = Path.Combine(LiveEngine.RepoRoot(), "InfoPanel", "InfoPanel.csproj");
        var start = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(project)!,
        };
        foreach (var arg in new[] { "msbuild", project, "-t:GetAssemblyAttributes", "-getItem:AssemblyAttribute", "-getProperty:GenerateAssemblyInfo",
                     "-nologo", "-nodeReuse:false" })
            start.ArgumentList.Add(arg);
        if (origin != null)
            start.ArgumentList.Add($"-p:InfoPanelAllowOrigin={origin}");
        // The test run's own MSBuild settings stay out of the evaluation.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);

        using var build = Process.Start(start)!;
        var output = build.StandardOutput.ReadToEndAsync(Ct);
        var errors = build.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await build.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            build.Kill(entireProcessTree: true);
            await build.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(build.ExitCode == 0, $"msbuild exited {build.ExitCode}: {await output}{await errors}");

        using var result = JsonDocument.Parse(await output);
        var generated = result.RootElement.GetProperty("Properties").GetProperty("GenerateAssemblyInfo").GetString();
        if (!result.RootElement.GetProperty("Items").TryGetProperty("AssemblyAttribute", out var attributes))
            return ([], generated);
        return ([.. attributes.EnumerateArray()
            .Where(a => a.GetProperty("Identity").GetString() == typeof(AssemblyMetadataAttribute).FullName
                && a.GetProperty("_Parameter1").GetString() == "InfoPanel.AllowOrigin")
            .Select(a => a.GetProperty("_Parameter2").GetString()!)], generated);
    }

    /// <summary>The dotnet host running these tests.</summary>
    private static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
            return host;
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet"));
    }
}
