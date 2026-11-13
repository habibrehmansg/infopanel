using InfoPanel.Plugins.Simulator;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugin simulator's options for scripts and quick checks: --set applies a setting, --action shows an action and runs
// it only with --yes, --once runs one round and exits (0, or 1 when a plugin failed); a number a plugin can't read prints
// as "–"; arguments it can't take exit 2. Run in process on copies of the test plugins.
public sealed class SimulatorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = PluginFixtures.NewTempRoot("ip-sim-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    private string V1Dll => PluginFixtures.DllOf(PluginFixtures.Copy("InfoPanel.V1Fixture", _root));

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        using var output = new StringWriter();
        var code = await SimulatorCommand.RunAsync(args, output, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Once_runs_one_round_with_the_settings_given_and_prints_an_unreadable_number_as_a_dash()
    {
        var (code, output) = await Run(V1Dll, "v1-fixture", "--set", "label=from the simulator", "--set", "known=false", "--once");

        Assert.Equal(0, code);
        Assert.Contains("---Label: from the simulator", output);
        Assert.Contains("---Counter: 1ticks", output);
        Assert.Contains("---Maybe: –", output);
        Assert.Contains("---Unknown: –", output);
    }

    [Fact]
    public async Task An_action_is_shown_and_runs_only_with_yes()
    {
        var dll = V1Dll;

        var (shown, shownOutput) = await Run(dll, "v1-fixture", "--action", "Mark", "--once");
        Assert.Equal(0, shown);
        Assert.Contains("Mark (Mark): not run; add --yes to run it", shownOutput);
        Assert.Contains("---Marks: 0", shownOutput);

        var (ran, ranOutput) = await Run(dll, "v1-fixture", "--action", "Mark", "--yes", "--once");
        Assert.Equal(0, ran);
        Assert.Contains("Mark (Mark): done", ranOutput);
        Assert.Contains("---Marks: 1", ranOutput);
    }

    [Fact]
    public async Task Once_exits_1_when_an_action_fails()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.CopyFixture(_root));

        var (code, output) = await Run(dll, "test-fixture", "--action", "ReportError", "--yes", "--once");

        Assert.Equal(1, code);
        Assert.Contains("ReportError (Report an error): failed: fixture: an error on request", output);
    }

    [Theory]
    [InlineData("--set", "nothing=1", "No setting 'nothing'")]
    [InlineData("--set", "label", "--set takes key=value")]
    [InlineData("--set", "known=maybe", "'known' takes true or false")]
    [InlineData("--action", "Nothing", "No action 'Nothing'")]
    [InlineData("--frobnicate", "1", "Unknown option --frobnicate")]
    public async Task Arguments_it_cant_take_exit_2_saying_why(string option, string value, string why)
    {
        var (code, output) = await Run(V1Dll, "v1-fixture", option, value, "--once");

        Assert.Equal(2, code);
        Assert.Contains(why, output);
    }

    // A value the engine would refuse (outside the setting's declared range, a number that isn't finite) is refused here
    // too, before the plugin is given any setting.
    [Theory]
    [InlineData("count=-1", "'count' takes a value from 0 to 100, not '-1'")]
    [InlineData("count=101", "'count' takes a value from 0 to 100, not '101'")]
    [InlineData("ratio=NaN", "'ratio' takes a finite number, not 'NaN'")]
    [InlineData("ratio=Infinity", "'ratio' takes a finite number, not 'Infinity'")]
    [InlineData("ratio=1.5", "'ratio' takes a value from 0 to 1, not '1.5'")]
    public async Task A_value_outside_its_settings_range_or_not_finite_exits_2_and_no_setting_is_applied(string setting, string why)
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.CopyFixture(_root));

        var (code, output) = await Run(dll, "test-fixture", "--set", "label=applied", "--set", setting, "--once");

        Assert.Equal(2, code);
        Assert.Contains(why, output);
        Assert.DoesNotContain("Setting label", output);
    }

    [Fact]
    public async Task Once_exits_1_when_a_plugins_settings_cant_be_read()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.CopyFixture(_root, "throw-list-settings"));

        var (code, output) = await Run(dll, "test-fixture", "--set", "label=x", "--once");

        Assert.Equal(1, code);
        Assert.Contains("fixture: can't list settings", output);
    }

    [Fact]
    public async Task Once_exits_1_when_an_entry_cant_be_read_and_prints_the_rest()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.CopyFixture(_root, "throw-read"));

        var (code, output) = await Run(dll, "test-fixture", "--once");

        Assert.Equal(1, code);
        Assert.Contains("---Broken: failed: fixture: can't read the value", output);
        Assert.Contains("---Counter: 1ticks", output);
        Assert.Contains("---Ephemeral: 7V", output);
    }

    [Fact]
    public async Task Once_exits_1_when_a_plugin_fails_to_close()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.CopyFixture(_root, "throw-close"));

        var (code, output) = await Run(dll, "test-fixture", "--once");

        Assert.Equal(1, code);
        Assert.Contains("failed to close: fixture: can't close", output);
        Assert.Contains("---Counter: 1ticks", output);
    }
}
