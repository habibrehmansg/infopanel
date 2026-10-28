using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// The validators run on untrusted imports and commits: each hostile shape here is checked on its own thread with a 1 MiB
/// stack (the smallest default an engine thread gets), within a time limit and a bounded allocation measured on that thread.
/// </summary>
public class HostileInputTests
{
    /// <summary>What a check may allocate in all (garbage included), counted on the thread that runs it.</summary>
    private const long MaxAllocated = 1L << 30;

    static HostileInputTests()
    {
        // Load the schemas (and JIT the validators) outside the measured runs.
        SceneValidator.Check(Parse("""{"format": "infopanel.scene/1", "nodes": []}"""));
        CommitValidator.Check(Parse("""{"opId": "a", "instance": "i", "baseRev": 0, "ops": [{"op": "remove", "id": "x"}]}"""));
        DocumentValidator.Check(Parse("""{"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "profile": {"name": "P", "width": 1, "height": 1}, "items": []}"""));
        MediaValidator.Check("<svg xmlns='http://www.w3.org/2000/svg'/>"u8);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement;

    /// <summary>
    /// The calibration workload: a fixed, ordinary scene validated <see cref="BaselineRuns"/> times. Its time on the check's
    /// own thread, just before the check, is the unit the time bounds are measured in, so a machine that is slow or busy
    /// (the full suite running in parallel) slows both alike and the bound keeps its meaning.
    /// </summary>
    private static readonly JsonElement BaselineScene = Parse(
        """{"format": "infopanel.scene/1", "nodes": [""" +
        string.Join(", ", Enumerable.Range(0, 40).Select(i => $$"""{"kind": "rect", "id": "r{{i}}", "w": {{i + 1}}, "h": 10, "fill": "#336699"}""")) + "]}");

    private const int BaselineRuns = 20;

    /// <summary>
    /// What the calibration workload takes on an idle reference machine (10.4 ms measured on an Apple-silicon laptop running this test alone);
    /// a bound of <c>seconds</c> means <c>seconds / NominalBaseline</c> baselines.
    /// </summary>
    private static readonly TimeSpan NominalBaseline = TimeSpan.FromMilliseconds(NominalBaselineMs);
    internal const double NominalBaselineMs = 10;

    /// <summary>The median of three timings of the calibration workload on the calling thread.</summary>
    private static TimeSpan Baseline()
    {
        var times = new TimeSpan[3];
        for (var k = 0; k < times.Length; k++)
        {
            var clock = Stopwatch.StartNew();
            for (var r = 0; r < BaselineRuns; r++) SceneValidator.Check(BaselineScene);
            times[k] = clock.Elapsed;
        }
        Array.Sort(times);
        return times[1];
    }

    /// <summary>
    /// Runs <paramref name="check"/> on a dedicated background thread with a 1 MiB stack and holds it to
    /// <see cref="MaxAllocated"/> allocated on that thread (independent of load) and to <paramref name="seconds"/> scaled by
    /// the calibration workload's time on the same thread just before (<see cref="Baseline"/>; never below
    /// <paramref name="seconds"/>), so the time bound measures the check's cost, not the machine's load. A check that
    /// hasn't finished within <paramref name="joinTimeout"/> (default: ten times <paramref name="seconds"/>, at least 10 s)
    /// fails the test as a hang; the thread is left behind as a background thread.
    /// </summary>
    private static T Bounded<T>(Func<T> check, double seconds = 1, TimeSpan? joinTimeout = null)
    {
        T result = default!;
        Exception? error = null;
        TimeSpan elapsed = default, baseline = default;
        long allocated = 0;
        var thread = new Thread(() =>
        {
            try
            {
                baseline = Baseline();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                result = check();
                elapsed = clock.Elapsed;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception e)
            {
                error = e;
            }
        }, maxStackSize: 1 << 20) { IsBackground = true, Name = "hostile input check" };
        thread.Start();
        var timeout = joinTimeout ?? TimeSpan.FromSeconds(Math.Max(10, seconds * 10));
        if (!thread.Join(timeout))
            throw new Xunit.Sdk.XunitException($"The check didn't finish within {timeout.TotalMilliseconds:0} ms.");
        if (error is not null) throw new Xunit.Sdk.XunitException("The check threw: " + error);
        Assert.True(allocated < MaxAllocated, $"Allocated {allocated >> 20} MiB.");
        var load = Math.Max(1, baseline / NominalBaseline);
        var limit = TimeSpan.FromSeconds(seconds * load);
        Assert.True(elapsed < limit,
            $"Took {elapsed.TotalMilliseconds:0} ms, {elapsed / baseline:0} baselines of {baseline.TotalMilliseconds:0.0} ms " +
            $"(limit {seconds / NominalBaseline.TotalSeconds:0} baselines, {limit.TotalMilliseconds:0} ms).");
        return result;
    }

    [Fact]
    public void Baseline_IsAValidScene()
    {
        // The calibration workload is ordinary validation, so it slows with load as the checks do.
        Assert.Equal(CheckStatus.Valid, SceneValidator.Check(BaselineScene).Status);
        Assert.True(Baseline() > TimeSpan.Zero);
    }

    [Fact]
    public void Bounded_FailsACheckOverItsTimeInBaselines()
    {
        // 1.5 s of work against a bound of 0.1 s (10 baselines): over it unless the machine is 15 times slower than nominal.
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Bounded(() =>
        {
            var clock = Stopwatch.StartNew();
            long spins = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(1.5)) spins++;
            return spins;
        }, seconds: 0.1));
        Assert.Contains("baselines", failure.Message);
    }

    [Fact]
    public void Bounded_FailsACheckThatDoesntFinish()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => Bounded(() => release.Wait(TimeSpan.FromMinutes(1)), joinTimeout: TimeSpan.FromMilliseconds(200)));
            Assert.Contains("didn't finish", failure.Message);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Bounded_CountsTheAllocationOnTheCheckThread()
    {
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Bounded(() =>
        {
            long total = 0;
            for (var i = 0; i < 1_100; i++) total += new byte[1 << 20].Length; // 1.1 GiB of garbage, never more than 1 MiB alive
            return total;
        }, seconds: 30));
        Assert.Contains("Allocated", failure.Message);
    }

    // ---- 1: chained named values -----------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="count"/> values, each reading the next one (declared after it) under <paramref name="nesting"/>
    /// levels of <c>add</c>; with <paramref name="closeTheCycle"/> the last reads the first. A node reads the first.
    /// </summary>
    private static string ValueChain(int count, int nesting, bool closeTheCycle)
    {
        var sb = new StringBuilder("""{"format": "infopanel.scene/1", "values": {""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var previous = i == count - 1 ? (closeTheCycle ? "v0" : null) : $"v{i + 1}";
            var e = previous is null ? "1" : $$"""{"op": "value", "name": "{{previous}}"}""";
            for (var n = 0; n < nesting; n++) e = $$"""{"op": "add", "args": [{{e}}, 1]}""";
            sb.Append($"\"v{i}\": ").Append(e);
        }
        sb.Append($$"""}, "nodes": [{"kind": "rect", "w": {"op": "value", "name": "v0"}, "h": 1, "fill": "#ffffff"}]}""");
        return sb.ToString();
    }

    [Fact]
    public void ChainedValues_AreCheckedWithoutRecursionThroughReferences()
    {
        // 256 values (the limit), each reading the next one 28 levels down: resolving references by recursion
        // would nest 256 × 28 expression levels.
        var scene = Parse(ValueChain(256, 28, closeTheCycle: false));
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    [Fact]
    public void ChainedDeepValues_AreOneDepthError()
    {
        // The expression depth limit only reports: 256 values each 70 levels deep (within the scene's size, JSON depth
        // and work) would nest 17,920 levels if references were resolved by recursion.
        var scene = Parse(ValueChain(256, 70, closeTheCycle: false));
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal("limit.exprDepth", Assert.Single(check.Errors).Code);
    }

    [Fact]
    public void AChainClosedIntoACycle_IsOneCycleError()
    {
        // Searched from v0: v0 reads v1, ..., v255 reads v0, which closes the cycle.
        var scene = Parse(ValueChain(256, 28, closeTheCycle: true));
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal(("cycle.value", "/values/v255"), (Assert.Single(check.Errors).Code, check.Errors[0].Path.Split("/args")[0]));
    }

    [Fact]
    public void TensOfThousandsOfChainedValues_DontExhaustTheStack()
    {
        // Far past the limits (which a test may raise): still no recursion through references.
        var limits = SpecLimits.Default with { Values = 20_000, SceneBytes = 16 << 20, ExprWork = 1_000_000 };
        var scene = Parse(ValueChain(20_000, 1, closeTheCycle: false));
        var check = Bounded(() => SceneValidator.Check(scene, limits), seconds: 2);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    [Fact]
    public void MoreValuesThanTheLimit_AreRefusedBeforeAnythingReadsThem()
    {
        var scene = Parse(ValueChain(20_000, 1, closeTheCycle: false));
        var check = Bounded(() => SceneValidator.Check(scene, SpecLimits.Default with { SceneBytes = 16 << 20 }));
        var error = Assert.Single(check.Errors);
        Assert.Equal(("limit.values", "/values"), (error.Code, error.Path));
    }

    // ---- 2: unions ---------------------------------------------------------------------------------------------

    [Fact]
    public void NestedUnions_TakeLinearTime()
    {
        // E = if(eq(E', 0), 0, 0), fifteen deep: each level is tried as several expression types.
        var e = "0";
        for (var i = 0; i < 15; i++) e = $$"""{"op": "if", "args": [{"op": "eq", "args": [{{e}}, 0]}, 0, 0]}""";
        var scene = Parse($$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": {{e}}, "h": 1, "fill": "#ffffff"}]}""");
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(x => $"{x.Code} {x.Path} {x.Message}")));
    }

    [Fact]
    public void WorkOverTheBudget_IsOneComplexityError()
    {
        // allOf of the same reference four times, nested: 4^n steps without a budget.
        var set = SchemaSet.Load(["""
            {"$id": "urn:t:bomb", "allOf": [{"$ref": "#/$defs/x"}, {"$ref": "#/$defs/x"}, {"$ref": "#/$defs/x"}, {"$ref": "#/$defs/x"}],
             "$defs": {"x": {"type": "object", "properties": {"x": {"$ref": "urn:t:bomb"}}}}}
            """]);
        var json = new StringBuilder();
        for (var i = 0; i < 30; i++) json.Append("""{"x": """);
        json.Append("{}").Append('}', 30);
        var instance = Parse(json.ToString());
        var result = Bounded(() => set.Validate(instance, "urn:t:bomb"));
        var error = Assert.Single(result.Errors);
        Assert.Equal("complexity", error.Keyword);
    }

    // ---- 3: huge collections -----------------------------------------------------------------------------------

    private static string Repeat(string head, string item, string tail, int bytes)
    {
        var sb = new StringBuilder(bytes + 1024).Append(head);
        var first = true;
        while (sb.Length < bytes - tail.Length - item.Length - 1)
        {
            if (!first) sb.Append(',');
            sb.Append(item);
            first = false;
        }
        return sb.Append(tail).ToString();
    }

    [Fact]
    public void MillionsOfNullOps_AreOneLimitError()
    {
        var commit = Parse(Repeat("""{"opId": "a", "instance": "i", "baseRev": 0, "ops": [""", "null", "]}", 16 << 20));
        var check = Bounded(() => CommitValidator.Check(commit));
        var error = Assert.Single(check.Errors);
        Assert.Equal(("limit.ops", "/ops"), (error.Code, error.Path));
    }

    [Fact]
    public void MillionsOfNullItems_AreOneLimitError()
    {
        var document = Parse(Repeat(
            """{"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "profile": {"name": "P", "width": 1, "height": 1}, "items": [""",
            "null", "]}", 16 << 20));
        var check = Bounded(() => DocumentValidator.Check(document));
        var error = Assert.Single(check.Errors);
        Assert.Equal(("limit.items", "/items"), (error.Code, error.Path));
    }

    [Fact]
    public void MillionsOfBadNodes_AreOneLimitError()
    {
        var scene = Parse(Repeat("""{"format": "infopanel.scene/1", "nodes": [""", "null", "]}", 1 << 20));
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal(("limit.nodes", "/nodes"), (Assert.Single(check.Errors).Code, check.Errors[0].Path));
    }

    [Fact]
    public void ManyErrors_AreCappedWithACount()
    {
        // 1,000 ops within the limit, each with errors: at most 100 are listed, then one "more".
        var commit = Parse(Repeat("""{"opId": "a", "instance": "i", "baseRev": 0, "ops": [""", """{"op": "setPlacement", "id": "x", "placement": {"x": "a", "y": "b"}}""", "]}", 60_000));
        var check = Bounded(() => CommitValidator.Check(commit), seconds: 2);
        Assert.Equal(101, check.Errors.Count);
        Assert.Equal("more", check.Errors[^1].Code);

        var nodes = Parse(Repeat("""{"format": "infopanel.scene/1", "nodes": [""", "1", "]}", 2 << 20));
        var schemaOnly = Bounded(() => SpecSchemas.Strict.Validate(nodes, SpecSchemas.Scene), seconds: 2);
        Assert.Equal(SchemaSet.MaxErrors + 1, schemaOnly.Errors.Count);
        Assert.Equal("more", schemaOnly.Errors[^1].Keyword);
    }

    [Fact]
    public void AnInsertOfMillionsOfChildren_IsOneLimitError()
    {
        var commit = Parse(Repeat("""{"opId": "a", "instance": "i", "baseRev": 0, "ops": [{"op": "insert", "item": {"kind": "group", "id": "g", "placement": {"x": 0, "y": 0}, "children": [""",
            "{}", "]}}]}", 16 << 20));
        var check = Bounded(() => CommitValidator.Check(commit));
        var error = Assert.Single(check.Errors);
        Assert.Equal(("limit.items", "/ops/0/item"), (error.Code, error.Path));
    }

    [Fact]
    public void ManyRuleErrors_AreCapped()
    {
        var scene = Parse(Repeat("""{"format": "infopanel.scene/1", "nodes": [""",
            """{"kind": "rect", "w": {"op": "input", "name": "missing"}, "h": 1}""", "]}", 120_000));
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal(101, check.Errors.Count);
        Assert.Equal("ref.input", check.Errors[0].Code);
        Assert.Equal("more", check.Errors[^1].Code);
    }

    [Fact]
    public void ExactlyOneErrorOverTheCap_IsListedAsMore()
    {
        // 101 rule errors: 100 listed, then "1 more".
        var nodes = string.Join(",", Enumerable.Repeat("""{"kind": "rect", "w": {"op": "input", "name": "missing"}, "h": 1}""", 101));
        var scene = Parse($$"""{"format": "infopanel.scene/1", "nodes": [{{nodes}}]}""");
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal(101, check.Errors.Count);
        Assert.Equal(100, check.Errors.Count(e => e.Code == "ref.input"));
        Assert.Equal(("more", "1 more errors not listed."), (check.Errors[^1].Code, check.Errors[^1].Message));
    }

    [Fact]
    public void CappedScenesInOneCommit_GiveOneMoreWithTheWholeCount()
    {
        // Two scenes with 150 rule errors each: each scene's check lists 100 and counts 50 more; the commit lists the
        // first 100 and counts the other 200 in one "more".
        var nodes = string.Join(",", Enumerable.Repeat("""{"kind": "rect", "w": {"op": "input", "name": "missing"}, "h": 1}""", 150));
        var op = """{"op": "setDesign", "id": "a", "scene": {"format": "infopanel.scene/1", "nodes": [""" + nodes + "]}}";
        var commit = Parse($$"""{"opId": "a", "instance": "i", "baseRev": 0, "ops": [{{op}}, {{op.Replace("\"a\"", "\"b\"")}}]}""");
        var check = Bounded(() => CommitValidator.Check(commit), seconds: 2);
        Assert.Equal(101, check.Errors.Count);
        Assert.Single(check.Errors, e => e.Code == "more");
        Assert.Equal("200 more errors not listed.", check.Errors[^1].Message);
    }

    // ---- 3b: long property names -------------------------------------------------------------------------------

    private static readonly string LongKey = new('k', 100_000);

    [Fact]
    public void ALongKeyAboveManyValues_BuildsNoPointerPerValue()
    {
        // About 200 KB: one 100,000-character key above 50,000 values in an item's source data. A JSON pointer string
        // per value would be 50,000 copies of the key (10 GB).
        var zeros = string.Join(",", Enumerable.Repeat("0", 50_000));
        var document = Parse($$"""
            {"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "profile": {"name": "P", "width": 1, "height": 1},
             "items": [{"kind": "scene", "id": "a", "placement": {"x": 0, "y": 0}, "source": {"format": "example.widget/1", "data": {"{{LongKey}}": [{{zeros}}] } },
                        "scene": {"format": "infopanel.scene/1", "nodes": []} } ] }
            """);
        var check = Bounded(() => DocumentValidator.Check(document));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => e.Code)));
    }

    [Fact]
    public void ALongKeyAboveManyErrors_MaterialisesOnlyTheReportedPointers()
    {
        // 50,000 errors under one 100,000-character key: only the 100 reported get a pointer string.
        var set = SchemaSet.Load(["""{"$id": "urn:t:long", "additionalProperties": {"type": "array", "items": {"type": "string"}}}"""]);
        var zeros = string.Join(",", Enumerable.Repeat("0", 50_000));
        var instance = Parse($$"""{"{{LongKey}}": [{{zeros}}]}""");
        var result = Bounded(() => set.Validate(instance, "urn:t:long"));
        Assert.Equal(SchemaSet.MaxErrors + 1, result.Errors.Count);
        Assert.Equal("/" + LongKey + "/0", result.Errors[0].InstancePath);
        Assert.Equal("more", result.Errors[^1].Keyword);
    }

    [Fact]
    public void ALongKeyReadByManyRules_CountsAgainstTheWorkBudget()
    {
        // A name is charged one step per 64 characters each time a rule reads it, and adds as much to the budget: 128
        // rules reading one 1,000,000-character name (128 × 15,625 steps, over a budget of 100,000 + 64 × 15,627) are
        // one complexity error, not 128 million characters of work.
        var refs = string.Join(", ", Enumerable.Repeat("""{"additionalProperties": {"type": "number"}}""", 128));
        var set = SchemaSet.Load([$$"""{"$id": "urn:t:names", "allOf": [{{refs}}]}"""]);
        var instance = Parse($$"""{"{{new string('k', 1_000_000)}}": 0}""");
        var result = Bounded(() => set.Validate(instance, "urn:t:names"));
        Assert.Equal("complexity", Assert.Single(result.Errors).Keyword);
    }

    // ---- 3c: uniqueItems --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("double")]
    [InlineData("decimal")]
    [InlineData("exact")]
    public void UniqueItemsOverCollidingNumbers_IsBounded(string how)
    {
        // The review's reproduction: 20,000 numbers whose double.GetHashCode collides made uniqueItems quadratic (13 s).
        // Hashes are seeded per process now; numbers that differ only past a double's precision still share a hash,
        // so every comparison is charged to the work budget instead.
        var numbers = Enumerable.Range(0, 20_000).Select(i => how == "double"
            ? BitConverter.Int64BitsToDouble(((0x3FF00000L + i) << 32) | ((0x3FF00000L + i) ^ 0x1234L) & 0xFFFFFFFFL).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : how == "decimal" ? "0.10000000000000000000000" + i.ToString("D5")
            // Past a decimal's 28 digits: compared exactly from their text.
            : "0.1" + new string('0', 40) + i.ToString("D5"));
        var array = "[" + string.Join(", ", numbers) + "]";
        var set = SchemaSet.Load(["""{"$id": "urn:t:unique", "uniqueItems": true}"""]);
        var result = Bounded(() => set.Validate(Parse(array), "urn:t:unique"));
        if (how != "double") Assert.Equal("complexity", Assert.Single(result.Errors).Keyword);
        else Assert.Empty(result.Errors);

        // In a scene's requires (now at most 32 items), the same numbers are refused within the bound.
        var scene = Parse($$"""{"format": "infopanel.scene/1", "requires": {{array}}, "nodes": []}""");
        Assert.NotEqual(CheckStatus.Valid, Bounded(() => SceneValidator.Check(scene)).Status);
    }

    [Fact]
    public void UniqueItems_StillFindsEqualNumbersAndObjects()
    {
        var set = SchemaSet.Load(["""{"$id": "urn:t:unique2", "uniqueItems": true}"""]);
        foreach (var dup in new[] { "[1, 1.0]", "[0, -0]", "[1e2, 100]", """[{"a": 1, "b": [2]}, {"b": [2.0], "a": 1}]""", """["x", "x"]""" })
            Assert.Equal("uniqueItems", Assert.Single(set.Validate(Parse(dup), "urn:t:unique2").Errors).Keyword);
        foreach (var distinct in new[] { "[1, 2]", "[0.1, 0.1000000000000000000000001]", """[{"a": 1, "b": 2}, {"a": 2, "b": 1}]""", "[[1, 2], [2, 1]]" })
            Assert.Empty(set.Validate(Parse(distinct), "urn:t:unique2").Errors);
    }

    // ---- 4, 5: SVG -----------------------------------------------------------------------------------------------

    [Fact]
    public void SvgInsideADataUri_IsRefused()
    {
        // A data: URI could carry another SVG (or anything else) past the profile.
        var inner = Convert.ToBase64String("<svg xmlns='http://www.w3.org/2000/svg'><script>x()</script></svg>"u8);
        var svg = Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><image href='data:image/svg+xml;base64,{inner}'/></svg>");
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Null(info);
        Assert.Equal("media.svg-external", Assert.Single(errors).Code);
    }

    [Fact]
    public void SvgInPhysicalUnits_IsRefused()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='1000000in' height='1000000in'/>"u8.ToArray();
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Null(info);
        Assert.Equal("media.svg-size", Assert.Single(errors).Code);
    }

    [Fact]
    public void SvgOverItsByteLimit_IsRefusedWithoutBeingRead()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'>" + new string('x', 30 << 20));
        var (_, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Equal("limit.svgBytes", Assert.Single(errors).Code);
    }

    [Fact]
    public void SvgElementBomb_StopsAtTheLimit()
    {
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>");
        while (sb.Length < 1_000_000) sb.Append("<g/>");
        var svg = Encoding.UTF8.GetBytes(sb.Append("</svg>").ToString());
        var (_, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Equal("limit.svgElements", Assert.Single(errors).Code);
    }

    [Fact]
    public void SvgUseExpansionBomb_IsRefusedBeforeDecoding()
    {
        // 30 definitions, each drawing the previous one twice through <use>: 2^30 elements drawn from 92 written, 63 levels deep.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><defs><rect id='d0' width='1' height='1'/>");
        for (var i = 1; i <= 30; i++) sb.Append($"<g id='d{i}'><use href='#d{i - 1}'/><use href='#d{i - 1}'/></g>");
        var svg = Encoding.UTF8.GetBytes(sb.Append("</defs><use href='#d30'/></svg>").ToString());
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Null(info);
        Assert.Equal("limit.svgElements", Assert.Single(errors).Code);
    }

    [Fact]
    public void SvgDeepUseChain_IsCheckedWithoutRecursion()
    {
        // 8 nested groups, the innermost one drawing a definition through a chain of 50 uses (each use in the chain
        // counts what it draws too: 1 + 2 + ... + 50 in the definitions, and 50 more where it is drawn): 60 levels drawn.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><defs><rect id='u0'/>");
        for (var i = 1; i < 50; i++) sb.Append($"<use id='u{i}' href='#u{i - 1}'/>");
        sb.Append("</defs>");
        for (var i = 0; i < 8; i++) sb.Append("<g>");
        sb.Append("<use href='#u49'/>");
        for (var i = 0; i < 8; i++) sb.Append("</g>");
        var svg = Encoding.UTF8.GetBytes(sb.Append("</svg>").ToString());
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Empty(errors);
        Assert.Equal(2 + 1_275 + 8 + 1 + 50, info!.Elements);
    }

    [Theory]
    [InlineData(3_000, 0)]
    [InlineData(0, 100_000)]
    public void SvgDeepNestingOrUseChain_IsRefusedBeforeDecoding(int groups, int uses)
    {
        // The review's stack overflows: 3,000 nested groups, and a flat chain of uses, each drawing the one before.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><defs><rect id='u0' width='9' height='9'/>");
        for (var i = 1; i <= uses; i++) sb.Append($"<use id='u{i}' href='#u{i - 1}'/>");
        sb.Append("</defs>");
        for (var i = 0; i < groups; i++) sb.Append("<g>");
        sb.Append($"<use href='#u{uses}'/>");
        for (var i = 0; i < groups; i++) sb.Append("</g>");
        var svg = Encoding.UTF8.GetBytes(sb.Append("</svg>").ToString());
        var (info, errors) = Bounded(() => MediaValidator.Check(svg, SpecLimits.Default with { SvgBytes = 8 << 20, SvgElements = 1_000_000 }));
        Assert.Null(info);
        Assert.Equal("media.svg-external", Assert.Single(errors).Code);
        Assert.Contains("deep", errors[0].Message);
    }

    [Fact]
    public void SvgLongGradientChain_IsRefusedBeforeDecoding()
    {
        // The review's reproduction: a chain of 4,900 gradients inheriting by href, painted by 4,901 rects, loaded for hours.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><linearGradient id='g0'><stop offset='0' stop-color='blue'/></linearGradient>");
        for (var i = 1; i <= 4_900; i++) sb.Append($"<linearGradient id='g{i}' href='#g{i - 1}'/>");
        for (var i = 0; i <= 4_900; i++) sb.Append($"<rect width='9' height='9' fill='url(#g{4_900 - i % 10})'/>");
        var svg = Encoding.UTF8.GetBytes(sb.Append("</svg>").ToString());
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Null(info);
        Assert.Contains("more than 4 href", Assert.Single(errors).Message);
    }

    [Fact]
    public void SvgLongPathDrawnByManyUses_IsRefusedBeforeDecoding()
    {
        // The review's reproduction: a 100,000-segment path drawn by 4,900 uses (69 s to render) in 580 KB.
        var d = "M0 0" + string.Concat(Enumerable.Repeat("l9 9 ", 100_000));
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><defs>" +
            $"<path id='p' d='{d}' stroke='black' fill='none'/></defs>" + string.Concat(Enumerable.Repeat("<use href='#p'/>", 4_900)) + "</svg>");
        var (info, errors) = Bounded(() => MediaValidator.Check(svg));
        Assert.Null(info);
        Assert.Equal("limit.svgElements", Assert.Single(errors).Code);
        // The path alone is 200,002 numbers: 6,251 elements, within the limit once.
        var once = Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><path d='{d}'/></svg>");
        Assert.Equal(2 + 200_002 / 32, Bounded(() => MediaValidator.Check(once)).Info!.Elements);
    }

    // ---- 6: strings ----------------------------------------------------------------------------------------------

    [Fact]
    public void DoublingConcatenation_IsCostedAsStringWork()
    {
        // v[i] = concat(v[i-1], v[i-1]): 2^200 characters without the 4,096 cut of every string result.
        var sb = new StringBuilder("""{"format": "infopanel.scene/1", "values": {"v0": "ab" """);
        for (var i = 1; i < 200; i++) sb.Append($$""", "v{{i}}": {"op": "concat", "args": [{"op": "value", "name": "v{{i - 1}}"}, {"op": "value", "name": "v{{i - 1}}"}]}""");
        sb.Append("""}, "nodes": [{"kind": "text", "font": {"family": "Inter", "size": 12}, "text": {"op": "value", "name": "v199"}}]}""");
        var scene = Parse(sb.ToString());
        var check = Bounded(() => SceneValidator.Check(scene));
        Assert.Equal(CheckStatus.Valid, check.Status);
        Assert.Equal(4_096, SpecLimits.Default.TextLength);
        // 199 concats at 16 each, and 2 value reads in each of them plus the text's own.
        Assert.Equal(199 * SceneValidator.StringOpWork + 199 * 2 + 1, check.Cost.Work);
    }
}
