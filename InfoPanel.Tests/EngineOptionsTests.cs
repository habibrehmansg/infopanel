using InfoPanel.Hosting;
using Microsoft.Extensions.Configuration;
using System.Xml.Linq;
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
        Assert.False(options.NoWindows);
        Assert.False(options.Fresh);
        Assert.Null(options.MockSeed);
        Assert.False(options.Mock);
        Assert.Empty(options.DevOrigins);
        Assert.Empty(options.AllowOrigins);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", options.Token);
        Assert.NotEqual(options.Token, Load([], out _)!.Token);
    }

    [Fact]
    public void ValueLessFlags_DontSwallowTheNextOption()
    {
        var dir = TempDir();
        var options = Load(["--no-browser", "--port", "5199", "--fresh", "--data-dir", dir, "--no-windows", "--token", "t3st", "--mock-seed", "7"], out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.True(options.NoBrowser);
        Assert.True(options.NoWindows);
        Assert.True(options.Fresh);
        Assert.Equal(5199, options.Port);
        Assert.Equal(Path.GetFullPath(dir), options.DataDir);
        Assert.Equal("t3st", options.Token);
        Assert.Equal(7u, options.MockSeed);
        Assert.True(options.Mock);
    }

    [Fact]
    public void V1Dir_IsAFullPath_ThatNeedNotExist_AndIsNullUnlessGiven()
    {
        var dir = TempDir();

        var options = Load(["--v1-dir", dir], out var errors);

        Assert.Empty(errors);
        Assert.Equal(Path.GetFullPath(dir), options!.V1Dir);
        Assert.False(Directory.Exists(dir));
        Assert.Null(Load([], out _)!.V1Dir);
        Assert.Contains("--v1-dir", EngineCommandLine.Usage);
    }

    [Fact]
    public void Mock_IsOnWithTheFlagOrAnySeed()
    {
        var flag = Load(["--mock"], out var errors);
        Assert.Empty(errors);
        Assert.True(flag!.Mock);
        Assert.Null(flag.MockSeed);

        Assert.True(Load(["--mock-seed", "0"], out _)!.Mock);
        Assert.False(Load(["--mock=false"], out _)!.Mock);
        Assert.True(Load(["--mock=false", "--mock-seed", "1"], out _)!.Mock);
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
    public void AllowOrigin_IsRepeatableAndNormalised()
    {
        var options = Load(["--allow-origin", "https://Client.Example", "--allow-origin=http://localhost:5183/", "--allow-origin", "https://client.example:443",
            "--allow-origin", "http://[::1]:8080"], out var errors);

        Assert.Empty(errors);
        Assert.Equal(["https://client.example", "http://localhost:5183", "http://[::1]:8080"], options!.AllowOrigins);
        Assert.Empty(options.DevOrigins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*")]
    [InlineData("https://*.client.example")]
    [InlineData("null")]
    [InlineData("client.example")]
    [InlineData("localhost:5183")]
    [InlineData("ftp://client.example")]
    [InlineData("ws://client.example")]
    [InlineData("file:///tmp")]
    [InlineData("https://client.example/path")]
    [InlineData("https://client.example/./")]
    [InlineData("https://client.example//")]
    [InlineData("https://client.example?x=1")]
    [InlineData("https://client.example#x")]
    [InlineData("https://user@client.example")]
    [InlineData("https://client\\.example")]
    [InlineData("https://clïent.example")]
    [InlineData("https://client example")]
    [InlineData("https://cl%69ent.example")]
    [InlineData("http://[fe80::1%25en0]:5183")]
    [InlineData("http://[fe80::1%en0]:5183")]
    public void AllowOrigin_MustBeAnExactOrigin(string origin)
    {
        Assert.Null(Load(["--allow-origin", origin], out var errors));
        Assert.Contains(errors, e => e.Contains("--allow-origin"));
    }

    [Theory]
    [InlineData("http://client.example")]
    [InlineData("http://client.example:5183")]
    [InlineData("http://192.168.1.10:5183")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://[2001:db8::1]:5183")]
    [InlineData("http://[::ffff:192.168.1.10]")]
    [InlineData("http://localhost.example")]
    [InlineData("http://127.0.0.1.example")]
    public void AllowOrigin_OverPlainHttp_MustBeLoopback(string origin)
    {
        Assert.Null(Load(["--allow-origin", origin], out var errors));
        var error = Assert.Single(errors);
        Assert.Contains("--allow-origin must use https unless the host is loopback", error);
    }

    [Theory]
    [InlineData("http://localhost:5183", "http://localhost:5183")]
    [InlineData("http://127.0.0.1:5183", "http://127.0.0.1:5183")]
    [InlineData("http://127.0.0.2", "http://127.0.0.2")]
    [InlineData("http://[::1]:5183", "http://[::1]:5183")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:5183", "http://[::1]:5183")]
    [InlineData("https://client.example", "https://client.example")]
    [InlineData("https://192.168.1.10:8443", "https://192.168.1.10:8443")]
    public void AllowOrigin_PlainHttpOnLoopback_OrHttps_IsAccepted(string origin, string expected)
    {
        var options = Load(["--allow-origin", origin], out var errors);
        Assert.Empty(errors);
        Assert.Equal([expected], options!.AllowOrigins);
    }

    /// <summary>IPv6 hosts are stored as browsers send them in <c>Origin</c> (the WHATWG URL serialisation).</summary>
    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]:5183", "http://[::ffff:7f00:1]:5183")]
    [InlineData("http://[::FFFF:7F00:1]:5183", "http://[::ffff:7f00:1]:5183")]
    [InlineData("https://[::192.168.1.10]", "https://[::c0a8:10a]")]
    [InlineData("https://[2001:db8:0:0:1:0:0:1]", "https://[2001:db8::1:0:0:1]")]
    [InlineData("https://[2001:0db8:0000:0001:0000:0000:0000:0001]:443", "https://[2001:db8:0:1::1]")]
    [InlineData("https://[2001:db8:1:2:3:4:5:6]", "https://[2001:db8:1:2:3:4:5:6]")]
    [InlineData("https://[1:0:2:3:4:5:6:7]", "https://[1:0:2:3:4:5:6:7]")]
    [InlineData("https://[::]", "https://[::]")]
    public void AllowOrigin_IPv6_IsSerialisedLikeABrowser(string origin, string expected)
    {
        var options = Load(["--allow-origin", origin], out var errors);
        Assert.Empty(errors);
        Assert.Equal([expected], options!.AllowOrigins);
    }

    [Fact]
    public void AllowOrigin_NeedsAValue()
    {
        Assert.Equal("--allow-origin needs a value.", EngineCommandLine.Parse(["--allow-origin"]).Error);
        Assert.Equal("--allow-origin needs a value.", EngineCommandLine.Parse(["--allow-origin", "--no-browser"]).Error);
        Assert.Contains("--allow-origin", EngineCommandLine.Usage);
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

    [Theory]
    [InlineData("--port", "abc")]
    [InlineData("--port", "65536")]
    [InlineData("--mock-seed", "-1")]
    [InlineData("--token", "has space")]
    [InlineData("--token", "a/b")]
    [InlineData("--web-root", "/definitely/not/here")]
    public void InvalidValues_AreRejected(string option, string value)
    {
        Assert.Null(Load([option, value], out var errors));
        Assert.Contains(errors, e => e.Contains(option));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--import-v1", "/a/1.x/folder")] // removed: 1.x data is imported through a client
    [InlineData("stray")]
    [InlineData("--port")]
    [InlineData("--port", "--no-browser")]
    [InlineData("--data-dir=")]
    [InlineData("--token", "a", "--token", "b")]
    [InlineData("--v1-dir")]
    [InlineData("--v1-dir", "a", "--v1-dir", "b")]
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

    [Fact]
    public void RelativePaths_ResolveFromTheWorkingDirectory()
    {
        // A test runner may start the engine from its own folder with e.g. `--data-dir .e2e-data --web-root dist`.
        // A folder under the working directory: a temp folder on another Windows drive has no relative path.
        var relative = "infopanel-options-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Environment.CurrentDirectory, relative);
        Directory.CreateDirectory(dir);
        try
        {
            var options = Load(["--data-dir", Path.Combine(relative, "data"), "--web-root", relative], out var errors);

            Assert.Empty(errors);
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "data"), options!.DataDir);
            Assert.Equal(Path.GetFullPath(dir), options.WebRoot);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void DotnetRun_StartsTheEngineInTheCallersFolder()
    {
        // The Web SDK makes `dotnet run` start in the project folder (RunWorkingDirectory), which would resolve the
        // relative paths above against InfoPanel/ instead of the caller's folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var project = XDocument.Load(Path.Combine(dir.FullName, "InfoPanel", "InfoPanel.csproj"));

        Assert.Equal("false", project.Descendants("EnableDefaultRunWorkingDirectory").Single().Value.Trim());
        Assert.Empty(project.Descendants("RunWorkingDirectory"));
    }
}
