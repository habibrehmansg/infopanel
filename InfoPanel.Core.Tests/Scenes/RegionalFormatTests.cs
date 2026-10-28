using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The regional format (spec/docs/expressions.md, "Regional format"): <c>format</c>'s separators and <c>formatTime</c>'s
/// names come from the profile's <c>locale</c>, else the PC's format, never the process culture; how the PC's format is
/// read from CoreFoundation's identifiers; the separators' bounds.
/// </summary>
public sealed class RegionalFormatTests
{
    private const string Green = "#00ff00ff";
    private const string Red = "#ff0000ff";

    private static readonly RegionalFormat German = RegionalFormat.Lookup("de-DE")!;
    private static readonly RegionalFormat American = RegionalFormat.Lookup("en-US")!;

    /// <summary>
    /// A one-item document whose square is green when <paramref name="expr"/> is <paramref name="expected"/> and red
    /// otherwise, with the profile's <c>locale</c> <paramref name="locale"/> (none when null).
    /// </summary>
    internal static JsonElement Probe(JsonNode expr, string expected, string? locale)
    {
        var scene = new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["kind"] = "rect",
                ["w"] = 4,
                ["h"] = 4,
                ["fill"] = new JsonObject
                {
                    ["op"] = "if",
                    ["args"] = new JsonArray(new JsonObject { ["op"] = "eq", ["args"] = new JsonArray(expr, expected) }, Green, Red),
                },
            }),
        };
        return WithLocale(SceneKit.Doc(JsonSerializer.SerializeToElement(scene), 4, 4), locale);
    }

    internal static JsonElement WithLocale(JsonElement document, string? locale)
    {
        var doc = JsonNode.Parse(document.GetRawText())!.AsObject();
        if (locale is not null) doc["profile"]!["locale"] = locale;
        return JsonSerializer.SerializeToElement(doc);
    }

    private static JsonObject Format(double value, int decimals, bool group, string? decimalSeparator = null, string? groupSeparator = null)
    {
        var f = new JsonObject { ["op"] = "format", ["value"] = value, ["decimals"] = decimals, ["group"] = group };
        if (decimalSeparator is not null) f["decimalSeparator"] = decimalSeparator;
        if (groupSeparator is not null) f["groupSeparator"] = groupSeparator;
        return f;
    }

    private static JsonObject FormatTime(string pattern, string? locale = null)
    {
        var f = new JsonObject { ["op"] = "formatTime", ["pattern"] = pattern };
        if (locale is not null) f["locale"] = locale;
        return f;
    }

    /// <summary>Draws the probe once at <paramref name="now"/> with the PC's format <paramref name="pc"/>; true when it is green.</summary>
    private static (bool Matches, RegionalFormat Format) Draw(JsonElement document, RegionalFormat pc, DateTimeOffset? now = null, ILogger? logger = null)
    {
        using var runtime = new SceneRuntime(SceneKit.Options(locale: pc, logger: logger));
        runtime.Load(document);
        using var snapshot = runtime.Snapshot(now ?? SceneKit.Now);
        using var bitmap = snapshot.Render();
        var pixel = bitmap.GetPixel(1, 1);
        Assert.True(pixel == new SKColor(0, 255, 0) || pixel == new SKColor(255, 0, 0), $"Unexpected pixel {pixel}.");
        return (pixel == new SKColor(0, 255, 0), snapshot.Clock.Format);
    }

    [Fact]
    public void A_profile_without_a_locale_draws_numbers_in_the_PCs_format()
    {
        var (matches, format) = Draw(Probe(Format(1234.5, 1, group: true), "1.234,5", locale: null), German);
        Assert.True(matches, "format(1234.5, 1, group) with the PC's format de-DE should be 1.234,5.");
        Assert.Same(German, format);

        Assert.True(Draw(Probe(Format(1234.5, 1, group: true), "1,234.5", locale: null), American).Matches);
    }

    [Fact]
    public void The_profiles_locale_wins_over_the_PCs_format()
    {
        var (matches, format) = Draw(Probe(Format(1234.5, 1, group: true), "1 234,5", "fr-FR"), German);
        Assert.True(matches, "format(1234.5, 1, group) under profile.locale fr-FR should be 1 234,5 with U+202F.");
        Assert.Equal(("fr-FR", ",", " "), (format.Tag, format.DecimalSeparator, format.GroupSeparator));
    }

    [Fact]
    public void An_explicit_separator_wins_on_its_own()
    {
        Assert.True(Draw(Probe(Format(1234.5, 1, group: true, decimalSeparator: ":"), "1.234:5", locale: null), German).Matches,
            "decimalSeparator ':' alone with the PC's format de-DE should give 1.234:5.");
        Assert.True(Draw(Probe(Format(1234.5, 1, group: true, groupSeparator: "'"), "1'234,5", "de-DE"), American).Matches);
        Assert.True(Draw(Probe(Format(1234.5, 1, group: true, ".", ","), "1,234.5", "fr-FR"), German).Matches);
    }

    [Fact]
    public void Digits_minus_and_groups_of_three_never_change()
    {
        // hi-IN groups 3,2 in its own data; ar-EG has native digits: neither reaches the text.
        Assert.True(Draw(Probe(Format(-12345678.25, 2, group: true), "-12,345,678.25", "hi-IN"), German).Matches);
        Assert.True(Draw(Probe(Format(-1234, 0, group: true), "-1.234", "de-DE"), American).Matches);
    }

    [Fact]
    public void FormatTime_names_follow_the_profiles_locale_unless_it_gives_its_own()
    {
        var wednesday = new DateTimeOffset(2026, 1, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.True(Draw(Probe(FormatTime("EEEE"), "Mittwoch", "de-DE"), American, wednesday).Matches,
            "formatTime EEEE without a locale under profile.locale de-DE should be Mittwoch.");
        Assert.True(Draw(Probe(FormatTime("EEEE"), "Wednesday", locale: null), American, wednesday).Matches);
        Assert.True(Draw(Probe(FormatTime("EEEE"), "Mittwoch", locale: null), German, wednesday).Matches);
        Assert.True(Draw(Probe(FormatTime("EEEE", "en-GB"), "Wednesday", "de-DE"), German, wednesday).Matches);
    }

    [Fact]
    public void An_unknown_profile_locale_draws_in_the_PCs_format_and_is_logged_once()
    {
        var events = new List<LogEvent>();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        using var runtime = new SceneRuntime(SceneKit.Options(locale: German, logger: logger));

        var unknown = Probe(Format(1234.5, 1, group: true), "1.234,5", "zz");
        runtime.Load(unknown);
        runtime.Load(Probe(Format(1234.5, 1, group: true), "1.234,5", "zz"));
        using (var snapshot = runtime.Snapshot(SceneKit.Now))
        {
            using var bitmap = snapshot.Render();
            Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(1, 1));
            Assert.Same(German, snapshot.Clock.Format);
        }
        var warning = Assert.Single(events);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Contains("'zz'", warning.RenderMessage());

        // Known again, then unknown again: not reported again (the report is kept for the process, not the runtime).
        runtime.Load(Probe(Format(1, 0, false), "1", "de-DE"));
        runtime.Load(unknown);
        Assert.Single(events);
    }

    [Fact]
    public void An_unknown_profile_locale_is_logged_once_per_process_not_once_per_runtime()
    {
        var events = new List<LogEvent>();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        // A thumbnail with no live loop is drawn by a fresh runtime each time.
        for (var i = 0; i < 3; i++)
        {
            using var runtime = new SceneRuntime(SceneKit.Options(locale: German, logger: logger));
            runtime.Load(Probe(Format(1234.5, 1, group: true), "1.234,5", "zy"));
        }
        Assert.Contains("'zy'", Assert.Single(events).RenderMessage());
    }

    [Fact]
    public void The_locale_cache_and_the_reported_tags_stay_within_their_bound()
    {
        for (var i = 0; i < 5000; i++)
        {
            Assert.Null(RegionalFormat.Lookup($"zq-{i:D8}"));
            RegionalFormat.FirstReport($"zq-{i:D8}");
        }
        Assert.InRange(RegionalFormat.CachedCount, 1, RegionalFormat.CacheLimit);
        Assert.InRange(RegionalFormat.ReportedCount, 1, RegionalFormat.CacheLimit);
        Assert.True(RegionalFormat.CacheLimit <= 1024);
        // Known tags still resolve after the flood.
        Assert.Equal("de-DE", RegionalFormat.Lookup("de-DE")!.Tag);
    }

    [Fact]
    public void A_captured_culture_is_a_frozen_copy()
    {
        var writable = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
        Assert.False(writable.IsReadOnly);
        var format = RegionalFormat.Of(writable);
        Assert.True(format.Culture.IsReadOnly);

        var before = SceneExpressionTests.Eval("{'op':'formatTime','pattern':'a'}", format: format).Value.Text;
        writable.DateTimeFormat.AMDesignator = "changed";
        writable.DateTimeFormat.PMDesignator = "changed";
        writable.NumberFormat.NumberDecimalSeparator = "!";
        Assert.Equal(before, SceneExpressionTests.Eval("{'op':'formatTime','pattern':'a'}", format: format).Value.Text);
        Assert.NotEqual("changed", format.Culture.DateTimeFormat.AMDesignator);
        Assert.NotEqual("changed", format.Culture.DateTimeFormat.PMDesignator);
        Assert.Equal(",", format.DecimalSeparator);
    }

    [Fact]
    public void Nothing_reads_the_process_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.True(Draw(Probe(Format(1234.5, 1, group: true), "1,234.5", locale: null), American).Matches);
            Assert.True(Draw(Probe(FormatTime("EEEE MMMM"), "Friday January", locale: null), American).Matches);
            var (value, _, _) = SceneExpressionTests.Eval("{'op':'format','value':1234.5,'decimals':1,'group':true}", format: American);
            Assert.Equal("1,234.5", value.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_takes_absent_separators_from_the_ticks_format()
    {
        Assert.Equal("1.234.567,9", SceneExpressionTests.Eval("{'op':'format','value':1234567.891,'decimals':1,'group':true}", format: German).Value.Text);
        Assert.Equal("1,234,567.9", SceneExpressionTests.Eval("{'op':'format','value':1234567.891,'decimals':1,'group':true}").Value.Text);
        Assert.Equal("Freitag", SceneExpressionTests.Eval("{'op':'formatTime','pattern':'EEEE'}", format: German).Value.Text);
    }

    [Fact]
    public void A_tag_is_looked_up_without_user_overrides_and_an_unknown_one_is_null()
    {
        Assert.Equal(("de-DE", ",", "."), (German.Tag, German.DecimalSeparator, German.GroupSeparator));
        Assert.False(German.Culture.UseUserOverride);
        Assert.Equal(("fr-FR", ",", " "), (RegionalFormat.Lookup("fr-FR")!.Tag, RegionalFormat.Lookup("fr-FR")!.DecimalSeparator, RegionalFormat.Lookup("fr-FR")!.GroupSeparator));
        Assert.Null(RegionalFormat.Lookup("zz"));
        Assert.Null(RegionalFormat.Lookup("en-ZZ"));
        Assert.Equal(("und", ".", ","), (RegionalFormat.Invariant.Tag, RegionalFormat.Invariant.DecimalSeparator, RegionalFormat.Invariant.GroupSeparator));
    }

    [Fact]
    public void A_separator_out_of_bounds_is_replaced_by_the_cultures_own_then_the_invariant_one()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var f = RegionalFormat.Create(de, "", "12345");
        Assert.Equal((",", "."), (f.DecimalSeparator, f.GroupSeparator));

        f = RegionalFormat.Create(de, "ab", "\U0001D7D8\U0001D7D8\U0001D7D8\U0001D7D8");
        Assert.Equal(("ab", "\U0001D7D8\U0001D7D8\U0001D7D8\U0001D7D8"), (f.DecimalSeparator, f.GroupSeparator));
        f = RegionalFormat.Create(de, null, "\U0001D7D8\U0001D7D8\U0001D7D8\U0001D7D8\U0001D7D8");
        Assert.Equal((",", "."), (f.DecimalSeparator, f.GroupSeparator));

        var broken = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        broken.NumberFormat.NumberDecimalSeparator = "12345";
        broken.NumberFormat.NumberGroupSeparator = "";
        f = RegionalFormat.Of(broken);
        Assert.Equal((".", ",", "und"), (f.DecimalSeparator, f.GroupSeparator, f.Tag));

        Assert.True(RegionalFormat.Fits("1234"));
        Assert.False(RegionalFormat.Fits("12345"));
        Assert.False(RegionalFormat.Fits(""));
        Assert.False(RegionalFormat.Fits(null));
    }

    [Theory]
    [InlineData("en_US@rg=dezzzz", "en-US")]
    [InlineData("en_SG@calendar=japanese", "en-SG")]
    [InlineData("ar_EG@numbers=latn", "ar-EG")]
    [InlineData("zh_SG", "zh-SG")]
    [InlineData("de_DE", "de-DE")]
    [InlineData("en_001", "en-001")]
    [InlineData("en_ZZ", "en")]
    [InlineData("zz_ZZ", "und")]
    [InlineData("", "und")]
    public void A_CoreFoundation_identifier_maps_to_a_tag(string identifier, string tag)
    {
        var culture = PcRegionalFormat.CultureOf(identifier);
        Assert.Equal(tag, RegionalFormat.Of(culture).Tag);
        Assert.False(culture.UseUserOverride);
    }

    [Theory]
    [InlineData("de_DE", "de-DE", ",", ".")]
    [InlineData("en_US@rg=dezzzz", "en-US", ",", ".")]
    [InlineData("ar_EG@numbers=latn", "ar-EG", ".", ",")]
    [InlineData("en_SG", "en-SG", ".", ",")]
    public void CoreFoundation_gives_the_separators_of_the_region_and_number_format(string identifier, string tag, string decimalSeparator, string groupSeparator)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CoreFoundation is macOS's.");
        if (!OperatingSystem.IsMacOS()) return;
        var f = PcRegionalFormat.MacLocale.Create(identifier)!;
        Assert.Equal((tag, decimalSeparator, groupSeparator), (f.Tag, f.DecimalSeparator, f.GroupSeparator));
    }

    [Fact]
    public void The_PCs_format_is_read_and_fits_the_bounds()
    {
        var f = PcRegionalFormat.Read();
        Assert.True(RegionalFormat.Fits(f.DecimalSeparator) && RegionalFormat.Fits(f.GroupSeparator), f.ToString());
        Assert.Matches("^(und|[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,4})$", f.Tag);
        Assert.Same(PcRegionalFormat.Current, PcRegionalFormat.Current);
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}

/// <summary>
/// One scene, with explicit separators beside the regional ones, drawn with the PC's format de-DE and with the profile's
/// <c>locale</c> fr-FR (U+202F as its group separator, in the bundled Inter): <c>Golden/scene/{os}/regional-*.png</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class RegionalFormatGoldenTests
{
    private const string Scene = """
        {
          "format": "infopanel.scene/1",
          "values": {
            "pc": {"op": "format", "value": 1234567.5, "decimals": 1, "group": true},
            "explicit": {"op": "format", "value": 1234567.5, "decimals": 1, "group": true, "decimalSeparator": "·", "groupSeparator": "'"},
            "day": {"op": "formatTime", "pattern": "EEEE d MMMM"}
          },
          "nodes": [
            {
              "kind": "text",
              "font": {"family": "Inter", "size": 20},
              "fill": "#e5e7eb",
              "text": [
                {"text": {"op": "value", "name": "pc"}},
                {"text": "  "},
                {"text": {"op": "value", "name": "explicit"}},
                {"text": "  "},
                {"text": {"op": "value", "name": "day"}}
              ]
            }
          ]
        }
        """;

    public static TheoryData<string, string?> Cases() => new()
    {
        { "regional-pc-de", null },
        { "regional-profile-fr", "fr-FR" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string name, string? locale)
    {
        using var scene = JsonDocument.Parse(Scene);
        var doc = RegionalFormatTests.WithLocale(SceneKit.Doc(scene.RootElement.Clone(), 460, 40, 8, 8), locale);
        using var runtime = new SceneRuntime(SceneKit.Options(locale: RegionalFormat.Lookup("de-DE")));
        runtime.Load(doc);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        using var bitmap = snapshot.Render();
        GoldenImage.Verify(name, bitmap, "scene");
    }
}
