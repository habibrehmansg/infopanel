using System.Globalization;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>Every expression operation and type (spec/docs/expressions.md), evaluated by the runtime on fixed inputs.</summary>
public sealed class SceneExpressionTests
{
    private const string Inputs = "{'v':{'type':'number'},'s':{'type':'text'},'t':{'type':'table'},'u':{'type':'number'}}";

    internal static (Val Value, Ty Type, ItemDiagnostics Diagnostics) Eval(string expr, Action<FakeSceneInputs>? setup = null, double elapsed = 12.5,
        RegionalFormat? format = null) =>
        EvalJson(expr.Replace('\'', '"'), setup, elapsed, format);

    /// <summary>An expression written as JSON (no quote replacement), evaluated as the named value <c>x</c> of a scene.</summary>
    /// <summary>The same, with the tick's regional format <paramref name="format"/> (the invariant one when null).</summary>
    internal static (Val Value, Ty Type, ItemDiagnostics Diagnostics) EvalJson(string expr, Action<FakeSceneInputs>? setup = null, double elapsed = 12.5,
        RegionalFormat? format = null)
    {
        var json = SceneKit.Json("{'format':'infopanel.scene/1','inputs':" + Inputs + ",'values':{'x':" + expr.Replace("'", "\\u0027") + "},'nodes':[]}");
        var check = SceneValidator.Check(json);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var scene = CompiledScene.Compile(check.Scene!, check.Cost, SpecLimits.Default);
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 42.5, "%", 3, 97, 38);
        fake.Text("test:s", "Hello");
        fake.Histories["test:v"] = [1, 2, 3, 4, 10];
        setup?.Invoke(fake);
        var snaps = scene.InputNames.Select(n => n == "u" ? InputSnap.Unbound
            : new InputSnap(fake.Read("test:" + n, scene.InputTypes[Array.IndexOf(scene.InputNames, n)]), n == "v" ? "max" : "now",
                fake.Histories.TryGetValue("test:" + n, out var h) ? h : [])).ToArray();
        var diag = new ItemDiagnostics("item");
        var e = new Ev
        {
            Values = new Val[scene.ValueCount],
            State = [],
            Inputs = snaps,
            Clock = new SceneClock(elapsed, SceneKit.Now, TimeZoneInfo.Utc, format ?? RegionalFormat.Invariant),
            MeasureW = [],
            MeasureH = [],
            BoxW = 200,
            BoxH = 100,
            Diagnostics = diag,
        };
        foreach (var i in scene.PhaseBeforeState) e.Values[i] = scene.Values[i].Eval(e);
        return (e.Values[0], scene.Values[0].Type, diag);
    }

    private static double Num(string expr) => Eval(expr).Value.N;
    private static string Str(string expr) => Eval(expr).Value.Text;
    private static bool Bool(string expr) => Eval(expr).Value.B;
    private static string Color(string expr) => SceneColor.Format(Eval(expr).Value.C);

    [Theory]
    [InlineData("{'op':'add','args':[1,2,3.5]}", 6.5)]
    [InlineData("{'op':'mul','args':[2,3,4]}", 24)]
    [InlineData("{'op':'min','args':[3,-1,2]}", -1)]
    [InlineData("{'op':'max','args':[3,-1,2]}", 3)]
    [InlineData("{'op':'sub','args':[1,3]}", -2)]
    [InlineData("{'op':'div','args':[1,4]}", 0.25)]
    [InlineData("{'op':'div','args':[1,0]}", 0)]
    [InlineData("{'op':'mod','args':[-7,3]}", 2)]
    [InlineData("{'op':'mod','args':[7,-3]}", -2)]
    [InlineData("{'op':'mod','args':[7,0]}", 0)]
    [InlineData("{'op':'pow','args':[2,10]}", 1024)]
    [InlineData("{'op':'pow','args':[-8,0.5]}", 0)]
    [InlineData("{'op':'atan2','args':[1,1]}", 45)]
    [InlineData("{'op':'atan2','args':[1,0]}", 90)]
    // A literal -0 reads as 0 (spec/docs/validation.md#numbers): the angle of (-1, 0) is 180, not -180.
    [InlineData("{'op':'atan2','args':[-0,-1]}", 180)]
    [InlineData("{'op':'atan2','args':[-0.0e3,-1]}", 180)]
    // No result is negative zero (spec/docs/expressions.md, "Numbers and strings"): mul(-1, 0) is 0, so the angle is 180.
    [InlineData("{'op':'atan2','args':[{'op':'mul','args':[-1,0]},-1]}", 180)]
    [InlineData("{'op':'atan2','args':[{'op':'neg','args':[0]},-1]}", 180)]
    [InlineData("{'op':'mul','args':[-1,0]}", 0)]
    [InlineData("{'op':'neg','args':[0]}", 0)]
    [InlineData("{'op':'ceil','args':[-0.5]}", 0)]
    [InlineData("{'op':'round','args':[-0.4]}", 0)]
    [InlineData("{'op':'sqrt','args':[{'op':'neg','args':[0]}]}", 0)]
    [InlineData("{'op':'steps','value':-5,'at':[0],'out':[{'op':'mul','args':[-1,0]},1]}", 0)]
    [InlineData("{'op':'neg','args':[2]}", -2)]
    [InlineData("{'op':'abs','args':[-2]}", 2)]
    [InlineData("{'op':'floor','args':[-1.5]}", -2)]
    [InlineData("{'op':'ceil','args':[-1.5]}", -1)]
    [InlineData("{'op':'round','args':[-2.5]}", -3)]
    [InlineData("{'op':'round','args':[2.5]}", 3)]
    [InlineData("{'op':'round','args':[0.49999]}", 0)]
    [InlineData("{'op':'sqrt','args':[16]}", 4)]
    [InlineData("{'op':'sqrt','args':[-1]}", 0)]
    [InlineData("{'op':'exp','args':[0]}", 1)]
    [InlineData("{'op':'exp','args':[1000]}", 0)]
    [InlineData("{'op':'log','args':[0]}", 0)]
    [InlineData("{'op':'sin','args':[90]}", 1)]
    [InlineData("{'op':'sin','args':[180]}", 0)]
    [InlineData("{'op':'cos','args':[90]}", 0)]
    [InlineData("{'op':'cos','args':[-360]}", 1)]
    [InlineData("{'op':'clamp','args':[5,0,3]}", 3)]
    [InlineData("{'op':'clamp','args':[-5,0,3]}", 0)]
    [InlineData("{'op':'norm','args':[5,0,10]}", 0.5)]
    [InlineData("{'op':'norm','args':[5,2,2]}", 0)]
    [InlineData("{'op':'norm','args':[20,0,10]}", 2)]
    [InlineData("{'op':'lerp','args':[0,10,0.25]}", 2.5)]
    [InlineData("{'op':'lerp','args':[0,10,2]}", 20)]
    [InlineData("{'op':'mul','args':[1e300,1e300]}", 0)]
    [InlineData("{'op':'steps','value':50,'at':[25,75],'out':[0,1,2]}", 1)]
    [InlineData("{'op':'steps','value':75,'at':[25,75],'out':[0,1,2]}", 2)]
    [InlineData("{'op':'steps','value':-1,'at':[25,75],'out':[0,1,2]}", 0)]
    [InlineData("{'op':'if','args':[true,1,2]}", 1)]
    [InlineData("{'op':'if','args':[false,1,2]}", 2)]
    [InlineData("{'op':'box','field':'w'}", 200)]
    [InlineData("{'op':'box','field':'h'}", 100)]
    [InlineData("{'op':'time','field':'elapsed'}", 12.5)]
    [InlineData("{'op':'time','field':'epoch'}", 1767323045)]
    [InlineData("{'op':'time','field':'dayTime'}", 11045)]
    [InlineData("{'op':'time','field':'year'}", 2026)]
    [InlineData("{'op':'time','field':'month'}", 1)]
    [InlineData("{'op':'time','field':'day'}", 2)]
    [InlineData("{'op':'time','field':'weekday'}", 5)]
    [InlineData("{'op':'time','field':'hour'}", 3)]
    [InlineData("{'op':'time','field':'hour','zone':'Europe/Berlin'}", 4)]
    [InlineData("{'op':'time','field':'hour','zone':'America/New_York'}", 22)]
    [InlineData("{'op':'time','field':'minute'}", 4)]
    [InlineData("{'op':'time','field':'second'}", 5)]
    [InlineData("{'op':'time','field':'millisecond'}", 0)]
    [InlineData("{'op':'input','name':'v'}", 97)]
    [InlineData("{'op':'input','name':'v','field':'now'}", 42.5)]
    [InlineData("{'op':'input','name':'v','field':'min'}", 3)]
    [InlineData("{'op':'input','name':'v','field':'avg'}", 38)]
    [InlineData("{'op':'input','name':'u'}", 0)]
    [InlineData("{'op':'history','input':'v','fn':'min','count':3}", 3)]
    [InlineData("{'op':'history','input':'v','fn':'max','count':3}", 10)]
    [InlineData("{'op':'history','input':'v','fn':'avg','count':2}", 7)]
    [InlineData("{'op':'history','input':'v','fn':'first','count':4}", 2)]
    [InlineData("{'op':'history','input':'v','fn':'last','count':4}", 10)]
    [InlineData("{'op':'history','input':'v','fn':'count','count':100}", 5)]
    [InlineData("{'op':'history','input':'u','fn':'avg','count':100}", 0)]
    public void Numbers(string expr, double expected) => AssertExact(expected, Num(expr), expr);

    /// <summary>The same double, bit for bit: exact (no rounding to some places) and with the same sign of zero.</summary>
    internal static void AssertExact(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R} ({BitConverter.DoubleToInt64Bits(expected):x16}), got {actual:R} ({BitConverter.DoubleToInt64Bits(actual):x16})");

    [Fact]
    public void A_negative_zero_input_reading_reads_as_zero()
    {
        var (v, _, _) = Eval("{'op':'atan2','args':[{'op':'input','name':'v','field':'now'},-1]}", f => f.Number("test:v", -0.0, "%", 0, 0, 0));
        AssertExact(180, v.N, "atan2(input -0, -1)");
    }

    [Theory]
    [InlineData("{'op':'lt','args':[1,2]}", true)]
    [InlineData("{'op':'le','args':[2,2]}", true)]
    [InlineData("{'op':'gt','args':[2,2]}", false)]
    [InlineData("{'op':'ge','args':[2,2]}", true)]
    [InlineData("{'op':'eq','args':[0.1,0.1]}", true)]
    [InlineData("{'op':'eq','args':[{'op':'add','args':[0.1,0.2]},0.3]}", false)]
    [InlineData("{'op':'eq','args':['a','A']}", false)]
    [InlineData("{'op':'ne','args':['a','b']}", true)]
    [InlineData("{'op':'eq','args':[true,true]}", true)]
    [InlineData("{'op':'eq','args':[{'op':'rgba','args':[255,0,0,1]},'#ff0000']}", true)]
    [InlineData("{'op':'eq','args':[{'op':'rgba','args':[255,0,0,1]},'#FF0000FF']}", true)]
    [InlineData("{'op':'eq','args':[{'op':'rgba','args':[255,0,0,0.5]},'#ff0000']}", false)]
    [InlineData("{'op':'and','args':[true,true,false]}", false)]
    [InlineData("{'op':'or','args':[false,false,true]}", true)]
    [InlineData("{'op':'not','args':[true]}", false)]
    [InlineData("{'op':'input','name':'v','field':'available'}", true)]
    [InlineData("{'op':'input','name':'u','field':'available'}", false)]
    // steps has an output of each of the four types, bool too.
    [InlineData("{'op':'steps','value':1,'at':[1],'out':[false,true]}", true)]
    [InlineData("{'op':'steps','value':0,'at':[1],'out':[false,true]}", false)]
    [InlineData("{'op':'steps','value':5,'at':[1,9],'out':[true,{'op':'lt','args':[2,1]},true]}", false)]
    [InlineData("{'op':'eq','args':[{'op':'input','name':'v','field':'status'},'live']}", true)]
    [InlineData("{'op':'eq','args':[{'op':'input','name':'u','field':'status'},'unbound']}", true)]
    [InlineData("{'op':'eq','args':[{'op':'input','name':'t','field':'status'},'unavailable']}", true)]
    public void Bools(string expr, bool expected) => Assert.Equal(expected, Bool(expr));

    [Theory]
    [InlineData("{'op':'concat','args':['a','b','c']}", "abc")]
    [InlineData("{'op':'upper','args':['straße ıi']}", "STRAßE ıI")]
    [InlineData("{'op':'lower','args':['ÀB']}", "àb")]
    [InlineData("{'op':'format','value':1234567.891,'decimals':2,'group':true}", "1,234,567.89")]
    [InlineData("{'op':'format','value':1234567.891,'decimals':1,'group':true,'decimalSeparator':',','groupSeparator':'.'}", "1.234.567,9")]
    [InlineData("{'op':'format','value':-0.4}", "0")]
    [InlineData("{'op':'format','value':2.5}", "3")]
    [InlineData("{'op':'format','value':-2.5}", "-3")]
    [InlineData("{'op':'format','value':-1234,'group':true}", "-1,234")]
    [InlineData("{'op':'format','value':0.125,'decimals':{'op':'add','args':[6,3]}}", "0.125000")]
    [InlineData("{'op':'format','value':{'op':'div','args':[1,0]},'decimals':1}", "0.0")]
    [InlineData("{'op':'formatTime','pattern':'yyyy-MM-dd HH:mm:ss.SSS'}", "2026-01-02 03:04:05.000")]
    [InlineData("{'op':'formatTime','pattern':'y yy M MM d dd H h hh m mm s ss S SS a','locale':'en-US'}", "2026 26 1 01 2 02 3 3 03 4 04 5 05 0 00 AM")]
    [InlineData("{'op':'formatTime','pattern':'EEE EEEE MMM MMMM','locale':'en-GB'}", "Fri Friday Jan January")]
    [InlineData("{'op':'formatTime','pattern':'EEEE d. MMMM','locale':'de-DE','zone':'Europe/Berlin'}", "Freitag 2. Januar")]
    [InlineData("{'op':'input','name':'v','field':'unit'}", "%")]
    [InlineData("{'op':'input','name':'s','field':'text'}", "Hello")]
    [InlineData("{'op':'input','name':'u','field':'status'}", "unbound")]
    [InlineData("{'op':'steps','value':80,'at':[25,75],'out':['low','mid','high']}", "high")]
    [InlineData("{'op':'if','args':[{'op':'input','name':'u','field':'available'},'yes','-']}", "-")]
    public void Strings(string expr, string expected) => Assert.Equal(expected, Str(expr));

    [Theory]
    [InlineData("'#ff8000'", "#ff8000ff")]
    [InlineData("{'op':'rgba','args':[300,-5,127.5,2]}", "#ff0080ff")]
    [InlineData("{'op':'rgba','args':[0,0,0,0.5]}", "#00000080")]
    [InlineData("{'op':'alpha','args':['#ff0000',0.25]}", "#ff000040")]
    [InlineData("{'op':'lerp','args':['#000000','#ffffff',0.5]}", "#808080ff")]
    [InlineData("{'op':'lerp','args':['#000000','#ffffff',5]}", "#ffffffff")]
    [InlineData("{'op':'lerp','args':['#ff000000','#0000ffff',0.5]}", "#80008080")]
    [InlineData("{'op':'steps','value':80,'at':[50],'out':['#22c55e',{'op':'rgba','args':[1,2,3,1]}]}", "#010203ff")]
    [InlineData("{'op':'if','args':[false,'#ff0000',{'op':'rgba','args':[0,0,255,1]}]}", "#0000ffff")]
    public void Colors(string expr, string expected) => Assert.Equal(expected, Color(expr));

    // format of a number given as text (spec/docs/expressions.md, "Number formatting"): a plain decimal keeps its own
    // digits and takes the separators; any other string comes back unchanged.
    [Theory]
    [InlineData("{'op':'format','value':'1234567.5','group':true}", "en-US", "1,234,567.5")]
    [InlineData("{'op':'format','value':'1234567.5','group':true}", "de-DE", "1.234.567,5")]
    [InlineData("{'op':'format','value':'1234567.5'}", "de-DE", "1234567,5")]
    [InlineData("{'op':'format','value':'1234567','group':true,'groupSeparator':'_'}", "de-DE", "1_234_567")]
    [InlineData("{'op':'format','value':'-0.50','group':true}", "en-US", "-0.50")]
    [InlineData("{'op':'format','value':'-0.50'}", "de-DE", "-0,50")]
    [InlineData("{'op':'format','value':'-1234.000','group':true,'decimals':1}", "de-DE", "-1.234,000")]
    [InlineData("{'op':'format','value':'0001234','group':true}", "en-US", "0,001,234")]
    [InlineData("{'op':'format','value':'123','group':true}", "en-US", "123")]
    [InlineData("{'op':'format','value':'99999999999999999999.5','group':true}", "en-US", "99,999,999,999,999,999,999.5")]
    [InlineData("{'op':'format','value':'abc','group':true}", "de-DE", "abc")]
    [InlineData("{'op':'format','value':'1e3','group':true}", "de-DE", "1e3")]
    [InlineData("{'op':'format','value':'1,234','group':true}", "de-DE", "1,234")]
    [InlineData("{'op':'format','value':' 1234','group':true}", "de-DE", " 1234")]
    [InlineData("{'op':'format','value':'1234.','group':true}", "de-DE", "1234.")]
    [InlineData("{'op':'format','value':'.5'}", "de-DE", ".5")]
    [InlineData("{'op':'format','value':'+1234','group':true}", "de-DE", "+1234")]
    [InlineData("{'op':'format','value':'-.5'}", "de-DE", "-.5")]
    [InlineData("{'op':'format','value':'-'}", "de-DE", "-")]
    [InlineData("{'op':'format','value':''}", "de-DE", "")]
    [InlineData("{'op':'format','value':'١٢٣٤','group':true}", "de-DE", "١٢٣٤")]
    [InlineData("{'op':'format','value':{'op':'input','name':'s','field':'text'},'group':true}", "de-DE", "Hello")]
    public void Format_writes_a_number_given_as_text(string expr, string locale, string expected) =>
        Assert.Equal(expected, Eval(expr, format: RegionalFormat.Lookup(locale)).Value.Text);

    [Fact]
    public void Format_groups_a_text_reading_that_is_a_number()
    {
        var (v, t, _) = Eval("{'op':'format','value':{'op':'input','name':'s','field':'text'},'group':true}",
            f => f.Text("test:s", "1234567.25"), format: RegionalFormat.Lookup("de-DE"));
        Assert.Equal(("1.234.567,25", Ty.String), (v.Text, t));
        // A reading that isn't a plain decimal is drawn as sent.
        Assert.Equal("1234 MHz", Eval("{'op':'format','value':{'op':'input','name':'s','field':'text'},'group':true}",
            f => f.Text("test:s", "1234 MHz"), format: RegionalFormat.Lookup("de-DE")).Value.Text);
    }

    [Fact]
    public void Format_takes_a_number_or_a_string_and_nothing_else()
    {
        static IReadOnlyList<(string Code, string Path)> Errors(string values)
        {
            var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','values':" + values + ",'nodes':[]}"));
            return check.Errors.Select(e => (e.Code, e.Path)).ToList();
        }

        Assert.Empty(Errors("{'x':{'op':'format','value':'1234'}}"));
        Assert.Empty(Errors("{'s':'1234','x':{'op':'format','value':{'op':'value','name':'s'}}}"));
        Assert.Equal([("type.mismatch", "/values/x/value")], Errors("{'b':{'op':'lt','args':[1,2]},'x':{'op':'format','value':{'op':'value','name':'b'}}}"));
        Assert.Equal([("type.mismatch", "/values/x/value")], Errors("{'c':{'op':'rgba','args':[1,2,3,1]},'x':{'op':'format','value':{'op':'value','name':'c'}}}"));
    }

    [Fact]
    public void Format_of_text_is_cut_to_4096_scalar_values()
    {
        var digits = new string('9', 4000);
        var text = Eval($"{{'op':'format','value':'{digits}','group':true}}", format: RegionalFormat.Lookup("en-US")).Value.Text;
        Assert.Equal(4096, text.Length);
        Assert.StartsWith("9,999,", text);
    }

    [Fact]
    public void Types_are_inferred_as_the_validator_does()
    {
        Assert.Equal(Ty.String | Ty.Color, Eval("'#ff0000'").Type);
        Assert.Equal(Ty.String, Eval("'red'").Type);
        Assert.Equal(Ty.Color, Eval("{'op':'lerp','args':['#000000','#ffffff',0.5]}").Type);
        Assert.Equal(Ty.Number, Eval("{'op':'lerp','args':[0,1,0.5]}").Type);
        Assert.Equal(Ty.String | Ty.Color, Eval("{'op':'if','args':[true,'#000000','#ffffff']}").Type);
    }

    [Fact]
    public void Every_string_result_is_cut_to_4096_scalar_values_at_the_operation()
    {
        var a = new string('a', 4096);
        var b = new string('b', 4096);
        Assert.Equal(a, Str($"{{'op':'concat','args':['{a}','{b}']}}"));
        // Comparisons see the cut strings.
        Assert.True(Bool($"{{'op':'eq','args':[{{'op':'concat','args':['{a}','x']}},'{a}']}}"));
        // A scalar value outside the BMP counts once.
        var emoji = string.Concat(Enumerable.Repeat("😀", 4096));
        Assert.Equal(emoji, Str($"{{'op':'concat','args':['{emoji}','{emoji}']}}"));
        Assert.Equal(4096 * 2, Str($"{{'op':'upper','args':[{{'op':'concat','args':['{emoji}','z']}}]}}").Length);
        var long1 = new string('x', 5000);
        Assert.Equal(4096, Str("{'op':'input','name':'s','field':'text'}".Replace("'s'", "'s'"), long1).Length);
    }

    private static string Str(string expr, string text) => Eval(expr, f => f.Text("test:s", text)).Value.Text;

    [Fact]
    public void Quoted_text_in_time_patterns_is_literal()
    {
        var tokens = SceneTime.ParsePattern("HH 'o''clock' '' h:mm a");
        Assert.Equal("15 o'clock ' 3:04 PM", SceneTime.Format(tokens, new DateTime(2026, 1, 2, 15, 4, 5), CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Unknown_zone_and_locale_fall_back_and_are_reported()
    {
        var (v, _, diag) = Eval("{'op':'time','field':'hour','zone':'Etc/Nowhere'}");
        Assert.Equal(3, v.N);
        Assert.Contains(diag.Items, d => d.Code == "time.zone");
        var (s, _, diag2) = Eval("{'op':'formatTime','pattern':'MMM','locale':'qx-QX'}");
        Assert.Equal("Jan", s.Text);
        Assert.Contains(diag2.Items, d => d.Code == "time.locale");
    }

    [Fact]
    public void Inputs_without_readings_read_zero_and_empty()
    {
        var (v, _, _) = Eval("{'op':'input','name':'v','field':'now'}", f => f.Readings.Remove("test:v"));
        Assert.Equal(0, v.N);
        var (u, _, _) = Eval("{'op':'input','name':'v','field':'unit'}", f => f.Readings.Remove("test:v"));
        Assert.Equal("", u.Text);
        var (t, _, _) = Eval("{'op':'input','name':'t','field':'text'}");
        Assert.Equal("", t.Text);
    }

    [Fact]
    public void String_operations_count_16_units_of_work()
    {
        var json = SceneKit.Json("{'format':'infopanel.scene/1','values':{'x':{'op':'concat','args':['a']},'y':{'op':'add','args':[1,2]}},'nodes':[]}");
        var check = SceneValidator.Check(json);
        var scene = CompiledScene.Compile(check.Scene!, check.Cost, SpecLimits.Default);
        var e = new Ev { Values = new Val[2], State = [], Inputs = [], Clock = SceneClock.At(0, SceneKit.Now), MeasureW = [], MeasureH = [] };
        foreach (var i in scene.PhaseBeforeState) e.Values[i] = scene.Values[i].Eval(e);
        Assert.Equal(17, e.Work);
        Assert.Equal(check.Cost.Work, e.Work);
    }

    [Fact]
    public void Canonical_json_numbers_follow_ecmascript()
    {
        Assert.Equal("1e+21", CanonicalJson.Number(1e21));
        Assert.Equal("100000000000000000000", CanonicalJson.Number(1e20));
        Assert.Equal("0.000001", CanonicalJson.Number(1e-6));
        Assert.Equal("1e-7", CanonicalJson.Number(1e-7));
        Assert.Equal("-1.5", CanonicalJson.Number(-1.5));
        Assert.Equal("60", CanonicalJson.Number(60.0));
        Assert.Equal("0.1", CanonicalJson.Number(0.1));
        Assert.Equal("{\"a\":[1,\"x\"],\"b\":true}", CanonicalJson.Write(SceneKit.Json("{ 'b' : true, 'a': [1.0, 'x'] }")));
    }
}
