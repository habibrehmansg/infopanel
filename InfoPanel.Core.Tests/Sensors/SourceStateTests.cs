using InfoPanel.Sensors;
using InfoPanel.Sensors.MacOS;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// A sensor source's state (<c>GET /api/sensors/sources</c>) as the polled base reports it: starting until its first
/// discovery is done, then what its sampler last said after each round, running when it says nothing (the macOS
/// source's), unavailable when discovery failed; and the hub's list of every source's state in its order.
/// </summary>
public class SourceStateTests
{
    private static readonly SourceReason Why = new("why", "The sampler says why");

    private sealed class Sampler : ISensorSampler
    {
        public SourceState? Said;
        public bool ThrowOnDiscover;
        public bool ThrowOnState;
        public int StateReads;

        public IReadOnlyList<SensorDefinition> Discover() =>
            ThrowOnDiscover ? throw new InvalidOperationException("no device") : [new SensorDefinition("a", "G", "A", "")];

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
        {
            foreach (ref var s in samples)
                s.Set(1);
        }

        public SourceState? State
        {
            get
            {
                StateReads++;
                return ThrowOnState ? throw new InvalidOperationException("state") : Said;
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void APolledSource_IsStarting_UntilDiscovered_ThenRunning_WhenItsSamplerSaysNothing()
    {
        var sampler = new Sampler();
        using var source = new PolledSensorSource("test.source", () => sampler);
        Assert.Equal(SourceState.Starting, source.State);

        source.Open();
        Assert.Equal(SourceState.Running, source.State);
        source.Poll();
        Assert.Equal(SourceState.Running, source.State);
    }

    [Fact]
    public void APolledSource_ReportsWhatItsSamplerLastSaid_AfterEachRound()
    {
        var sampler = new Sampler { Said = SourceState.UnavailableBecause(Why) };
        using var source = new PolledSensorSource("test.source", () => sampler);
        source.Open();
        Assert.Equal(new SourceState(SourceStateKind.Unavailable, Why), source.State);

        sampler.Said = null;
        Assert.Equal(SourceStateKind.Unavailable, source.State.Kind);
        source.Poll();
        Assert.Equal(SourceState.Running, source.State);

        sampler.Said = SourceState.OffBecause(Why);
        source.Poll();
        Assert.Equal(SourceStateKind.Off, source.State.Kind);
        Assert.Equal("why", source.State.Reason?.Code);
        Assert.Equal("The sampler says why", source.State.Reason?.Message);
    }

    [Fact]
    public void APolledSource_WhoseDiscoveryFailed_IsUnavailable()
    {
        using var source = new PolledSensorSource("test.source", () => new Sampler { ThrowOnDiscover = true });
        source.Open();
        Assert.Equal(SourceState.Unavailable, source.State);
        Assert.Null(source.State.Reason);
    }

    [Fact]
    public void APolledSource_KeepsItsLastState_WhenItsSamplerFailsToSay()
    {
        var sampler = new Sampler { Said = SourceState.UnavailableBecause(Why) };
        using var source = new PolledSensorSource("test.source", () => sampler);
        source.Open();
        sampler.ThrowOnState = true;
        source.Poll();
        Assert.Equal(SourceState.UnavailableBecause(Why), source.State);
    }

    [Fact]
    public void TheMacOSSampler_SaysNothing_SoTheMacOSSourceIsRunning()
    {
        using var sampler = new MacSensorSampler();
        Assert.Null(((ISensorSampler)sampler).State);
    }

    [Fact]
    public void AReasonHasACodeAndAMessage()
    {
        Assert.Throws<ArgumentException>(() => new SourceReason("", "message"));
        Assert.Throws<ArgumentException>(() => new SourceReason("code", ""));
    }

    [Fact]
    public void TheHub_ListsEverySourcesState_InItsOrder_ARunningOneWhenTheSourceSaysNothing()
    {
        var sampler = new Sampler { Said = SourceState.UnavailableBecause(Why) };
        using var polled = new PolledSensorSource("test.polled", () => sampler);
        polled.Open();
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock, polled]);

        var states = hub.SourceStates();
        Assert.Equal(["infopanel.mock", "test.polled"], states.Select(s => s.Id));
        Assert.Equal(SourceState.Running, states[0].State);
        Assert.Equal(mock.GetSensors().Count, states[0].Sensors);
        Assert.Equal(SourceState.UnavailableBecause(Why), states[1].State);
        Assert.Equal(1, states[1].Sensors);
    }
}
