using InfoPanel.Editing;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

/// <summary>The coalescing rules of <see cref="UndoHistory"/>.</summary>
public class UndoHistoryTests
{
    private readonly ManualTimeProvider _time = new();

    private static DocumentSnapshot S(int n) => new($"p{n}", $"i{n}");

    [Fact]
    public void GestureKeys_AlwaysMergeWithTheSameKey()
    {
        var history = new UndoHistory(_time);

        Assert.False(history.Record(S(0), "gesture:1"));
        _time.Advance(60_000);
        Assert.True(history.Record(S(1), "gesture:1"));
        Assert.False(history.Record(S(2), "gesture:2"));

        Assert.Equal(2, history.UndoCount);
        Assert.Equal(S(2), history.Undo(S(3)));
        Assert.Equal(S(0), history.Undo(S(2)));
    }

    [Fact]
    public void OtherKeys_MergeOnlyWithin1000ms_OfThePreviousRecord()
    {
        var history = new UndoHistory(_time);

        history.Record(S(0), "nudge:a");
        _time.Advance(999);
        Assert.True(history.Record(S(1), "nudge:a"));
        _time.Advance(999); // the window restarts at each record
        Assert.True(history.Record(S(2), "nudge:a"));
        _time.Advance(1000);
        Assert.False(history.Record(S(3), "nudge:a"));
        _time.Advance(1);
        Assert.False(history.Record(S(4), "nudge:b"));

        Assert.Equal(3, history.UndoCount);
    }

    [Fact]
    public void NoKey_NeverMerges()
    {
        var history = new UndoHistory(_time);

        Assert.False(history.Record(S(0), null));
        Assert.False(history.Record(S(1), null));

        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void UndoEndsAMerge_AndARecordClearsRedo()
    {
        var history = new UndoHistory(_time);

        history.Record(S(0), "gesture:1");
        Assert.Equal(S(0), history.Undo(S(1)));
        Assert.True(history.CanRedo);
        Assert.False(history.Record(S(0), "gesture:1")); // not merged into anything after an undo
        Assert.False(history.CanRedo);
        Assert.Null(history.Redo(S(5)));
    }

    [Fact]
    public void RedoReturnsTheUndoneState()
    {
        var history = new UndoHistory(_time);
        history.Record(S(0), null);
        history.Record(S(1), null);

        Assert.Equal(S(1), history.Undo(S(2)));
        Assert.Equal(S(0), history.Undo(S(1)));
        Assert.Null(history.Undo(S(0)));
        Assert.Equal(S(1), history.Redo(S(0)));
        Assert.Equal(S(2), history.Redo(S(1)));
        Assert.Null(history.Redo(S(2)));
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void KeepsTheNewest100()
    {
        var history = new UndoHistory(_time);
        for (var i = 0; i < 130; i++)
            history.Record(S(i), null);

        Assert.Equal(UndoHistory.Limit, history.UndoCount);
        DocumentSnapshot? last = null;
        while (history.CanUndo)
            last = history.Undo(S(-1));
        Assert.Equal(S(30), last);
    }
}
