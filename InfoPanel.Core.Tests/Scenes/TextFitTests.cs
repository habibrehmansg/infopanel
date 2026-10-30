using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The tick snapshot's report of its text nodes (<see cref="TickSnapshot.Texts"/>): each text node of each visible item,
/// where its lines are drawn on the canvas, and whether its text fits its box, as the text layout knows it (cut with
/// "…", clipped, lines left out, shrunk to its fit minimum, drawn past its box). Internal to the engine: no spec change.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class TextFitTests
{
    private const string Long = "The quick brown fox jumps over the lazy dog";

    private static IReadOnlyList<TextReport> Texts(string node, int width = 400, int height = 200, double x = 0, double y = 0)
    {
        var scene = SceneKit.Json($"{{'format':'infopanel.scene/1','nodes':[{node}]}}");
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, width, height, x, y), SceneKit.Now, SceneKit.Options());
        return snapshot.Texts;
    }

    private static string Text(string text, string box, string more = "") =>
        $"{{'kind':'text','x':10,'y':20,{box}'text':'{text}','font':{{'family':'Inter','size':16}},'fill':'#000000'{more}}}";

    [Fact]
    public void A_short_text_in_a_big_box_fits_and_is_placed_on_the_canvas()
    {
        var report = Assert.Single(Texts(Text("CPU", "'w':200,'h':40,"), x: 100, y: 50));
        Assert.Equal(TextFit.Fits, report.Fit);
        Assert.Equal("item", report.ItemId);
        Assert.Equal("CPU", report.Text);
        Assert.InRange(report.Box.X, 109.5, 111);
        Assert.InRange(report.Box.Y, 69.5, 71);
        Assert.InRange(report.Box.W, 20, 45); // the lines' own width, not the box's
    }

    /// <summary>Codex p2 #5: end-aligned text in a wide box is reported where its lines are, not over the box's unused width.</summary>
    [Fact]
    public void Aligned_text_is_reported_where_its_lines_are_drawn()
    {
        var end = Assert.Single(Texts(Text("CPU", "'w':200,", ",'align':'end'")));
        Assert.InRange(end.Box.X + end.Box.W, 209, 211); // x 10, box 200 wide
        Assert.InRange(end.Box.X, 160, 195);
        var middle = Assert.Single(Texts(Text("CPU", "'w':200,", ",'align':'center'")));
        Assert.InRange(middle.Box.X + middle.Box.W / 2, 109, 111);
        Assert.InRange(middle.Box.W, 20, 45);

        // Line by line: a short second line is reported over its own width, centred, not the first line's.
        var two = Assert.Single(Texts(Text("A much wider first line\\nB", "'w':300,", ",'align':'center'")));
        Assert.Equal(2, two.Lines!.Count);
        Assert.True(two.Lines[1].W < two.Lines[0].W / 4, $"{two.Lines[0]} {two.Lines[1]}");
        Assert.All(two.Lines, l => Assert.InRange(l.X + l.W / 2, 159, 161)); // x 10, box 300 wide
        Assert.True(two.Lines[1].Y >= two.Lines[0].Y + two.Lines[0].H - 0.5);
        Assert.Equal(two.Lines.Min(l => l.X), two.Box.X, 3);
    }

    [Fact]
    public void A_text_without_a_box_always_fits() =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text(Long, ""))).Fit);

    [Fact]
    public void A_line_cut_with_an_ellipsis_is_reported() =>
        Assert.Equal(TextFit.Ellipsis, Assert.Single(Texts(Text(Long, "'w':80,", ",'overflow':'ellipsis'"))).Fit);

    [Fact]
    public void Wrapped_lines_cut_at_the_box_height_with_an_ellipsis_are_reported() =>
        Assert.Equal(TextFit.Ellipsis, Assert.Single(Texts(Text(Long, "'w':120,'h':30,", ",'wrap':'word','overflow':'ellipsis'"))).Fit);

    [Fact]
    public void A_text_that_fits_with_overflow_ellipsis_fits() =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text("CPU", "'w':120,'h':30,", ",'wrap':'word','overflow':'ellipsis'"))).Fit);

    [Theory]
    [InlineData("CPU")]
    [InlineData("Processor package temperature")]
    public void One_line_that_fits_with_overflow_ellipsis_fits(string text) =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text(text, "'w':300,", ",'overflow':'ellipsis'"))).Fit);

    [Fact]
    public void Lines_up_to_maxLines_fit() =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text("CPU", "'w':300,", ",'wrap':'word','maxLines':1"))).Fit);

    [Fact]
    public void A_clipped_text_is_reported() =>
        Assert.Equal(TextFit.Clipped, Assert.Single(Texts(Text(Long, "'w':80,'h':30,", ",'overflow':'clip'"))).Fit);

    [Fact]
    public void A_text_drawn_past_its_box_is_reported()
    {
        Assert.Equal(TextFit.PastBox, Assert.Single(Texts(Text(Long, "'w':80,"))).Fit); // wider
        Assert.Equal(TextFit.PastBox, Assert.Single(Texts(Text(Long, "'w':120,'h':20,", ",'wrap':'word'"))).Fit); // taller
    }

    [Fact]
    public void Lines_left_out_by_maxLines_are_reported() =>
        Assert.Equal(TextFit.LinesLeftOut, Assert.Single(Texts(Text(Long, "'w':120,", ",'wrap':'word','maxLines':1"))).Fit);

    [Fact]
    public void A_fit_that_reached_its_minimum_and_still_overflows_is_reported() =>
        Assert.Equal(TextFit.FitMinimum, Assert.Single(Texts(Text(Long, "'w':60,'h':20,", ",'fit':{'min':12}"))).Fit);

    [Fact]
    public void A_fit_that_shrank_until_it_fits_fits() =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text("Temperature", "'w':80,", ",'fit':{'min':6}"))).Fit);

    [Fact]
    public void A_marquee_scrolls_rather_than_overflows() =>
        Assert.Equal(TextFit.Fits, Assert.Single(Texts(Text(Long, "'w':80,", ",'overflow':'marquee'"))).Fit);

    [Fact]
    public void Every_text_node_is_reported_in_drawing_order_and_hidden_ones_are_not()
    {
        var reports = Texts($"{Text("one", "'w':200,")},{{'kind':'group','children':[{Text("two", "'w':200,")}]}},{Text("three", "'w':200,", ",'visible':false")}");
        Assert.Equal(["one", "two"], reports.Select(r => r.Text));
    }

    [Fact]
    public void Spans_are_reported_as_their_joined_text()
    {
        var report = Assert.Single(Texts("{'kind':'text','x':0,'y':0,'w':300,'text':[{'text':'42'},{'text':' °C','font':{'size':10}}],'font':{'family':'Inter','size':20}}"));
        Assert.Equal("42 °C", report.Text);
    }
}
