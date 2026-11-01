using InfoPanel.Sensors.Network;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// When the public address is looked up: no request while no public-address reading is in use (read within 2 min);
/// one when it comes into use; one when the network changes while in use; otherwise every 30 min; after failures a
/// back-off doubling from 30 s up to 30 min, reset by an answer.
/// </summary>
public sealed class PublicIpScheduleTests
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly PublicIpSchedule _schedule;

    public PublicIpScheduleTests() => _schedule = new PublicIpSchedule(_time);

    private long Now => _time.GetTimestamp();

    private void Advance(TimeSpan by) => _time.Advance(by);

    /// <summary>Moves on <paramref name="by"/> a minute at a time, reading a public address each minute (a shown profile).</summary>
    private void InUseFor(TimeSpan by)
    {
        for (var left = by; left > TimeSpan.Zero; left -= TimeSpan.FromMinutes(1))
        {
            Advance(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1));
            _schedule.NoteUse(Now);
        }
    }

    /// <summary>Takes the lookup that is due now, as the task does, and says how it went.</summary>
    private void LookUp(bool answered = true)
    {
        Assert.True(_schedule.TakeDue(Now));
        _schedule.LookedUp(Now, answered);
    }

    [Fact]
    public void NoRequestWhileUnused_EvenForADay_AndNothingToWaitFor()
    {
        for (var i = 0; i < 48; i++)
        {
            Advance(TimeSpan.FromMinutes(30));
            Assert.False(_schedule.TakeDue(Now));
            Assert.Null(_schedule.WaitFrom(Now));
        }

        Assert.False(_schedule.InUse(Now));
    }

    [Fact]
    public void ComingIntoUse_AsksOnce_AtOnce()
    {
        Assert.True(_schedule.NoteUse(Now));
        Assert.Equal(TimeSpan.Zero, _schedule.WaitFrom(Now));
        LookUp();

        Assert.False(_schedule.NoteUse(Now));
        Assert.False(_schedule.TakeDue(Now));
        InUseFor(TimeSpan.FromMinutes(10));
        Assert.False(_schedule.TakeDue(Now));
    }

    [Fact]
    public void InUse_Every30Minutes_NotBefore()
    {
        _schedule.NoteUse(Now);
        LookUp();
        Assert.Equal(TimeSpan.FromMinutes(30), _schedule.WaitFrom(Now));

        InUseFor(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.False(_schedule.TakeDue(Now));
        Assert.Equal(TimeSpan.FromSeconds(1), _schedule.WaitFrom(Now));
        InUseFor(TimeSpan.FromSeconds(1));
        LookUp();

        InUseFor(TimeSpan.FromMinutes(30));
        LookUp();
    }

    [Fact]
    public void NoRequestOnceUnused_ThoughThe30MinutesPass_UntilItComesIntoUseAgain()
    {
        _schedule.NoteUse(Now);
        LookUp();

        // The last read 2 min and a moment ago: no longer in use.
        Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1));
        Assert.False(_schedule.InUse(Now));
        Advance(TimeSpan.FromHours(3));
        Assert.False(_schedule.TakeDue(Now));
        Assert.Null(_schedule.WaitFrom(Now));

        Assert.True(_schedule.NoteUse(Now));
        LookUp();
    }

    [Fact]
    public void ReadsWithin2Minutes_KeepItInUse_WithoutAskingAgain()
    {
        _schedule.NoteUse(Now);
        LookUp();

        Advance(TimeSpan.FromMinutes(2));
        Assert.True(_schedule.InUse(Now));
        Assert.False(_schedule.NoteUse(Now));
        Assert.False(_schedule.TakeDue(Now));
    }

    [Fact]
    public void ARequestOnANetworkChange_WhileInUse()
    {
        _schedule.NoteUse(Now);
        LookUp();
        InUseFor(TimeSpan.FromMinutes(5));

        Assert.True(_schedule.NetworkChanged(Now));
        LookUp();

        // And the 30 minutes count from that lookup.
        InUseFor(TimeSpan.FromMinutes(29));
        Assert.False(_schedule.TakeDue(Now));
        InUseFor(TimeSpan.FromMinutes(1));
        LookUp();
    }

    [Fact]
    public void ANetworkChange_WhileUnused_AsksNothing()
    {
        Assert.False(_schedule.NetworkChanged(Now));
        Assert.False(_schedule.TakeDue(Now));

        _schedule.NoteUse(Now);
        LookUp();
        Advance(TimeSpan.FromMinutes(3));
        Assert.False(_schedule.NetworkChanged(Now));
        Assert.False(_schedule.TakeDue(Now));
    }

    [Fact]
    public void ANetworkChange_DuringABackOff_IsServedWhenTheBackOffEnds_NeverEarlier()
    {
        _schedule.NoteUse(Now);
        LookUp(answered: false);
        Assert.False(_schedule.TakeDue(Now));

        InUseFor(TimeSpan.FromSeconds(5));
        Assert.True(_schedule.NetworkChanged(Now));
        Assert.False(_schedule.TakeDue(Now));
        Assert.Equal(TimeSpan.FromSeconds(25), _schedule.WaitFrom(Now));
        InUseFor(TimeSpan.FromSeconds(24));
        Assert.False(_schedule.TakeDue(Now));
        InUseFor(TimeSpan.FromSeconds(1));
        LookUp();

        // Served by that lookup: nothing more is due.
        Assert.False(_schedule.TakeDue(Now));
    }

    [Fact]
    public void AFlappingNetwork_AtMaximumBackOff_AsksOnlyAsTheBackOffEnds()
    {
        _schedule.NoteUse(Now);
        while (_schedule.Backoff < PublicIpSchedule.MaxBackoff)
        {
            InUseFor(_schedule.WaitFrom(Now) ?? TimeSpan.Zero);
            LookUp(answered: false);
        }

        // A change every 5 s for the whole back-off: one lookup, at its end.
        var asked = 0;
        for (var t = TimeSpan.Zero; t < PublicIpSchedule.MaxBackoff; t += TimeSpan.FromSeconds(5))
        {
            Advance(TimeSpan.FromSeconds(5));
            _schedule.NoteUse(Now);
            _schedule.NetworkChanged(Now);
            if (_schedule.TakeDue(Now))
            {
                asked++;
                Assert.Equal(PublicIpSchedule.MaxBackoff, t + TimeSpan.FromSeconds(5));
                _schedule.LookedUp(Now, answered: false);
            }
        }

        Assert.Equal(1, asked);
    }

    [Fact]
    public void ComingIntoUseAgain_DuringABackOff_WaitsForItsEnd()
    {
        _schedule.NoteUse(Now);
        LookUp(answered: false);
        LookUpAfterTheBackOff(answered: false);
        LookUpAfterTheBackOff(answered: false);
        LookUpAfterTheBackOff(answered: false);
        Assert.Equal(TimeSpan.FromMinutes(4), _schedule.Backoff);

        // Unused for longer than the use window, then read again within the back-off.
        Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.True(_schedule.NoteUse(Now));
        Assert.False(_schedule.TakeDue(Now));
        Assert.Equal(TimeSpan.FromSeconds(119), _schedule.WaitFrom(Now));
        InUseFor(TimeSpan.FromSeconds(118));
        Assert.False(_schedule.TakeDue(Now));
        InUseFor(TimeSpan.FromSeconds(1));
        LookUp();
    }

    private void LookUpAfterTheBackOff(bool answered)
    {
        InUseFor(_schedule.WaitFrom(Now)!.Value);
        LookUp(answered);
    }

    [Fact]
    public void BackOff_OnErrors_DoublingFrom30sUpTo30Minutes_ResetByAnAnswer()
    {
        _schedule.NoteUse(Now);
        LookUp(answered: false);

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 9; i++)
        {
            var wait = _schedule.WaitFrom(Now)!.Value;
            waits.Add(wait);
            InUseFor(wait - TimeSpan.FromSeconds(1));
            Assert.False(_schedule.TakeDue(Now));
            InUseFor(TimeSpan.FromSeconds(1));
            LookUp(answered: false);
        }

        Assert.Equal(
            new[] { 30, 60, 120, 240, 480, 960, 1800, 1800, 1800 }.Select(s => TimeSpan.FromSeconds(s)),
            waits);
        Assert.Equal(TimeSpan.FromMinutes(30), _schedule.Backoff);

        InUseFor(_schedule.WaitFrom(Now)!.Value);
        LookUp(answered: true);
        Assert.Equal(TimeSpan.Zero, _schedule.Backoff);
        Assert.Equal(TimeSpan.FromMinutes(30), _schedule.WaitFrom(Now));

        InUseFor(TimeSpan.FromMinutes(30));
        LookUp(answered: false);
        Assert.Equal(TimeSpan.FromSeconds(30), _schedule.WaitFrom(Now));
    }

    [Fact]
    public void ACauseDuringALookup_AsksOnceMoreAfterIt()
    {
        _schedule.NoteUse(Now);
        Assert.True(_schedule.TakeDue(Now));
        // The network changes while the lookup is in flight.
        Assert.True(_schedule.NetworkChanged(Now));
        _schedule.LookedUp(Now, answered: true);

        LookUp();
        Assert.False(_schedule.TakeDue(Now));
    }
}
