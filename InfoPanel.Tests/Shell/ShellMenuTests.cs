using InfoPanel.Shell;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>The menu-bar item's menu as the model builds it: what the user sees when they click the icon.</summary>
public sealed class ShellMenuTests
{
    private static ShellState State(params ShellProfile[] profiles) =>
        new("2.0.0-dev") { HasCredential = true, SignedInAs = "Ann Lee", SignedInEmail = "ann@example.com", DesktopWindows = true, Profiles = profiles };

    private static ShellProfile[] Profiles(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new ShellProfile($"p{i}", $"Profile {i}", Shown: i % 2 == 0))];

    private static ShellMenuItem Item(ShellMenu menu, string id) => Assert.Single(menu.Items, i => i.Id == id);

    [Fact]
    public void TheMenu_InOrder_HeaderAndStatus_WhoIsSignedIn_OpenEditor_EditFromAnotherComputer_ShowOnDesktop_TheFolders_Details_AndQuit()
    {
        var menu = ShellMenu.Build(State(new ShellProfile("a", "Desk overlay", Shown: true)));

        Assert.Equal(
        [
            (ShellMenu.Header, "InfoPanel", ShellItemKind.Header),
            (ShellMenu.Status, "Running · 2.0.0-dev", ShellItemKind.Status),
            ("", "", ShellItemKind.Separator),
            (ShellMenu.SignedIn, "Ann Lee", ShellItemKind.Action),
            ("", "", ShellItemKind.Separator),
            (ShellMenu.OpenEditor, "Open Editor", ShellItemKind.Action),
            (ShellMenu.EditFromAnotherComputer, "Edit from Another Computer…", ShellItemKind.Action),
            ("", "", ShellItemKind.Separator),
            (ShellMenu.ShowOnDesktop, "Show on Desktop", ShellItemKind.Submenu),
            (ShellMenu.ClickThrough, "Click-Through", ShellItemKind.Submenu),
            ("", "", ShellItemKind.Separator),
            (ShellMenu.ShowDataFolder, "Show Data Folder in Finder", ShellItemKind.Action),
            (ShellMenu.OpenLogsFolder, "Open Logs Folder", ShellItemKind.Action),
            (ShellMenu.CopyEngineDetails, "Copy Engine Details", ShellItemKind.Action),
            ("", "", ShellItemKind.Separator),
            (ShellMenu.Quit, "Quit InfoPanel", ShellItemKind.Action),
        ], menu.Items.Select(i => (i.Id, i.Label, i.Kind)));
        Assert.Equal("q", Item(menu, ShellMenu.Quit).KeyEquivalent);
        Assert.False(Item(menu, ShellMenu.Header).Enabled);
        Assert.False(Item(menu, ShellMenu.Status).Enabled);
        Assert.True(Item(menu, ShellMenu.SignedIn).Enabled);
        Assert.True(Item(menu, ShellMenu.ShowOnDesktop).Enabled);
        Assert.True(Item(menu, ShellMenu.ClickThrough).Enabled);
        Assert.All(menu.Items.Where(i => i.Kind == ShellItemKind.Action), i => Assert.True(i.Enabled));
        // Only the row of who is signed in has a second line or an image.
        Assert.All(menu.Items.Where(i => i.Id != ShellMenu.SignedIn), i => Assert.Equal((null, null), (i.Detail, i.Initial)));
    }

    [Fact]
    public void TheHeader_IsTheAppsName_AndTheStatus_SaysRunning_WithTheVersion()
    {
        var items = ShellMenu.Build(new ShellState("2.1.0")).Items;

        Assert.Equal((ShellMenu.Header, "InfoPanel", ShellItemKind.Header), (items[0].Id, items[0].Label, items[0].Kind));
        Assert.Equal((ShellMenu.Status, "Running · 2.1.0", ShellItemKind.Status), (items[1].Id, items[1].Label, items[1].Kind));
        Assert.Equal(ShellItemKind.Separator, items[2].Kind);
    }

    [Fact]
    public void WhoIsSignedIn_TheName_TheEmailBeneath_AndTheNamesInitial_AClickableRow()
    {
        var menu = ShellMenu.Build(new ShellState("2.0.0") { HasCredential = true, SignedInAs = "habib rehman", SignedInEmail = "habib@example.com" });

        var row = Item(menu, ShellMenu.SignedIn);
        Assert.Equal(("habib rehman", "habib@example.com", "H", ShellItemKind.Action, true), (row.Label, row.Detail, row.Initial, row.Kind, row.Enabled));
        // The name is the line that stands out; the email beneath is secondary.
        Assert.False(row.DetailIsAction);
        Assert.Equal(3, menu.Items.ToList().IndexOf(row));
        Assert.Equal([ShellItemKind.Separator, ShellItemKind.Separator], new[] { menu.Items[2].Kind, menu.Items[4].Kind });
        Assert.Empty(row.Children);
    }

    [Fact]
    public void WhoIsSignedIn_WithoutAName_TheEmailAlone_ItsInitial()
    {
        // The holder named by the email alone (no name): it is the line, with nothing beneath.
        var row = Item(ShellMenu.Build(new ShellState("2.0.0") { HasCredential = true, SignedInAs = "zed@example.com" }), ShellMenu.SignedIn);

        Assert.Equal(("zed@example.com", (string?)null, "Z", ShellItemKind.Action), (row.Label, row.Detail, row.Initial, row.Kind));
    }

    [Fact]
    public void WhoIsSignedIn_NotSignedIn_WithSignInBeneath_AClickableRow_WhateverNameIsKept()
    {
        foreach (var state in new[] { new ShellState("2.0.0"), new ShellState("2.0.0") { SignedInAs = "Ann", SignedInEmail = "ann@example.com" } })
        {
            var menu = ShellMenu.Build(state);
            var row = Item(menu, ShellMenu.SignedIn);
            // A name kept without a credential (or after the service refused it) never shows.
            Assert.Equal(("Not signed in", "Sign In…", (string?)null, ShellItemKind.Action, true), (row.Label, row.Detail, row.Initial, row.Kind, row.Enabled));
            // Sign In… is what choosing the row does: the line that stands out, "Not signed in" the secondary one.
            Assert.True(row.DetailIsAction);
            Assert.Equal(3, menu.Items.ToList().IndexOf(row));
        }
    }

    [Fact]
    public void WhoIsSignedIn_IsLeftOut_WithItsSeparator_WhileTheNameIsntKnownYet()
    {
        var menu = ShellMenu.Build(new ShellState("2.0.0") { HasCredential = true, SignedInAs = null, SignedInEmail = "ignored@example.com" });

        Assert.DoesNotContain(menu.Items, i => i.Id == ShellMenu.SignedIn);
        Assert.Equal([ShellMenu.Header, ShellMenu.Status, "", ShellMenu.OpenEditor], menu.Items.Take(4).Select(i => i.Id));
    }

    /// <summary>The round initial: the first letter or digit, in capitals (a whole character, an accent included); none without one.</summary>
    [Theory]
    [InlineData("habib", "H")]
    [InlineData("élan Vital", "É")]
    [InlineData("(Ann) Lee", "A")]
    [InlineData("👩🏽‍💻 Bo", "B")]
    [InlineData("42 Club", "4")]
    [InlineData("e\u0301mile", "E\u0301")]
    [InlineData("…", null)]
    public void TheInitial_IsTheFirstLetterOrDigit_InCapitals(string name, string? initial)
    {
        Assert.Equal(initial, ShellMenu.InitialOf(name));
        Assert.Equal(initial, Item(ShellMenu.Build(new ShellState("2.0.0") { HasCredential = true, SignedInAs = name }), ShellMenu.SignedIn).Initial);
    }

    /// <summary>Edit from Another Computer… is always there, an action right below Open Editor, whatever the state.</summary>
    [Fact]
    public void EditFromAnotherComputer_FollowsOpenEditor_InEveryState()
    {
        foreach (var state in new[]
                 {
                     new ShellState("2.0.0"),
                     new ShellState("2.0.0") { HasCredential = true },
                     State() with { DesktopWindows = false },
                     State(Profiles(21)),
                 })
        {
            var ids = ShellMenu.Build(state).Items.Select(i => i.Id).ToList();
            Assert.Equal(ids.IndexOf(ShellMenu.OpenEditor) + 1, ids.IndexOf(ShellMenu.EditFromAnotherComputer));
            var line = Item(ShellMenu.Build(state), ShellMenu.EditFromAnotherComputer);
            Assert.Equal(("Edit from Another Computer…", ShellItemKind.Action, true, (string?)null), (line.Label, line.Kind, line.Enabled, line.KeyEquivalent));
        }
    }

    [Fact]
    public void ShowOnDesktop_ListsEachProfile_InOrder_CheckedWhenShown()
    {
        var menu = ShellMenu.Build(State(
            new ShellProfile("a", "Desk overlay", Shown: true),
            new ShellProfile("b", "Wide bar", Shown: false),
            new ShellProfile("c", "Mac overview", Shown: true)));

        var children = Item(menu, ShellMenu.ShowOnDesktop).Children;
        Assert.Equal(["Desk overlay", "Wide bar", "Mac overview"], children.Select(c => c.Label));
        Assert.Equal([true, false, true], children.Select(c => c.Checked));
        Assert.All(children, c => Assert.Equal(ShellItemKind.Action, c.Kind));
        // Choosing one asks for the other state: a shown profile's item hides it, a hidden one's shows it.
        Assert.Equal([ShellMenu.DesktopItem("a", show: false), ShellMenu.DesktopItem("b", show: true), ShellMenu.DesktopItem("c", show: false)],
            children.Select(c => c.Id));
    }

    [Fact]
    public void ShowOnDesktop_WithOneProfile_HasJustIt()
    {
        var children = Item(ShellMenu.Build(State(new ShellProfile("a", "Desk overlay", Shown: false))), ShellMenu.ShowOnDesktop).Children;

        var only = Assert.Single(children);
        Assert.Equal("Desk overlay", only.Label);
        Assert.False(only.Checked);
    }

    [Fact]
    public void ShowOnDesktop_WithNoProfiles_SaysNoProfilesYet_Greyed()
    {
        var children = Item(ShellMenu.Build(State()), ShellMenu.ShowOnDesktop).Children;

        var only = Assert.Single(children);
        Assert.Equal((ShellMenu.NoProfiles, "No Profiles Yet", ShellItemKind.Status), (only.Id, only.Label, only.Kind));
        Assert.False(only.Enabled);
    }

    [Fact]
    public void ShowOnDesktop_WithTwentyProfiles_ListsThemAll()
    {
        var children = Item(ShellMenu.Build(State(Profiles(20))), ShellMenu.ShowOnDesktop).Children;

        Assert.Equal(20, children.Count);
        Assert.DoesNotContain(children, c => c.Id == ShellMenu.MoreProfiles);
    }

    [Fact]
    public void ShowOnDesktop_PastTwentyProfiles_ListsTheFirstTwenty_ThenMoreInTheEditor()
    {
        var children = Item(ShellMenu.Build(State(Profiles(21))), ShellMenu.ShowOnDesktop).Children;

        Assert.Equal(22, children.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"Profile {i}"), children.Take(20).Select(c => c.Label));
        Assert.Equal(ShellItemKind.Separator, children[20].Kind);
        Assert.Equal((ShellMenu.MoreProfiles, "More in the Editor…", ShellItemKind.Action), (children[21].Id, children[21].Label, children[21].Kind));
    }

    [Fact]
    public void ShowOnDesktop_IsLeftOut_WhenDesktopWindowsAreOff()
    {
        var menu = ShellMenu.Build(State(new ShellProfile("a", "Desk overlay", Shown: true)) with { DesktopWindows = false });

        Assert.DoesNotContain(menu.Items, i => i.Id == ShellMenu.ShowOnDesktop);
        Assert.DoesNotContain(menu.Items, i => i.Id == ShellMenu.ClickThrough);
        // No two separators in a row: the folders follow the editor's lines' separator.
        Assert.Equal([ShellMenu.Header, ShellMenu.Status, "", ShellMenu.SignedIn, "", ShellMenu.OpenEditor, ShellMenu.EditFromAnotherComputer, "", ShellMenu.ShowDataFolder],
            menu.Items.Take(9).Select(i => i.Id));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("1c6f2a9e-0b7d-4d3e-9a41-5f2b8c7d6e10", false)]
    [InlineData("with:colon", true)]
    public void ADesktopItemsId_ReadsBack_AsItsProfileAndState(string profile, bool show)
    {
        Assert.True(ShellMenu.TryReadDesktopItem(ShellMenu.DesktopItem(profile, show), out var id, out var shown));
        Assert.Equal((profile, show), (id, shown));
    }

    [Theory]
    [InlineData(ShellMenu.OpenEditor)]
    [InlineData("desktop:")]
    [InlineData("desktop:maybe:a")]
    [InlineData("desktop:show:")]
    public void AnyOtherId_IsntADesktopItem(string id)
    {
        Assert.False(ShellMenu.TryReadDesktopItem(id, out _, out _));
    }

    [Fact]
    public void ClickThrough_ListsOnlyTheShownWindows_InOrder_CheckedWhileClicksPassThrough()
    {
        var menu = ShellMenu.Build(State(
            new ShellProfile("a", "Desk overlay", Shown: true, ClickThrough: true),
            new ShellProfile("b", "Wide bar", Shown: false, ClickThrough: true),
            new ShellProfile("c", "Mac overview", Shown: true)));

        var children = Item(menu, ShellMenu.ClickThrough).Children;
        Assert.Equal(["Desk overlay", "Mac overview"], children.Select(c => c.Label));
        Assert.Equal([true, false], children.Select(c => c.Checked));
        Assert.All(children, c => Assert.Equal(ShellItemKind.Action, c.Kind));
        // Choosing one asks for the other state: a checked line lets the window take clicks again, an unchecked one lets them through.
        Assert.Equal([ShellMenu.ClickThroughItem("a", on: false), ShellMenu.ClickThroughItem("c", on: true)], children.Select(c => c.Id));
        // Show on Desktop is as it was: every profile, a click away.
        Assert.Equal(3, Item(menu, ShellMenu.ShowOnDesktop).Children.Count);
    }

    [Fact]
    public void ClickThrough_WithNoWindowShown_SaysNoWindowsShown_Greyed()
    {
        foreach (var state in new[] { State(), State(new ShellProfile("a", "Desk overlay", Shown: false, ClickThrough: true)) })
        {
            var only = Assert.Single(Item(ShellMenu.Build(state), ShellMenu.ClickThrough).Children);
            Assert.Equal((ShellMenu.NoWindowsShown, "No Windows Shown", ShellItemKind.Status), (only.Id, only.Label, only.Kind));
            Assert.False(only.Enabled);
        }
    }

    [Fact]
    public void ClickThrough_ListsTheShownWindows_OfTheFirstTwentyProfiles_WithNoMoreLine()
    {
        ShellProfile[] profiles = [.. Enumerable.Range(1, 22).Select(i => new ShellProfile($"p{i}", $"Profile {i}", Shown: true))];

        var children = Item(ShellMenu.Build(State(profiles)), ShellMenu.ClickThrough).Children;

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"Profile {i}"), children.Select(c => c.Label));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("1c6f2a9e-0b7d-4d3e-9a41-5f2b8c7d6e10", false)]
    [InlineData("with:colon", true)]
    public void AClickThroughItemsId_ReadsBack_AsItsProfileAndState(string profile, bool on)
    {
        Assert.True(ShellMenu.TryReadClickThroughItem(ShellMenu.ClickThroughItem(profile, on), out var id, out var asked));
        Assert.Equal((profile, on), (id, asked));
        Assert.False(ShellMenu.TryReadDesktopItem(ShellMenu.ClickThroughItem(profile, on), out _, out _));
        Assert.False(ShellMenu.TryReadClickThroughItem(ShellMenu.DesktopItem(profile, on), out _, out _));
    }

    [Theory]
    [InlineData(ShellMenu.ClickThrough)]
    [InlineData(ShellMenu.NoWindowsShown)]
    [InlineData("click-through:")]
    [InlineData("click-through:maybe:a")]
    [InlineData("click-through:on:")]
    [InlineData("click-through:show:a")]
    [InlineData("Click-Through:on:a")]
    public void AnyOtherId_IsntAClickThroughItem(string id)
    {
        Assert.False(ShellMenu.TryReadClickThroughItem(id, out _, out _));
    }

    [Fact]
    public void TwoMenusBuiltFromTheSameState_AreEqual_ChildrenIncluded()
    {
        var state = State(new ShellProfile("a", "Desk overlay", Shown: true));

        Assert.Equal(ShellMenu.Build(state), ShellMenu.Build(state));
        Assert.NotEqual(ShellMenu.Build(state), ShellMenu.Build(State(new ShellProfile("a", "Desk overlay", Shown: false))));
        Assert.NotEqual(ShellMenu.Build(state), ShellMenu.Build(State(new ShellProfile("a", "Desk overlay", Shown: true, ClickThrough: true))));
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

/// <summary>What Copy Engine Details puts on the clipboard: what a bug report needs, never a secret.</summary>
public sealed class EngineDetailsTests
{
    private static EngineDetails Details(string dataDir) => new(
        Version: "2.0.0-dev", Build: "abc1234", Protocol: 2, Platform: "macos", OsVersion: "26.1", Arch: "arm64",
        Started: "2026-11-05T15:04:05Z", Address: "http://127.0.0.1:5296", DataDir: dataDir);

    [Fact]
    public void TheText_HasTheVersion_Build_System_Started_Address_AndDataFolder_OneALine_Fenced()
    {
        var text = Details("/Users/ann/Library/Application Support/InfoPanel/v2").Text("/Users/ann");

        Assert.Equal(string.Join("\n",
            "```",
            "InfoPanel engine details",
            "Engine: 2.0.0-dev, protocol 2, macOS",
            "Engine build: abc1234",
            "Engine started: 2026-11-05T15:04:05Z",
            "OS version: 26.1",
            "Architecture: arm64",
            "Engine address: http://127.0.0.1:5296",
            "Data folder: ~/Library/Application Support/InfoPanel/v2",
            "```"), text);
    }

    [Theory]
    [InlineData("/Users/ann", "/Users/ann", "~")]
    [InlineData("/Users/ann/x", "/Users/ann/", "~/x")]
    [InlineData("/Users/annabel/x", "/Users/ann", "/Users/annabel/x")]
    [InlineData("/data/v2", null, "/data/v2")]
    [InlineData(@"C:\Users\ann\AppData\Local\InfoPanel\v2", @"C:\Users\ann", @"~\AppData\Local\InfoPanel\v2")]
    public void TheHomeFolder_IsWrittenAsATilde_OnlyWhenTheDataFolderIsInIt(string dataDir, string? home, string shown)
    {
        Assert.EndsWith($"Data folder: {shown}\n```", Details(dataDir).Text(home));
    }

    [Theory]
    [InlineData("/users/ANN/Library/x", "/Users/ann", "~/Library/x")]
    [InlineData("/Users/Ann", "/users/ann/", "~")]
    [InlineData(@"c:\users\ann\AppData\x", @"C:\Users\Ann", @"~\AppData\x")]
    [InlineData("/USERS/ANNABEL/x", "/Users/ann", "/USERS/ANNABEL/x")]
    public void OnACaseInsensitiveFileSystem_TheHomeFolder_MatchesInAnyCase_StillAtAFoldersEnd(string dataDir, string home, string shown)
    {
        Assert.EndsWith($"Data folder: {shown}\n```", Details(dataDir).Text(home, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnACaseSensitiveFileSystem_AnotherCase_IsAnotherFolder()
    {
        Assert.EndsWith("Data folder: /users/ann/x\n```", Details("/users/ann/x").Text("/Users/ann", StringComparison.Ordinal));
        Assert.EndsWith("Data folder: ~/x\n```", Details("/Users/ann/x").Text("/Users/ann", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCaseRule_IsTheFileSystems_InsensitiveOnMacOSAndWindows()
    {
        var shown = Details("/users/ANN/x").Text("/Users/ann");

        Assert.EndsWith(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? "Data folder: ~/x\n```" : "Data folder: /users/ANN/x\n```", shown);
    }

    [Fact]
    public void TheCaseRule_IsWhatTheHomeFoldersVolumeSays_ElseTheSystemsDefault()
    {
        string? probed = null;
        Assert.Equal(StringComparison.Ordinal, EngineDetails.CaseRule("/Users/Ann", home =>
        {
            probed = home;
            return true;
        }));
        Assert.Equal("/Users/Ann", probed);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, EngineDetails.CaseRule("/Users/Ann", _ => false));
        Assert.Equal(EngineDetails.PathComparison, EngineDetails.CaseRule("/Users/Ann", _ => null));
        Assert.Equal(EngineDetails.PathComparison, EngineDetails.CaseRule(null, _ => throw new InvalidOperationException("no home, no probe")));
    }

    [Fact]
    public void OnAVolumeProbedCaseSensitive_TheHomeFolderInAnotherCase_IsAnotherFolder()
    {
        var sensitive = EngineDetails.CaseRule("/Users/Ann", _ => true);
        var insensitive = EngineDetails.CaseRule("/Users/Ann", _ => false);

        Assert.EndsWith("Data folder: /Users/ann/panel\n```", Details("/Users/ann/panel").Text("/Users/Ann", sensitive));
        Assert.EndsWith("Data folder: ~/panel\n```", Details("/Users/ann/panel").Text("/Users/Ann", insensitive));
        Assert.EndsWith("Data folder: /Users/Annabel/x\n```", Details("/Users/Annabel/x").Text("/Users/ann", insensitive));
    }

    [Fact]
    public void TheProbe_ReadsThisComputersHomeVolume_AndTheTextUsesIt()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
        var swapped = new string(home.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
        Assert.NotEqual(home, swapped);

        var sensitive = EngineDetails.IsCaseSensitive(home);

        if (OperatingSystem.IsWindows())
            Assert.False(sensitive);
        else if (OperatingSystem.IsMacOS())
        {
            // The volume's own answer agrees with what it does: the home folder in another case is there, or isn't.
            Assert.Equal(!Directory.Exists(swapped), sensitive);
            Assert.Null(EngineDetails.IsCaseSensitive(Path.Combine(home, $"no-such-folder-{Guid.NewGuid():N}")));
        }
        else
            Assert.True(sensitive);

        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        var shown = Details($"{swapped}{sep}x").Text(home);
        Assert.EndsWith(sensitive == false ? $"Data folder: ~{sep}x\n```" : $"Data folder: {swapped}{sep}x\n```", shown);
    }

    [Theory]
    [InlineData("windows", "Windows")]
    [InlineData("linux", "Linux")]
    [InlineData("other", "other")]
    public void TheSystem_IsNamed(string platform, string named)
    {
        Assert.Contains($"Engine: 2.0.0-dev, protocol 2, {named}\n", (Details("/d") with { Platform = platform }).Text(null));
    }
}
