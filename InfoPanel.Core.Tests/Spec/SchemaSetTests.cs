using System.Text;
using System.Text.Json;
using InfoPanel.Spec.Schema;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

public class SchemaSetTests
{
    private const string Shapes = """
    { "$id": "urn:test:shapes", "$defs": {
      "shape": { "oneOf": [ {"$ref": "#/$defs/rect"}, {"$ref": "#/$defs/circle"} ] },
      "rect": { "type": "object", "required": ["kind","w"], "additionalProperties": false,
                "properties": { "kind": {"const": "rect"}, "w": {"type": "number", "minimum": 0} } },
      "circle": { "type": "object", "required": ["kind","r"], "additionalProperties": false,
                  "properties": { "kind": {"const": "circle"}, "r": {"type": "number"} } },
      "numberExpr": { "oneOf": [ {"type": "number"}, {"$ref": "#/$defs/add"} ] },
      "add": { "type": "object", "required": ["op","args"], "additionalProperties": false,
               "properties": { "op": {"const": "add"}, "args": {"type": "array", "items": {"$ref": "#/$defs/numberExpr"}, "minItems": 2} } } } }
    """;

    private static JsonElement J(string json) =>
        JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2048 }).RootElement;

    private static SchemaSet Load(params string[] docs) => SchemaSet.Load(docs);

    /// <summary>Validates against a one-document schema; the schema body is wrapped with an $id.</summary>
    private static List<SchemaError> Check(string schemaBody, string instance)
    {
        var set = Load("{\"$id\":\"urn:t:s\"," + schemaBody + "}");
        return [.. set.Validate(J(instance), "urn:t:s").Errors];
    }

    private static void Ok(string schemaBody, string instance) => Assert.Empty(Check(schemaBody, instance));

    private static SchemaError One(string schemaBody, string instance, string keyword, string path = "")
    {
        var e = Assert.Single(Check(schemaBody, instance));
        Assert.Equal(keyword, e.Keyword);
        Assert.Equal(path, e.InstancePath);
        return e;
    }

    private static List<SchemaError> Shape(string instance, string def = "shape") =>
        [.. Load(Shapes).Validate(J(instance), "urn:test:shapes#/$defs/" + def).Errors];

    // ---- keywords -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"string\"", "\"a\"", true)]
    [InlineData("\"string\"", "1", false)]
    [InlineData("\"null\"", "null", true)]
    [InlineData("\"boolean\"", "true", true)]
    [InlineData("\"boolean\"", "0", false)]
    [InlineData("\"object\"", "{}", true)]
    [InlineData("\"array\"", "{}", false)]
    [InlineData("\"number\"", "1.5", true)]
    [InlineData("[\"string\",\"null\"]", "null", true)]
    [InlineData("[\"string\",\"null\"]", "1", false)]
    public void Type(string type, string instance, bool valid) =>
        Assert.Equal(valid, Check($"\"type\":{type}", instance).Count == 0);

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e2", true)]
    [InlineData("1.5", false)]
    [InlineData("-0", true)]
    [InlineData("\"1\"", false)]
    // By the double nearest the number's text: these are 1e-100, 1, 1, the double just below 1, and doubles past 2^53
    // (every one of which is whole).
    [InlineData("1e-100", false)]
    [InlineData("1.00000000000000000000000000001", true)]
    [InlineData("0.99999999999999999", true)]
    [InlineData("0.9999999999999999", false)]
    [InlineData("1234567890123456789012345678901234567890000000000e-10", true)]
    [InlineData("12345678901234567890123456789012345678901e-10", true)]
    public void IntegerSemantics(string instance, bool valid) =>
        Assert.Equal(valid, Check("\"type\":\"integer\"", instance).Count == 0);

    [Fact]
    public void Enum()
    {
        Ok("\"enum\":[1,\"a\",null,{\"x\":[1]}]", "\"a\"");
        Ok("\"enum\":[1,\"a\",null,{\"x\":[1]}]", "1.0");
        Ok("\"enum\":[1,\"a\",null,{\"x\":[1]}]", "{\"x\":[1.0]}");
        One("\"enum\":[1,\"a\"]", "2", "enum");
    }

    [Fact]
    public void Const()
    {
        Ok("\"const\":{\"a\":1,\"b\":[true]}", "{\"b\":[true],\"a\":1.0}");
        One("\"const\":{\"a\":1}", "{\"a\":1,\"b\":2}", "const");
        One("\"const\":false", "0", "const");
        // Numbers as the doubles nearest their text, however they are written.
        Ok("\"const\":100", "1e2");
        Ok("\"const\":0.5", "50e-2");
        Ok("\"const\":1", "1.00000000000000000000000000001");
        Ok("\"const\":0", "-0");
        Ok("\"const\":0", "1e-400");
        One("\"const\":0", "1e-100", "const");
        One("\"const\":1", "1.0000000000000002", "const");
    }

    [Theory]
    [InlineData("minimum", "5", "5", true)]
    [InlineData("minimum", "5", "4.99", false)]
    [InlineData("maximum", "5", "5", true)]
    [InlineData("maximum", "5", "5.01", false)]
    [InlineData("exclusiveMinimum", "5", "5", false)]
    [InlineData("exclusiveMinimum", "5", "5.1", true)]
    [InlineData("exclusiveMaximum", "5", "5", false)]
    [InlineData("exclusiveMaximum", "5", "4.9", true)]
    [InlineData("minimum", "0.1", "0.1", true)]
    [InlineData("maximum", "1e400", "1e300", true)]
    // By the doubles nearest the numbers' text.
    [InlineData("exclusiveMinimum", "0", "1e-100", true)]
    [InlineData("minimum", "0", "-1e-100", false)]
    [InlineData("minimum", "1e-100", "1e-101", false)]
    [InlineData("exclusiveMaximum", "1e-100", "0.1e-100", true)]
    [InlineData("maximum", "1", "1.00000000000000000000000000001", true)]
    [InlineData("minimum", "1", "0.99999999999999999999999999999", true)]
    [InlineData("maximum", "1", "1.0000000000000002", false)]
    [InlineData("exclusiveMinimum", "0", "1e-400", false)]
    [InlineData("maximum", "1.7976931348623157e308", "1.7976931348623158e308", true)]
    [InlineData("maximum", "-0", "0e5", true)]
    [InlineData("maximum", "-15", "-1.5E+1", true)]
    [InlineData("exclusiveMaximum", "-15", "-1.5e1", false)]
    [InlineData("minimum", "-1e-100", "-0.5e-100", true)]
    [InlineData("maximum", "-1e-100", "-0.5e-100", false)]
    [InlineData("minimum", "123.456", "1234.56e-1", true)]
    [InlineData("exclusiveMinimum", "123.456", "12345.6000000000000000000000000000000000001e-2", false)]
    public void NumberBounds(string keyword, string limit, string instance, bool valid)
    {
        var errors = Check($"\"{keyword}\":{limit}", instance);
        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, e => Assert.Equal(keyword, e.Keyword));
    }

    [Fact]
    public void NumberBoundsIgnoreNonNumbers() => Ok("\"minimum\":5", "\"x\"");

    [Theory]
    [InlineData("1e400", "")]
    [InlineData("-1e400", "")]
    [InlineData("1.8e308", "")]
    [InlineData("[0, {\"a\": [1, 1e400]}]", "/1/a/1")]
    public void ANumberThatRoundsToInfinity_IsOneJsonErrorAtTheNumber_AndNothingElseIsChecked(string instance, string path)
    {
        // Whatever the schema (here one the number breaks too, and one with no rule for it): only the json error.
        foreach (var schema in new[] { "\"type\":\"string\"", "\"description\":\"anything\"" })
            One(schema, instance, "json", path);
    }

    [Fact]
    public void NumbersThatRoundToInfinity_AreEachAJsonError_UpToTheCap()
    {
        var errors = Check("\"type\":\"array\"", "[" + string.Join(",", Enumerable.Repeat("1e999", 150)) + "]");
        Assert.Equal(SchemaSet.MaxErrors + 1, errors.Count);
        Assert.All(errors.Take(SchemaSet.MaxErrors), e => Assert.Equal("json", e.Keyword));
        Assert.Equal(("more", "50 more errors not listed."), (errors[^1].Keyword, errors[^1].Message));
    }

    [Fact]
    public void LengthCountsCodePoints()
    {
        Ok("\"maxLength\":1", "\"😀\"");
        Ok("\"minLength\":2", "\"😀😀\"");
        One("\"minLength\":2", "\"😀\"", "minLength");
        One("\"maxLength\":2", "\"abc\"", "maxLength");
        Ok("\"maxLength\":2", "5");
    }

    [Fact]
    public void PatternIsUnanchored()
    {
        Ok("\"pattern\":\"b+\"", "\"abbc\"");
        One("\"pattern\":\"^b+$\"", "\"abbc\"", "pattern");
        Ok("\"pattern\":\"^b\"", "5");
    }

    [Fact]
    public void PatternTimeoutIsAnErrorNotAHang()
    {
        var input = new string('a', 40) + "!";
        var e = Check("\"pattern\":\"^(a+)+$\"", "\"" + input + "\"");
        Assert.Single(e);
        Assert.Equal("pattern", e[0].Keyword);
    }

    [Fact]
    public void ItemCounts()
    {
        Ok("\"minItems\":1,\"maxItems\":2", "[1]");
        One("\"minItems\":2", "[1]", "minItems");
        One("\"maxItems\":1", "[1,2]", "maxItems");
        Ok("\"minItems\":2", "{}");
    }

    [Fact]
    public void PropertyCounts()
    {
        Ok("\"minProperties\":1,\"maxProperties\":2", "{\"a\":1}");
        One("\"minProperties\":1", "{}", "minProperties");
        One("\"maxProperties\":1", "{\"a\":1,\"b\":2}", "maxProperties");
    }

    [Fact]
    public void UniqueItemsUsesDeepNumericEquality()
    {
        Ok("\"uniqueItems\":true", "[1,2,\"1\",[1],{\"a\":1}]");
        One("\"uniqueItems\":true", "[1,2,1.0]", "uniqueItems");
        One("\"uniqueItems\":true", "[{\"a\":1,\"b\":[2]},{\"b\":[2.0],\"a\":1}]", "uniqueItems");
        Ok("\"uniqueItems\":false", "[1,1]");
        Ok("\"uniqueItems\":true", "[true,1,false,0,null]");
    }

    [Fact]
    public void RequiredReportsEachMissingPropertyAtTheObject()
    {
        var errors = Check("\"required\":[\"a\",\"b\",\"c\"]", "{\"b\":1}");
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => { Assert.Equal("required", e.Keyword); Assert.Equal("", e.InstancePath); });
        Assert.Contains(errors, e => e.Message.Contains("\"a\""));
        Assert.Contains(errors, e => e.Message.Contains("\"c\""));
        Ok("\"required\":[\"a\"]", "[1]");
    }

    [Fact]
    public void Properties()
    {
        const string s = "\"properties\":{\"a\":{\"type\":\"integer\"},\"b/c\":{\"type\":\"string\"}}";
        Ok(s, "{\"a\":1,\"zzz\":null}");
        One(s, "{\"a\":\"x\"}", "type", "/a");
        One(s, "{\"b/c\":1}", "type", "/b~1c");
    }

    [Fact]
    public void AdditionalPropertiesFalseReportsEachExtraProperty()
    {
        const string s = "\"properties\":{\"a\":true},\"additionalProperties\":false";
        Ok(s, "{\"a\":1}");
        var errors = Check(s, "{\"a\":1,\"x\":1,\"y~\":2}");
        Assert.Equal(["/x", "/y~0"], errors.Select(e => e.InstancePath));
        Assert.All(errors, e => Assert.Equal("additionalProperties", e.Keyword));
    }

    [Fact]
    public void AdditionalPropertiesSchema()
    {
        const string s = "\"properties\":{\"a\":true},\"additionalProperties\":{\"type\":\"integer\"}";
        Ok(s, "{\"a\":\"free\",\"x\":1}");
        One(s, "{\"a\":1,\"x\":\"no\"}", "type", "/x");
        Ok("\"additionalProperties\":true", "{\"a\":1}");
    }

    [Fact]
    public void PropertyNames()
    {
        const string s = "\"propertyNames\":{\"pattern\":\"^[a-z]+$\"}";
        Ok(s, "{\"abc\":1}");
        One(s, "{\"ok\":1,\"Bad\":2}", "propertyNames", "/Bad");
    }

    [Fact]
    public void ItemsAndPrefixItems()
    {
        One("\"items\":{\"type\":\"integer\"}", "[1,2,\"x\"]", "type", "/2");
        Ok("\"items\":{\"type\":\"integer\"}", "[]");
        const string s = "\"prefixItems\":[{\"type\":\"string\"},{\"type\":\"boolean\"}],\"items\":{\"type\":\"integer\"}";
        Ok(s, "[\"a\",true,1,2]");
        Ok(s, "[\"a\"]");
        var errors = Check(s, "[1,\"x\",\"y\"]");
        Assert.Equal(["/0", "/1", "/2"], errors.Select(e => e.InstancePath));
        Ok("\"prefixItems\":[true,{\"type\":\"string\"}]", "[1,\"s\",{}]");
    }

    [Fact]
    public void AllOfAnyOfOneOfNot()
    {
        const string all = "\"allOf\":[{\"type\":\"integer\"},{\"minimum\":3}]";
        Ok(all, "5");
        One(all, "1", "minimum");
        const string any = "\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"string\"}]";
        Ok(any, "\"a\"");
        Ok(any, "1");
        One(any, "true", "anyOf");
        const string one = "\"oneOf\":[{\"type\":\"integer\"},{\"minimum\":3}]";
        Ok(one, "1");
        Ok(one, "3.5");
        Assert.Contains("more than one", One(one, "5", "oneOf").Message);
        One(one, "2.5", "minimum");
        Ok("\"not\":{\"type\":\"string\"}", "1");
        One("\"not\":{\"type\":\"string\"}", "\"x\"", "not");
    }

    [Fact]
    public void BooleanSchemas()
    {
        Ok("\"properties\":{\"a\":true}", "{\"a\":1}");
        One("\"properties\":{\"a\":false}", "{\"a\":1}", "false", "/a");
        Ok("\"properties\":{\"a\":false}", "{}");
        One("\"items\":false", "[1]", "false", "/0");
        Ok("\"$defs\":{\"t\":true,\"f\":false}", "1");
        var set = Load("{\"$id\":\"urn:b\",\"$defs\":{\"t\":true,\"f\":false}}");
        Assert.True(set.Validate(J("1"), "urn:b#/$defs/t").IsValid);
        Assert.False(set.Validate(J("1"), "urn:b#/$defs/f").IsValid);
    }

    [Fact]
    public void AnnotationsAndCommentsAreAccepted() =>
        Ok("\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$comment\":\"c\",\"title\":\"t\",\"description\":\"d\",\"default\":1,\"examples\":[1],\"deprecated\":true,\"readOnly\":true,\"writeOnly\":false,\"type\":\"integer\"", "1");

    // ---- $ref -----------------------------------------------------------------------------------------------

    [Fact]
    public void RefWithinDocumentAndRecursion()
    {
        const string s = "\"$defs\":{\"node\":{\"type\":\"object\",\"properties\":{\"kids\":{\"type\":\"array\",\"items\":{\"$ref\":\"#/$defs/node\"}},\"n\":{\"type\":\"integer\"}}}},\"$ref\":\"#/$defs/node\"";
        Ok(s, "{\"n\":1,\"kids\":[{\"kids\":[{\"n\":2}]}]}");
        One(s, "{\"kids\":[{\"kids\":[{\"n\":\"x\"}]}]}", "type", "/kids/0/kids/0/n");
    }

    [Fact]
    public void RefSiblingsAreEvaluatedToo()
    {
        const string s = "\"$defs\":{\"int\":{\"type\":\"integer\"}},\"properties\":{\"a\":{\"$ref\":\"#/$defs/int\",\"minimum\":10}}";
        Ok(s, "{\"a\":10}");
        One(s, "{\"a\":5}", "minimum", "/a");
        One(s, "{\"a\":50.5}", "type", "/a");
    }

    [Fact]
    public void RefAcrossDocuments()
    {
        var set = Load(
            "{\"$id\":\"urn:t:a\",\"$defs\":{\"n\":{\"type\":\"integer\"}}}",
            "{\"$id\":\"urn:t:b\",\"properties\":{\"x\":{\"$ref\":\"urn:t:a#/$defs/n\"},\"y\":{\"$ref\":\"urn:t:a\"}}}");
        Assert.Equal(new[] { "urn:t:a", "urn:t:b" }, set.Ids.OrderBy(x => x));
        Assert.True(set.Validate(J("{\"x\":1,\"y\":{}}"), "urn:t:b").IsValid);
        var e = Assert.Single(set.Validate(J("{\"x\":\"s\"}"), "urn:t:b").Errors);
        Assert.Equal("/x", e.InstancePath);
        Assert.Equal("urn:t:a#/$defs/n/type", e.SchemaPath);
    }

    [Fact]
    public void SchemaPathNamesTheFailingKeyword()
    {
        var e = Assert.Single(Shape("{\"kind\":\"rect\",\"w\":-1}"));
        Assert.Equal("urn:test:shapes#/$defs/rect/properties/w/minimum", e.SchemaPath);
    }

    [Fact]
    public void RefPointerEscapesAndPercentDecoding()
    {
        var set = Load("{\"$id\":\"urn:p\",\"$defs\":{\"a/b\":{\"type\":\"integer\"},\"c~d\":{\"type\":\"string\"}},\"properties\":{\"x\":{\"$ref\":\"#/$defs/a~1b\"},\"y\":{\"$ref\":\"#/$defs/c~0d\"}}}");
        Assert.True(set.Validate(J("{\"x\":1,\"y\":\"s\"}"), "urn:p").IsValid);
        Assert.False(set.Validate(J("{\"x\":\"s\"}"), "urn:p").IsValid);
        Assert.True(set.Validate(J("1"), "urn:p#/$defs/a~1b").IsValid);
    }

    [Fact]
    public void UnknownSchemaRefThrowsArgumentException()
    {
        var set = Load(Shapes);
        Assert.Throws<ArgumentException>(() => set.Validate(J("1"), "urn:nope"));
        Assert.Throws<ArgumentException>(() => set.Validate(J("1"), "urn:test:shapes#/$defs/nope"));
        Assert.Throws<ArgumentException>(() => set.Validate(J("1"), "urn:test:shapes#anchor"));
    }

    // ---- load-time failures ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"format\":\"email\"")]
    [InlineData("\"if\":true")]
    [InlineData("\"$anchor\":\"a\"")]
    [InlineData("\"$dynamicRef\":\"#a\"")]
    [InlineData("\"dependentRequired\":{}")]
    [InlineData("\"properties\":{\"a\":{\"format\":\"x\"}}")]
    [InlineData("\"type\":\"integr\"")]
    [InlineData("\"type\":[]")]
    [InlineData("\"minLength\":-1")]
    [InlineData("\"minLength\":1.5")]
    [InlineData("\"required\":\"a\"")]
    [InlineData("\"required\":[\"a\",\"a\"]")]
    [InlineData("\"properties\":[]")]
    [InlineData("\"items\":[{}]")]
    [InlineData("\"allOf\":[]")]
    [InlineData("\"allOf\":{}")]
    [InlineData("\"pattern\":\"(\"")]
    [InlineData("\"enum\":1")]
    [InlineData("\"minimum\":\"1\"")]
    [InlineData("\"uniqueItems\":1")]
    [InlineData("\"$schema\":\"http://json-schema.org/draft-07/schema#\"")]
    [InlineData("\"$defs\":{\"a\":{\"$id\":\"urn:inner\"}}")]
    [InlineData("\"title\":5")]
    [InlineData("\"not\":5")]
    public void LoadRejects(string body)
    {
        var ex = Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:bad\"," + body + "}"));
        Assert.Contains("urn:bad#", ex.Message);
    }

    [Theory]
    [InlineData("#/$defs/missing")]
    [InlineData("urn:unknown")]
    [InlineData("urn:unknown#/$defs/x")]
    [InlineData("#anchor")]
    [InlineData("#/properties/a/0")]
    public void LoadRejectsUnresolvedRefs(string reference)
    {
        var ex = Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:bad\",\"properties\":{\"a\":{\"$ref\":\"" + reference + "\"}}}"));
        Assert.Equal("urn:bad", ex.SchemaId);
        Assert.Equal("/properties/a/$ref", ex.Pointer);
        Assert.Contains("/properties/a/$ref", ex.Message);
    }

    [Fact]
    public void LoadRejectsBadDocuments()
    {
        Assert.Throws<SchemaException>(() => Load("{not json"));
        Assert.Throws<SchemaException>(() => Load("{\"type\":\"string\"}"));
        Assert.Throws<SchemaException>(() => Load("true"));
        Assert.Throws<SchemaException>(() => Load("{\"$id\":5}"));
        Assert.Throws<SchemaException>(() => Load("{\"$id\":\"relative\"}"));
        Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:x#frag\"}"));
        var dup = Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:d\"}", "{\"$id\":\"urn:d\"}"));
        Assert.Contains("urn:d", dup.Message);
    }

    [Fact]
    public void LoadRejectsRefLoopsThatDoNotDescend()
    {
        Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:l\",\"$defs\":{\"a\":{\"$ref\":\"#/$defs/b\"},\"b\":{\"$ref\":\"#/$defs/a\"}}}"));
        Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:l\",\"$ref\":\"#\"}"));
        Assert.Throws<SchemaException>(() => Load("{\"$id\":\"urn:l\",\"anyOf\":[{\"type\":\"null\"},{\"$ref\":\"#\"}]}"));
        // descending cycles are fine
        Load("{\"$id\":\"urn:l\",\"properties\":{\"n\":{\"$ref\":\"#\"}}}");
    }

    // ---- instance guards -------------------------------------------------------------------------------------

    [Fact]
    public void DuplicateKeysAreReported()
    {
        var e = One("\"type\":\"object\"", "{\"a\":1,\"a\":2}", "duplicateKey");
        Assert.Contains("\"a\"", e.Message);
        var nested = Check("\"type\":\"object\"", "{\"x\":[{\"b\":1,\"b\":1,\"b\":1}]}");
        var d = Assert.Single(nested);
        Assert.Equal("/x/0", d.InstancePath);
        Assert.Equal("duplicateKey", d.Keyword);
    }

    [Fact]
    public void DepthGuardGivesOneError()
    {
        static string Nest(int n) => new string('[', n) + new string(']', n);
        Ok("\"type\":\"array\"", Nest(256));
        var e = Assert.Single(Check("\"type\":\"array\"", Nest(300)));
        Assert.Equal("depth", e.Keyword);
        // also through a recursive schema
        var set = Load("{\"$id\":\"urn:r\",\"type\":\"array\",\"items\":{\"$ref\":\"#\"}}");
        Assert.Single(set.Validate(J(Nest(1500)), "urn:r").Errors);
        Assert.True(set.Validate(J(Nest(200)), "urn:r").IsValid);
    }

    [Fact]
    public void ErrorsAreOrderedByPathThenKeyword()
    {
        var errors = Check("\"properties\":{\"b\":{\"type\":\"string\",\"minLength\":5},\"a\":{\"type\":\"string\"}},\"required\":[\"z\"]",
            "{\"b\":1,\"a\":2}");
        Assert.Equal(new[] { ("", "required"), ("/a", "type"), ("/b", "type") }, errors.Select(e => (e.InstancePath, e.Keyword)));
    }

    [Fact]
    public void InstanceAsJsonNode()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}");
        using var doc = JsonDocument.Parse(bytes);
        Assert.True(Load("{\"$id\":\"urn:q\",\"type\":\"object\"}").Validate(doc.RootElement, "urn:q").IsValid);
    }

    // ---- union reporting -------------------------------------------------------------------------------------

    [Fact]
    public void UnionValidInstancesPass()
    {
        Assert.Empty(Shape("{\"kind\":\"rect\",\"w\":1}"));
        Assert.Empty(Shape("{\"kind\":\"circle\",\"r\":-1}"));
        Assert.Empty(Shape("1", "numberExpr"));
        Assert.Empty(Shape("{\"op\":\"add\",\"args\":[1,{\"op\":\"add\",\"args\":[2,3]}]}", "numberExpr"));
    }

    [Fact]
    public void UnionDiscriminatorPicksTheBranchAndReportsItsDeepError()
    {
        var e = Assert.Single(Shape("{\"kind\":\"rect\",\"w\":-1}"));
        Assert.Equal("/w", e.InstancePath);
        Assert.Equal("minimum", e.Keyword);
    }

    [Fact]
    public void UnionUnknownDiscriminatorValue()
    {
        var e = Assert.Single(Shape("{\"kind\":\"tri\"}"));
        Assert.Equal("/kind", e.InstancePath);
        Assert.Equal("oneOf", e.Keyword);
        Assert.Contains("\"rect\"", e.Message);
        Assert.Contains("\"circle\"", e.Message);
    }

    [Fact]
    public void UnionMissingDiscriminator()
    {
        var e = Assert.Single(Shape("{\"w\":1}"));
        Assert.Equal("", e.InstancePath);
        Assert.Equal("required", e.Keyword);
        Assert.Contains("\"kind\"", e.Message);
    }

    [Fact]
    public void UnionDiscriminatedBranchReportsAllItsErrors()
    {
        var errors = Shape("{\"kind\":\"rect\",\"w\":-1,\"extra\":1}");
        Assert.Equal(new[] { ("/extra", "additionalProperties"), ("/w", "minimum") }, errors.Select(e => (e.InstancePath, e.Keyword)));
        var missing = Assert.Single(Shape("{\"kind\":\"circle\"}"));
        Assert.Equal(("", "required"), (missing.InstancePath, missing.Keyword));
        Assert.Contains("\"r\"", missing.Message);
    }

    [Fact]
    public void UnionTypeMismatchGivesOneRootError()
    {
        var e = Assert.Single(Shape("\"x\"", "numberExpr"));
        Assert.Equal("", e.InstancePath);
        Assert.Contains(e.Keyword, new[] { "oneOf", "type" });
        var s = Assert.Single(Shape("[1]"));
        Assert.Equal("", s.InstancePath);
    }

    [Fact]
    public void UnionSingleTypeCompatibleBranchReportsDeepErrors()
    {
        var e = Assert.Single(Shape("{\"op\":\"add\",\"args\":[1,\"x\"]}", "numberExpr"));
        Assert.Equal("/args/1", e.InstancePath);
        Assert.Equal("oneOf", e.Keyword);
        var deep = Assert.Single(Shape("{\"op\":\"add\",\"args\":[1,{\"op\":\"add\",\"args\":[2,\"y\"]}]}", "numberExpr"));
        Assert.Equal("/args/1/args/1", deep.InstancePath);
        var short1 = Assert.Single(Shape("{\"op\":\"add\",\"args\":[1]}", "numberExpr"));
        Assert.Equal(("/args", "minItems"), (short1.InstancePath, short1.Keyword));
    }

    [Fact]
    public void UnionNoDiscriminatorGivesGenericError()
    {
        const string s = "\"oneOf\":[{\"type\":\"object\",\"required\":[\"a\"]},{\"type\":\"object\",\"required\":[\"b\"]}]";
        var e = One(s, "{}", "oneOf");
        Assert.Contains("any", e.Message);
        Assert.Contains("more than one", One(s, "{\"a\":1,\"b\":1}", "oneOf").Message);
        Ok(s, "{\"a\":1}");
    }

    [Fact]
    public void AnyOfUsesTheSameReporting()
    {
        const string s = "\"anyOf\":[{\"type\":\"object\",\"required\":[\"k\"],\"properties\":{\"k\":{\"const\":\"a\"},\"n\":{\"type\":\"integer\"}}}," +
                         "{\"type\":\"object\",\"required\":[\"k\"],\"properties\":{\"k\":{\"const\":\"b\"}}}]";
        Ok(s, "{\"k\":\"a\",\"n\":1}");
        Ok(s, "{\"k\":\"b\"}");
        One(s, "{\"k\":\"a\",\"n\":\"x\"}", "type", "/n");
        One(s, "{\"k\":\"c\"}", "anyOf", "/k");
        One(s, "{}", "required", "");
    }

    private const string Exprs = """
    { "$id": "urn:test:expr", "$defs": {
      "expr": { "oneOf": [ {"type": "number"}, {"$ref": "#/$defs/unary"}, {"$ref": "#/$defs/ifExpr"}, {"$ref": "#/$defs/mathUnion"} ] },
      "unary": { "type": "object", "required": ["op","args"],
                 "properties": { "op": {"enum": ["neg","abs","floor"]}, "args": {"type": "array", "items": {"$ref": "#/$defs/expr"}, "minItems": 1, "maxItems": 1} } },
      "ifExpr": { "type": "object", "required": ["op","args"],
                  "properties": { "op": {"const": "if"}, "args": {"type": "array", "items": {"$ref": "#/$defs/expr"}, "minItems": 3} } },
      "mathUnion": { "oneOf": [ {"$ref": "#/$defs/sin"}, {"$ref": "#/$defs/cos"} ] },
      "sin": { "type": "object", "required": ["fn","x"], "properties": { "fn": {"const": "sin"}, "x": {"type": "number"} } },
      "cos": { "type": "object", "required": ["fn","x"], "properties": { "fn": {"const": "cos"}, "x": {"type": "string"} } } } }
    """;

    private static List<SchemaError> Expr(string instance) =>
        [.. Load(Exprs).Validate(J(instance), "urn:test:expr#/$defs/expr").Errors];

    [Fact]
    public void UnionDiscriminatorMayBeAnEnum()
    {
        Assert.Empty(Expr("{\"op\":\"abs\",\"args\":[1]}"));
        var e = Assert.Single(Expr("{\"op\":\"abs\",\"args\":[\"x\"]}"));
        Assert.Equal("/args/0", e.InstancePath);
        // An op outside every enum leaves only the nested union (no "op" discriminator of its own); its own report wins.
        var bad = Assert.Single(Expr("{\"op\":\"sqrt\",\"args\":[1]}"));
        Assert.Equal(("", "required"), (bad.InstancePath, bad.Keyword));
        Assert.Contains("\"fn\"", bad.Message);
    }

    [Fact]
    public void UnionNestedUnionBranchStaysACandidateAndIsReportedInto()
    {
        // Valid through the nested union.
        Assert.Empty(Expr("{\"fn\":\"sin\",\"x\":1}"));
        // "op" is absent but the nested union has no discriminator on "op", so it is the only candidate: its own report is used.
        var e = Assert.Single(Expr("{\"fn\":\"sin\",\"x\":\"s\"}"));
        Assert.Equal(("/x", "type"), (e.InstancePath, e.Keyword));
        var tag = Assert.Single(Expr("{\"fn\":\"tan\"}"));
        Assert.Equal(("/fn", "oneOf"), (tag.InstancePath, tag.Keyword));
        // An unrecognised "op" leaves only the nested union, which then reports its own (missing "fn") error.
        var none = Assert.Single(Expr("{\"op\":\"zzz\"}"));
        Assert.Equal(("", "required"), (none.InstancePath, none.Keyword));
        Assert.Contains("\"fn\"", none.Message);
    }
}
