using InfoPanel.Shell;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>The menu-bar item's menu as the model builds it: what the user sees when they click the icon.</summary>
public sealed class ShellMenuTests
{
    [Fact]
    public void TheMenu_IsTheStatusLine_OpenEditor_AndQuit_AndNothingElse()
    {
        var menu = ShellMenu.Build(new ShellState("2.0.0-dev"));

        Assert.Collection(menu.Items,
            status =>
            {
                Assert.Equal(ShellItemKind.Status, status.Kind);
                Assert.Equal("InfoPanel 2.0.0-dev is running", status.Label);
                Assert.False(status.Enabled);
            },
            open =>
            {
                Assert.Equal(ShellMenu.OpenEditor, open.Id);
                Assert.Equal("Open Editor", open.Label);
                Assert.True(open.Enabled);
            },
            separator => Assert.Equal(ShellItemKind.Separator, separator.Kind),
            quit =>
            {
                Assert.Equal(ShellMenu.Quit, quit.Id);
                Assert.Equal("Quit InfoPanel", quit.Label);
                Assert.Equal("q", quit.KeyEquivalent);
                Assert.True(quit.Enabled);
            });
    }

    [Fact]
    public void TheStatusLine_NamesTheVersion()
    {
        Assert.Equal("InfoPanel 2.1.0 is running", ShellMenu.Build(new ShellState("2.1.0")).Items[0].Label);
    }

    [Fact]
    public void TheGlyph_IsAOneColourTemplate_WithInkInside()
    {
        var png = ShellGlyph.Png(36);

        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(36, bitmap.Width);
        Assert.Equal(36, bitmap.Height);
        var inked = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha == 0)
                    continue;
                inked++;
                // A template image is black with alpha; macOS tints it for the menu bar.
                Assert.Equal(0, pixel.Red);
                Assert.Equal(0, pixel.Green);
                Assert.Equal(0, pixel.Blue);
            }
        }

        Assert.InRange(inked, 36 * 36 / 10, 36 * 36 / 2);
        // The corners are outside the hexagon.
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bitmap.GetPixel(35, 35).Alpha);
    }
}
