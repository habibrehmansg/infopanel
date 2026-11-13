using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A plugin host's processor use, counted as the engine's own is (EngineSourceTests): the host reports its CPU time over the
// wall time per CPU, 100 a busy CPU, whatever the machine; the engine shows that per CPU off Windows (Activity Monitor,
// top: up to 100 × the CPUs) and as a share of the whole machine on Windows (Task Manager: 0–100).
public sealed class PluginCpuTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(0.17, 1, 17)]
    [InlineData(2.8, 1, 280)]
    [InlineData(0.5, 2, 25)]
    [InlineData(0, 1, 0)]
    public void The_host_reports_its_cpu_time_over_the_wall_time_per_cpu(double cpuSeconds, double wallSeconds, double percent)
    {
        Assert.Equal(percent, HostService.CpuPercent(TimeSpan.FromSeconds(cpuSeconds), TimeSpan.FromSeconds(wallSeconds)), 9);
    }

    [Fact]
    public void The_host_reports_no_negative_figure_and_nothing_over_no_wall_time()
    {
        Assert.Equal(0, HostService.CpuPercent(TimeSpan.FromSeconds(-0.2), Second));
        Assert.Equal(0, HostService.CpuPercent(Second, TimeSpan.Zero));
        Assert.Equal(0, HostService.CpuPercent(Second, TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(17, 14, 17)]
    [InlineData(280, 14, 280)]
    [InlineData(1_400, 14, 1_400)]
    [InlineData(2_000, 14, 1_400)]
    [InlineData(-3, 14, 0)]
    [InlineData(280, 0, 100)]
    public void Per_cpu_the_engine_shows_the_hosts_figure_within_0_to_100_times_the_cpus(double reported, int processors, double shown)
    {
        Assert.Equal(shown, PluginHostRunner.CpuShown(reported, processors, perCpu: true), 9);
    }

    [Theory]
    [InlineData(17, 14, 17d / 14)]
    [InlineData(280, 14, 20)]
    [InlineData(2_000, 14, 100)]
    [InlineData(-3, 14, 0)]
    [InlineData(50, 1, 50)]
    public void As_a_share_the_engine_shows_the_hosts_figure_over_the_cpus_within_0_to_100(double reported, int processors, double shown)
    {
        Assert.Equal(shown, PluginHostRunner.CpuShown(reported, processors, perCpu: false), 9);
    }

    [Fact]
    public void The_hosts_samples_are_timed_by_the_monotonic_clock_so_a_step_of_the_wall_clock_changes_no_figure()
    {
        var time = new SteppingTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var host = new HostService("plugin.dll", null, null, time);
        host.StartCpuBaseline(TimeSpan.Zero);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(25, host.CpuSample(TimeSpan.FromSeconds(0.5)), 9);

        // The wall clock steps back an hour (a sync, a time zone fix) while two seconds pass.
        time.Advance(TimeSpan.FromSeconds(2), wallStep: TimeSpan.FromHours(-1));
        Assert.Equal(50, host.CpuSample(TimeSpan.FromSeconds(1.5)), 9);

        // Then forward by a day.
        time.Advance(TimeSpan.FromSeconds(2), wallStep: TimeSpan.FromDays(1));
        Assert.Equal(50, host.CpuSample(TimeSpan.FromSeconds(2.5)), 9);
    }

    // Two clocks that move apart on demand: the wall clock (UtcNow) and the monotonic one (timestamps).
    private sealed class SteppingTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _wall = start;
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _wall;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan passed, TimeSpan wallStep = default)
        {
            _ticks += passed.Ticks;
            _wall += passed + wallStep;
        }
    }
}
