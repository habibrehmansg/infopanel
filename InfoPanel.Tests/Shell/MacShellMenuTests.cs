using System.Runtime.Versioning;
using InfoPanel.Desktop.MacOS;
using InfoPanel.Shell;
using InfoPanel.Shell.MacOS;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The model's menu as AppKit's <c>NSMenu</c> holds it (<see cref="MacShellPlatform"/>), built off screen: no menu-bar
/// item, nothing shown. Each line's title, its second line and colours (an attributed title), its image, whether it can
/// be chosen, and the id a click reports. macOS only.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacShellMenuTests
{
    private static ShellState Named => new("2.0.0")
    {
        HasCredential = true,
        SignedInAs = "Habib Rehman",
        SignedInEmail = "habib@example.com",
        DesktopWindows = true,
        Profiles = [new ShellProfile("p1", "Desk overlay", Shown: true)],
    };

    /// <summary>One <c>NSMenuItem</c> as read back: its title's text, whether it is bold, the colour of its second line, and so on.</summary>
    private sealed record Line(
        string? Id, bool Separator, string Text, bool Enabled, bool HasAction, bool HasImage, bool ImageIsTemplate, bool HasSubmenu,
        string? FirstColour, string? LastColour, bool Bold, string KeyEquivalent);

    private static List<Line> Lines(ShellState state)
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var shell = new MacShellPlatform(null!);
            var menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            try
            {
                shell.Fill(menu, ShellMenu.Build(state));
                var lines = new List<Line>();
                var count = (int)ObjC.SendIntRet(menu, ObjC.Sel("numberOfItems"));
                for (var i = 0; i < count; i++)
                    lines.Add(Read(shell, ObjC.SendUInt(menu, ObjC.Sel("itemAtIndex:"), (nuint)i)));
                return lines;
            }
            finally
            {
                ObjC.SendVoid(menu, ObjC.Sel("release"));
            }
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    private static Line Read(MacShellPlatform shell, IntPtr item)
    {
        var separator = ObjC.SendBool(item, ObjC.Sel("isSeparatorItem")) != 0;
        var attributed = ObjC.Send(item, ObjC.Sel("attributedTitle"));
        var text = attributed != IntPtr.Zero
            ? ObjC.ToManagedString(ObjC.Send(attributed, ObjC.Sel("string")))!
            : ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("title")))!;
        string? first = null, last = null;
        var bold = false;
        if (attributed != IntPtr.Zero && text.Length > 0)
        {
            first = ColourName(Attribute(attributed, "NSForegroundColorAttributeName", 0));
            last = ColourName(Attribute(attributed, "NSForegroundColorAttributeName", text.Length - 1));
            var font = Attribute(attributed, "NSFontAttributeName", 0);
            bold = font != IntPtr.Zero && ObjC.ToManagedString(ObjC.Send(font, ObjC.Sel("fontName")))!.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        }

        var image = ObjC.Send(item, ObjC.Sel("image"));
        return new Line(
            separator ? null : shell.IdOf(ObjC.SendIntRet(item, ObjC.Sel("tag"))),
            separator,
            text,
            ObjC.SendBool(item, ObjC.Sel("isEnabled")) != 0,
            ObjC.Send(item, ObjC.Sel("action")) != IntPtr.Zero,
            image != IntPtr.Zero,
            image != IntPtr.Zero && ObjC.SendBool(image, ObjC.Sel("isTemplate")) != 0,
            ObjC.Send(item, ObjC.Sel("submenu")) != IntPtr.Zero,
            first,
            last,
            bold,
            ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("keyEquivalent"))) ?? "");
    }

    private static readonly IntPtr MsgSend = System.Runtime.InteropServices.NativeLibrary.GetExport(
        System.Runtime.InteropServices.NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private static unsafe IntPtr Attribute(IntPtr attributed, string name, int index)
    {
        var send = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint, IntPtr, IntPtr>)MsgSend;
        return send(attributed, ObjC.Sel("attribute:atIndex:effectiveRange:"), ObjC.Constant("AppKit", name), (nuint)index, IntPtr.Zero);
    }

    private static unsafe bool IsEqual(IntPtr a, IntPtr b) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(a, ObjC.Sel("isEqual:"), b) != 0;

    /// <summary>A system colour's name (<c>labelColor</c>, <c>secondaryLabelColor</c>), or null for none.</summary>
    private static string? ColourName(IntPtr colour)
    {
        if (colour == IntPtr.Zero)
            return null;
        foreach (var name in new[] { "labelColor", "secondaryLabelColor", "tertiaryLabelColor", "disabledControlTextColor" })
        {
            if (IsEqual(colour, ObjC.Send(ObjC.Class("NSColor"), ObjC.Sel(name))))
                return name;
        }

        return "other";
    }

    [Fact]
    public void TheMenu_HeaderStatus_TheRowOfWhoIsSignedIn_ThenTheRest_InOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        var lines = Lines(Named);

        Assert.Equal(
        [
            "InfoPanel", "Running · 2.0.0", "",
            "Habib Rehman\nhabib@example.com", "",
            "Open Editor", "Edit from Another Computer…", "",
            "Show on Desktop", "Click-Through", "",
            "Show Data Folder in Finder", "Open Logs Folder", "Copy Engine Details", "",
            "Quit InfoPanel",
        ], lines.Select(l => l.Separator ? "" : l.Text));
        Assert.Equal(
        [
            ShellMenu.Header, ShellMenu.Status, null, ShellMenu.SignedIn, null, ShellMenu.OpenEditor, ShellMenu.EditFromAnotherComputer, null,
            ShellMenu.ShowOnDesktop, ShellMenu.ClickThrough, null, ShellMenu.ShowDataFolder, ShellMenu.OpenLogsFolder, ShellMenu.CopyEngineDetails, null, ShellMenu.Quit,
        ], lines.Select(l => l.Id));
        Assert.Equal("q", lines[^1].KeyEquivalent);
    }

    [Fact]
    public void TheHeader_IsTheAppsName_InBold_NotGreyed_AndTheStatus_IsInTheSecondaryColour()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        var lines = Lines(Named);

        var header = lines[0];
        Assert.Equal(("InfoPanel", true, "labelColor", false, false, false), (header.Text, header.Bold, header.FirstColour, header.Enabled, header.HasAction, header.HasImage));
        var status = lines[1];
        Assert.Equal(("Running · 2.0.0", false, "secondaryLabelColor", false, false), (status.Text, status.Bold, status.FirstColour, status.Enabled, status.HasAction));
    }

    [Fact]
    public void WhoIsSignedIn_TheInitial_TheName_AndTheEmailInTheSecondaryColour_OneRowThatOpensTheSignInSettings()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        var row = Lines(Named).Single(l => l.Id == ShellMenu.SignedIn);

        Assert.Equal("Habib Rehman\nhabib@example.com", row.Text);
        // The name in the label colour, named (without one an attributed title is drawn black, even on a dark menu).
        Assert.Equal(("labelColor", "secondaryLabelColor"), (row.FirstColour, row.LastColour));
        Assert.True(row.HasImage, "the round initial");
        Assert.False(row.ImageIsTemplate, "the initial keeps its colours");
        Assert.Equal((true, true, false, false), (row.Enabled, row.HasAction, row.HasSubmenu, row.Bold));
    }

    [Fact]
    public void WhoIsSignedIn_WithoutAName_TheEmailAlone_WithItsInitial()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        var row = Lines(Named with { SignedInAs = "habib@example.com", SignedInEmail = null }).Single(l => l.Id == ShellMenu.SignedIn);

        Assert.Equal(("habib@example.com", true, true), (row.Text, row.HasImage, row.HasAction));
    }

    [Fact]
    public void WhoIsSignedIn_SignedOut_NotSignedIn_WithSignInBeneath_NoImage_AndClickable()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        var row = Lines(new ShellState("2.0.0")).Single(l => l.Id == ShellMenu.SignedIn);

        Assert.Equal(("Not signed in\nSign In…", false, true, true), (row.Text, row.HasImage, row.Enabled, row.HasAction));
        // "Not signed in" in the secondary colour, Sign In… (what the row does) in the label colour, so it doesn't read as disabled.
        Assert.Equal(("secondaryLabelColor", "labelColor"), (row.FirstColour, row.LastColour));
    }

    [Fact]
    public void TheInitial_IsDrawnFromTheLetter_ARoundBadge()
    {
        var png = ShellInitial.Png("H", 52);
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.Equal((52, 52), (bitmap.Width, bitmap.Height));
        // Round: the corners are clear, the middle of each edge is filled, and the letter is drawn in its middle.
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bitmap.GetPixel(51, 51).Alpha);
        Assert.Equal(255, bitmap.GetPixel(26, 1).Alpha);
        Assert.Equal(255, bitmap.GetPixel(1, 26).Alpha);
        Assert.NotEqual(ShellInitial.Png("H", 52), ShellInitial.Png("Z", 52));
    }
}
