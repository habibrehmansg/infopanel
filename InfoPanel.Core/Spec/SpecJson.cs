using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.Spec;

/// <summary>
/// The schema definition(s) a model type mirrors (<c>{schema id}#{pointer}</c>); SpecSchemaAgreementTests checks that the
/// type's JSON members are the definition's properties.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SpecDefAttribute(params string[] refs) : Attribute
{
    public IReadOnlyList<string> Refs { get; } = refs;
}

/// <summary>
/// Reads and writes the spec model. Strict: unknown members, duplicate members and missing required members throw.
/// Callers validate against the schemas first (<see cref="SpecSchemas"/>), so these readers only see well-formed input.
/// </summary>
public static class SpecJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowOutOfOrderMetadataProperties = true,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = SchemaDepth,
        };
        options.Converters.Add(new IntegralInt32Converter());
        options.Converters.Add(new IntegralInt64Converter());
        options.Converters.Add(new ExprConverter());
        options.Converters.Add(new PaintConverter());
        options.Converters.Add(new CornerRadiiConverter());
        options.Converters.Add(new TextContentConverter());
        options.Converters.Add(new ColumnRefConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// The nesting depth the model is read and written at: the format's <c>jsonDepth</c> limit, so every value within the
    /// limit can be read and written; deeper input is refused by the limits first.
    /// </summary>
    public const int SchemaDepth = 256;

    /// <summary>
    /// Options for <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> of spec documents and bodies: any depth
    /// (parsing is not recursive), so that content nested too deeply is the validator's <c>limit.jsonDepth</c>, not
    /// unreadable JSON.
    /// </summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new() { MaxDepth = int.MaxValue };

    /// <summary>
    /// How deeply a JSON value nests arrays and objects as <c>jsonDepth</c> counts it: 0 for a number, string, boolean or
    /// null, 1 for an array or object holding none, one more per level. Read without recursion.
    /// </summary>
    public static int Depth(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = int.MaxValue });
        var max = 0;
        while (reader.Read())
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= max)
                max = reader.CurrentDepth + 1;
        return max;
    }

    /// <summary><see cref="Depth(ReadOnlySpan{byte})"/> of an element's own text; 0 for an absent (undefined) one.</summary>
    public static int Depth(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? 0 : Depth(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value));

    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Options)
        ?? throw new JsonException($"Expected a {typeof(T).Name}, not null.");

    public static JsonElement Write<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// A JSON integer as an int: JSON Schema's integer allows any spelling with a zero fraction (1.0, 1e0), so every
    /// integer member is read through here or <see cref="IntegralInt32Converter"/>, never with <c>GetInt32</c>.
    /// </summary>
    internal static int ReadInt32(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetInt32(out var i)) return i;
            if (Integral32(e.GetDouble()) is { } d) return d;
        }
        throw new JsonException("Expected an integer.");
    }

    private static int? Integral32(double d) => Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;

    /// <summary>JSON integers may be written with a fraction of zero (1.0), as JSON Schema's integer allows.</summary>
    private sealed class IntegralInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt32(out var i)) return i;
                if (Integral32(reader.GetDouble()) is { } d) return d;
            }
            throw new JsonException("Expected an integer.");
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class IntegralInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out var i)) return i;
                var d = reader.GetDouble();
                if (Math.Floor(d) == d && d >= -9.007199254740992E15 && d <= 9.007199254740992E15) return (long)d;
            }
            throw new JsonException("Expected an integer.");
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class ExprConverter : JsonConverter<Expr>
    {
        public override Expr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return ReadExpr(doc.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, Expr value, JsonSerializerOptions options) => WriteExpr(writer, value);
    }

    internal static Expr ReadExpr(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: return new NumberLit(e.GetDouble());
            case JsonValueKind.String: return new StringLit(e.GetString()!);
            case JsonValueKind.True: return new BoolLit(true);
            case JsonValueKind.False: return new BoolLit(false);
            case JsonValueKind.Object: break;
            default: throw new JsonException($"An expression can't be {e.ValueKind}.");
        }

        var op = Str(e, "op") ?? throw new JsonException("An expression object needs \"op\".");
        Expr result = op switch
        {
            "input" => new InputExpr(Req(e, "name"), Str(e, "field")),
            "history" => new HistoryExpr(Req(e, "input"), Req(e, "fn"), ReadInt32(e.GetProperty("count"))),
            "time" => new TimeExpr(Req(e, "field"), Str(e, "zone")),
            "formatTime" => new FormatTimeExpr(Req(e, "pattern"), Str(e, "locale"), Str(e, "zone")),
            "format" => new FormatExpr(ReadExpr(e.GetProperty("value")), Opt(e, "decimals"), Opt(e, "group"),
                Str(e, "decimalSeparator"), Str(e, "groupSeparator")),
            "box" => new BoxExpr(Req(e, "field")),
            "measure" => new MeasureExpr(Req(e, "node"), Req(e, "field")),
            "dt" => new DtExpr(),
            "value" => new ValueRefExpr(Req(e, "name")),
            "state" => new StateRefExpr(Req(e, "name")),
            "steps" => new StepsExpr(ReadExpr(e.GetProperty("value")), List(e, "at"), List(e, "out")),
            _ => new CallExpr(op, List(e, "args")),
        };
        var expected = result switch
        {
            InputExpr x => 2 + (x.Field is null ? 0 : 1),
            HistoryExpr => 4,
            TimeExpr x => 2 + (x.Zone is null ? 0 : 1),
            FormatTimeExpr x => 2 + (x.Locale is null ? 0 : 1) + (x.Zone is null ? 0 : 1),
            FormatExpr x => 2 + (x.Decimals is null ? 0 : 1) + (x.Group is null ? 0 : 1)
                + (x.DecimalSeparator is null ? 0 : 1) + (x.GroupSeparator is null ? 0 : 1),
            BoxExpr or ValueRefExpr or StateRefExpr => 2,
            MeasureExpr => 3,
            DtExpr => 1,
            StepsExpr => 4,
            _ => 2,
        };
        var count = 0;
        foreach (var _ in e.EnumerateObject()) count++;
        if (count != expected) throw new JsonException($"Unexpected members in a \"{op}\" expression.");
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : throw new JsonException($"\"{name}\" must be a string.")) : null;

    private static string Req(JsonElement e, string name) => Str(e, name) ?? throw new JsonException($"Missing \"{name}\".");

    private static Expr? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? ReadExpr(v) : null;

    private static List<Expr> List(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new JsonException($"\"{name}\" must be an array.");
        var list = new List<Expr>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray()) list.Add(ReadExpr(item));
        return list;
    }

    internal static void WriteExpr(Utf8JsonWriter w, Expr expr)
    {
        switch (expr)
        {
            case NumberLit n: w.WriteNumberValue(n.Value); return;
            case StringLit s: w.WriteStringValue(s.Value); return;
            case BoolLit b: w.WriteBooleanValue(b.Value); return;
        }

        w.WriteStartObject();
        switch (expr)
        {
            case CallExpr c:
                w.WriteString("op", c.Op);
                WriteList(w, "args", c.Args);
                break;
            case InputExpr x:
                w.WriteString("op", "input");
                w.WriteString("name", x.Name);
                if (x.Field is not null) w.WriteString("field", x.Field);
                break;
            case HistoryExpr x:
                w.WriteString("op", "history");
                w.WriteString("input", x.Input);
                w.WriteString("fn", x.Fn);
                w.WriteNumber("count", x.Count);
                break;
            case TimeExpr x:
                w.WriteString("op", "time");
                w.WriteString("field", x.Field);
                if (x.Zone is not null) w.WriteString("zone", x.Zone);
                break;
            case FormatTimeExpr x:
                w.WriteString("op", "formatTime");
                w.WriteString("pattern", x.Pattern);
                if (x.Locale is not null) w.WriteString("locale", x.Locale);
                if (x.Zone is not null) w.WriteString("zone", x.Zone);
                break;
            case FormatExpr x:
                w.WriteString("op", "format");
                w.WritePropertyName("value");
                WriteExpr(w, x.Value);
                if (x.Decimals is not null) { w.WritePropertyName("decimals"); WriteExpr(w, x.Decimals); }
                if (x.Group is not null) { w.WritePropertyName("group"); WriteExpr(w, x.Group); }
                if (x.DecimalSeparator is not null) w.WriteString("decimalSeparator", x.DecimalSeparator);
                if (x.GroupSeparator is not null) w.WriteString("groupSeparator", x.GroupSeparator);
                break;
            case BoxExpr x:
                w.WriteString("op", "box");
                w.WriteString("field", x.Field);
                break;
            case MeasureExpr x:
                w.WriteString("op", "measure");
                w.WriteString("node", x.Node);
                w.WriteString("field", x.Field);
                break;
            case DtExpr:
                w.WriteString("op", "dt");
                break;
            case ValueRefExpr x:
                w.WriteString("op", "value");
                w.WriteString("name", x.Name);
                break;
            case StateRefExpr x:
                w.WriteString("op", "state");
                w.WriteString("name", x.Name);
                break;
            case StepsExpr x:
                w.WriteString("op", "steps");
                w.WritePropertyName("value");
                WriteExpr(w, x.Value);
                WriteList(w, "at", x.At);
                WriteList(w, "out", x.Out);
                break;
            default:
                throw new JsonException($"Unknown expression {expr.GetType().Name}.");
        }
        w.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<Expr> list)
    {
        w.WriteStartArray(name);
        foreach (var item in list) WriteExpr(w, item);
        w.WriteEndArray();
    }

    /// <summary>A paint is a colour expression (string or op object) or a shader object with <c>type</c>.</summary>
    private sealed class PaintConverter : JsonConverter<Paint>
    {
        public override Paint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out _))
                return e.Deserialize<ShaderPaint>(options) ?? throw new JsonException("A paint can't be null.");
            return new SolidPaint(ReadExpr(e));
        }

        public override void Write(Utf8JsonWriter writer, Paint value, JsonSerializerOptions options)
        {
            if (value is SolidPaint solid) WriteExpr(writer, solid.Color);
            else JsonSerializer.Serialize(writer, (ShaderPaint)value, options);
        }
    }

    private sealed class CornerRadiiConverter : JsonConverter<CornerRadii>
    {
        public override CornerRadii Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Array) return new CornerRadii(ReadExpr(e), null);
            var corners = new List<Expr>();
            foreach (var item in e.EnumerateArray()) corners.Add(ReadExpr(item));
            return new CornerRadii(null, corners);
        }

        public override void Write(Utf8JsonWriter writer, CornerRadii value, JsonSerializerOptions options)
        {
            if (value.Corners is { } corners)
            {
                writer.WriteStartArray();
                foreach (var c in corners) WriteExpr(writer, c);
                writer.WriteEndArray();
            }
            else WriteExpr(writer, value.All ?? throw new JsonException("Corner radii need a value."));
        }
    }

    private sealed class TextContentConverter : JsonConverter<TextContent>
    {
        public override TextContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Array) return new TextContent(ReadExpr(e), null);
            return new TextContent(null, e.Deserialize<List<TextSpan>>(options));
        }

        public override void Write(Utf8JsonWriter writer, TextContent value, JsonSerializerOptions options)
        {
            if (value.Spans is { } spans) JsonSerializer.Serialize(writer, spans, options);
            else WriteExpr(writer, value.Plain ?? throw new JsonException("Text needs a value."));
        }
    }

    private sealed class ColumnRefConverter : JsonConverter<ColumnRef>
    {
        public override ColumnRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => new ColumnRef(null, reader.GetString()),
                JsonTokenType.Number => new ColumnRef(new IntegralInt32Converter().Read(ref reader, typeof(int), options), null),
                _ => throw new JsonException("A column is an index or a name."),
            };

        public override void Write(Utf8JsonWriter writer, ColumnRef value, JsonSerializerOptions options)
        {
            if (value.Index is { } index) writer.WriteNumberValue(index);
            else writer.WriteStringValue(value.Name);
        }
    }
}
