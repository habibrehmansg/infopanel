using InfoPanel.SensorHelper;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's command line: <c>--pipe &lt;name&gt; --parent &lt;pid&gt; --parent-start &lt;start time&gt;</c>, each
/// once and nothing else; a pipe name that could be a path, a process id or start time that isn't one, or anything
/// more is refused.
/// </summary>
public sealed class HelperCommandLineTests
{
    [Fact]
    public void ThePipe_TheEnginesProcess_AndItsStartTime_AreRead()
    {
        Assert.Equal(new HelperCommandLine("InfoPanel.SensorHelper.4242", 4242, 639_000_000_000_000_000), HelperCommandLine.Parse(["--pipe", "InfoPanel.SensorHelper.4242", "--parent", "4242", "--parent-start", "639000000000000000"]));
        Assert.Equal(new HelperCommandLine("p", 7, 1), HelperCommandLine.Parse(["--parent-start", "1", "--parent", "7", "--pipe", "p"]));
    }

    [Fact]
    public void ACommandLine_ReadsBackFromItsArguments()
    {
        var line = new HelperCommandLine("InfoPanel.SensorHelper.4242", 4242, long.MaxValue);

        Assert.Equal(line, HelperCommandLine.Parse([.. line.Arguments]));
    }

    [Theory]
    [InlineData]
    [InlineData("--pipe", "p")]
    [InlineData("--parent", "7")]
    [InlineData("--pipe", "p", "--parent")]
    [InlineData("--pipe", "p", "--parent", "7")] // no start time
    [InlineData("--pipe", "../p", "--parent", "7", "--parent-start", "1")]
    [InlineData("--pipe", @"\\.\pipe\p", "--parent", "7", "--parent-start", "1")]
    [InlineData("--pipe", "p", "--parent", "0", "--parent-start", "1")]
    [InlineData("--pipe", "p", "--parent", "-1", "--parent-start", "1")]
    [InlineData("--pipe", "p", "--parent", "seven", "--parent-start", "1")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "0")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "-1")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "1.5")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "99999999999999999999")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent", "8")]
    [InlineData("--pipe", "p", "--parent-start", "1", "--parent-start", "2")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "1", "--pipe", "q")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "1", "--register")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "1", "extra")]
    [InlineData("--pipe", "p", "--parent", "7", "--register", "1")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start", "1", "--pipe")]
    [InlineData("--pipe", "p", "--parent", "7", "--parent-start")]
    public void AnythingElse_IsRefused(params string[] args) => Assert.Null(HelperCommandLine.Parse(args));
}
