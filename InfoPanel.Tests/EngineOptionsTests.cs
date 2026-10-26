using InfoPanel.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InfoPanel.Tests;

public class EngineOptionsTests
{
    private static EngineOptions? Load(string[] args, out IReadOnlyList<string> errors)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();
        return EngineOptions.Load(configuration, out errors);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "infopanel-options-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults()
    {
        var options = Load([], out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal(0, options.Port);
        Assert.Null(options.DataDir);
        Assert.Null(options.WebRoot);
        Assert.False(options.NoBrowser);
        Assert.False(options.Fresh);
        Assert.Null(options.MockSeed);
        Assert.Empty(options.DevOrigins);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", options.Token);
        Assert.NotEqual(options.Token, Load([], out _)!.Token);
    }

    [Fact]
    public void ValueLessFlags_DontSwallowTheNextOption()
    {
        var dir = TempDir();
        var options = Load(["--no-browser", "--port", "5199", "--fresh", "--data-dir", dir, "--token", "t3st", "--mock-seed", "7"], out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.True(options.NoBrowser);
        Assert.True(options.Fresh);
        Assert.Equal(5199, options.Port);
        Assert.Equal(Path.GetFullPath(dir), options.DataDir);
        Assert.Equal("t3st", options.Token);
        Assert.Equal(7u, options.MockSeed);
    }

    [Fact]
    public void InlineValues()
    {
        var options = Load(["--port=5299", "--no-browser=false", "--token=abc"], out var errors);

        Assert.Empty(errors);
        Assert.Equal(5299, options!.Port);
        Assert.False(options.NoBrowser);
        Assert.Equal("abc", options.Token);
    }

    [Fact]
    public void DevOrigin_IsRepeatableAndNormalised()
    {
        var options = Load(["--dev-origin", "http://localhost:5180/", "--dev-origin=HTTP://LocalHost:5181", "--dev-origin", "http://localhost:5180"], out var errors);

        Assert.Empty(errors);
        Assert.Equal(["http://localhost:5180", "http://localhost:5181"], options!.DevOrigins);
    }

    [Theory]
    [InlineData("localhost:5180")]
    [InlineData("ftp://localhost")]
    [InlineData("http://localhost:5180/path")]
    [InlineData("http://localhost:5180?x=1")]
    public void DevOrigin_MustBeAnOrigin(string origin)
    {
        Assert.Null(Load(["--dev-origin", origin], out var errors));
        Assert.Contains(errors, e => e.Contains("--dev-origin"));
    }

    [Fact]
    public void Fresh_WithoutDataDir_IsRejected()
    {
        Assert.Null(Load(["--fresh"], out var errors));
        Assert.Contains(errors, e => e.Contains("--fresh") && e.Contains("--data-dir"));
    }

    [Fact]
    public void Fresh_OnHomeOrRoot_IsRejected()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.GetPathRoot(home)!;

        Assert.Null(Load(["--fresh", "--data-dir", home], out var homeErrors));
        Assert.Contains(homeErrors, e => e.Contains("refuses"));
        Assert.Null(Load(["--fresh", "--data-dir", root], out var rootErrors));
        Assert.Contains(rootErrors, e => e.Contains("refuses"));
        Assert.Null(Load(["--fresh", "--data-dir", Path.GetDirectoryName(home)!], out var parentErrors));
        Assert.Contains(parentErrors, e => e.Contains("refuses"));
    }

    [Fact]
    public void Fresh_ThroughASymlinkToHome_IsRejected()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var link = Path.Combine(dir, "data");
        Directory.CreateSymbolicLink(link, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            Assert.Null(Load(["--fresh", "--data-dir", link], out var errors));
            Assert.Contains(errors, e => e.Contains("refuses") && e.Contains("home folder"));
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Fresh_OnAFolderWithForeignFiles_IsRejected()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        File.WriteAllText(Path.Combine(dir, "thesis.docx"), "x");
        try
        {
            Assert.Null(Load(["--fresh", "--data-dir", dir], out var errors));
            Assert.Contains(errors, e => e.Contains("refuses") && e.Contains("thesis.docx"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportV1_InsideTheDataFolder_IsRejected()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "inner"));
        try
        {
            Assert.Null(Load(["--data-dir", dir, "--import-v1", Path.Combine(dir, "inner")], out var errors));
            Assert.Contains(errors, e => e.Contains("--import-v1") && e.Contains("must not be the 1.x folder"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FreshWithAnOverlappingImport_DeletesNothingAndExits2()
    {
        var dir = TempDir();
        var inner = Path.Combine(dir, "inner");
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(dir, "profiles.xml"), "<profiles/>");
        File.WriteAllText(Path.Combine(dir, "profiles", "a.xml"), "x");
        File.WriteAllText(Path.Combine(inner, "profiles.xml"), "<profiles/>");
        try
        {
            var main = typeof(Program).Assembly.EntryPoint!;
            var result = main.Invoke(null, [new[] { "--fresh", "--data-dir", dir, "--import-v1", inner, "--no-browser" }]);
            var exit = result is Task<int> task ? await task : (int)result!;

            Assert.Equal(2, exit);
            Assert.True(File.Exists(Path.Combine(dir, "profiles.xml")));
            Assert.True(File.Exists(Path.Combine(dir, "profiles", "a.xml")));
            Assert.True(File.Exists(Path.Combine(inner, "profiles.xml")));
            Assert.False(File.Exists(Path.Combine(dir, "engine.lock")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("--port", "abc")]
    [InlineData("--port", "65536")]
    [InlineData("--mock-seed", "-1")]
    [InlineData("--token", "has space")]
    [InlineData("--token", "a/b")]
    [InlineData("--web-root", "/definitely/not/here")]
    [InlineData("--import-v1", "/definitely/not/here")]
    public void InvalidValues_AreRejected(string option, string value)
    {
        Assert.Null(Load([option, value], out var errors));
        Assert.Contains(errors, e => e.Contains(option));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("stray")]
    [InlineData("--port")]
    [InlineData("--port", "--no-browser")]
    [InlineData("--data-dir=")]
    [InlineData("--token", "a", "--token", "b")]
    [InlineData("--urls=http://0.0.0.0:80")]
    [InlineData("--Kestrel:Endpoints:Lan:Url=http://0.0.0.0:80")]
    [InlineData("--kestrel:endpoints:lan:url=http://0.0.0.0:80")]
    [InlineData("--http_ports=80")]
    [InlineData("--preferHostingUrls=true")]
    public void BadArguments_FailFast(params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);

        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void HostSettings_PassThrough()
    {
        var parsed = EngineCommandLine.Parse(["--environment=Development", "--contentRoot=/tmp", "--Engine:Token=abc", "--no-browser"]);

        Assert.Null(parsed.Error);
        Assert.Equal(["--environment=Development", "--contentRoot=/tmp", "--Engine:Token=abc"], parsed.HostArgs);
        Assert.Equal("true", parsed.Settings["Engine:NoBrowser"]);
    }

    [Fact]
    public void Help()
    {
        Assert.True(EngineCommandLine.Parse(["--help"]).ShowHelp);
    }
}
