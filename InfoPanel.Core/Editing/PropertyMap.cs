using InfoPanel.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Serialization;
using SkiaSharp;

namespace InfoPanel.Editing;

/// <summary>The JSON type of an item or profile property.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PropertyJsonType>))]
public enum PropertyJsonType
{
    [JsonStringEnumMemberName("int")] Int,
    [JsonStringEnumMemberName("uint")] UInt,
    [JsonStringEnumMemberName("float")] Float,
    [JsonStringEnumMemberName("double")] Double,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("enum")] Enum,
}

/// <summary>One property as the API exposes it: camelCase <see cref="Name"/>, its JSON type, enum names, read-only.</summary>
public sealed record PropertyDescriptor(
    string Name,
    string ClrName,
    PropertyInfo Property,
    PropertyJsonType Type,
    IReadOnlyList<string>? EnumValues,
    bool ReadOnly)
{
    /// <summary>Accepts JSON null: a <c>string?</c> or <c>Nullable&lt;T&gt;</c> property.</summary>
    public bool Nullable { get; init; }

    /// <summary>The inclusive range a numeric value must fall in (<see cref="PropertyMap"/>'s limits); null when any value of the type is accepted.</summary>
    public (double Min, double Max)? Range { get; init; }

    /// <summary>Checks a string value's format beyond its JSON type (<c>tableFormat</c>); returns why it is refused, or null.</summary>
    public Func<string, string?>? Format { get; init; }

    /// <summary>
    /// A string property whose name ends in <c>Color</c>, optionally followed by digits (<c>GradientColor2</c>): values
    /// must parse with <see cref="SKColor.TryParse"/>.
    /// </summary>
    public bool IsColor => Type == PropertyJsonType.String && ClrName.TrimEnd("0123456789".ToCharArray()).EndsWith("Color", StringComparison.Ordinal);

    /// <summary>The property's current value as JSON (enums by name; non-finite numbers as null).</summary>
    public JsonNode? Read(object instance) => PropertyMap.ToJson(Property.GetValue(instance), Type);

    /// <summary>Sets a value from <see cref="FromJson"/> through the model setter.</summary>
    /// <exception cref="PatchException">The setter refused the value by throwing (a calendar's <c>format</c> of <c>"Q"</c>).</exception>
    public void Write(object instance, object? value)
    {
        try
        {
            Property.SetValue(instance, value);
        }
        catch (TargetInvocationException ex)
        {
            throw new PatchException(Name, $"'{Name}' can't be set to that value: {(ex.InnerException ?? ex).Message}");
        }
    }

    /// <summary>
    /// Converts a JSON value to the property's CLR value, strictly: integers for int and uint (in range, no
    /// fraction), finite numbers for float and double, both within <see cref="Range"/> when there is one,
    /// <c>true</c>/<c>false</c>, strings XML 1.0 can carry (<see cref="PropertyMap.IsXmlText"/>) and that pass
    /// <see cref="Format"/>, enum names (exact case), colours that <see cref="SKColor.TryParse"/> accepts, and null only
    /// for nullable properties.
    /// </summary>
    /// <exception cref="PatchException">The value doesn't fit; <see cref="EditException.Key"/> is <see cref="Name"/>.</exception>
    public object? FromJson(JsonNode? node)
    {
        var value = Convert(node);
        if (Range is var (min, max) && value is int or uint or float or double)
        {
            var number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (number < min || number > max)
                throw new PatchException(Name, $"'{Name}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return value;
    }

    private object? Convert(JsonNode? node)
    {
        if (node == null)
        {
            if (Nullable)
                return null;
            throw new PatchException(Name, $"'{Name}' can't be null.");
        }

        if (node is not JsonValue)
            throw new PatchException(Name, $"'{Name}' must be a {Describe()}, not an {(node is JsonArray ? "array" : "object")}.");

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(node);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A string with an unpaired surrogate escape ("\uD800") parses but can't be read as text.
            throw new PatchException(Name, $"'{Name}' is not valid text (an unpaired surrogate).");
        }

        switch (Type)
        {
            case PropertyJsonType.Int when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i):
                return i;
            case PropertyJsonType.UInt when element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var u):
                return u;
            case PropertyJsonType.Float when element.ValueKind == JsonValueKind.Number
                                             && element.GetDouble() is var f && double.IsFinite(f) && Math.Abs(f) <= float.MaxValue:
                return (float)f;
            case PropertyJsonType.Double when element.ValueKind == JsonValueKind.Number && element.GetDouble() is var d && double.IsFinite(d):
                return d;
            case PropertyJsonType.Bool when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return element.GetBoolean();
            case PropertyJsonType.String when element.ValueKind == JsonValueKind.String:
                string text;
                try
                {
                    text = element.GetString()!;
                }
                catch (InvalidOperationException)
                {
                    throw new PatchException(Name, $"'{Name}' is not valid text (an unpaired surrogate).");
                }

                if (!PropertyMap.IsXmlText(text))
                    throw new PatchException(Name, $"'{Name}' contains a character a profile can't store (a control character or an unpaired surrogate).");
                if (IsColor && !SKColor.TryParse(text, out _))
                    throw new PatchException(Name, $"'{Name}' must be a colour such as #AARRGGBB; '{text}' isn't one.");
                if (Format?.Invoke(text) is { } problem)
                    throw new PatchException(Name, problem);
                return text;
            case PropertyJsonType.Enum when element.ValueKind == JsonValueKind.String:
                var name = element.GetString()!;
                if (!EnumValues!.Contains(name, StringComparer.Ordinal))
                    throw new PatchException(Name, $"'{Name}' must be one of {string.Join(", ", EnumValues!)}; '{name}' isn't.");
                var enumType = System.Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
                return Enum.Parse(enumType, name, ignoreCase: false);
        }

        throw new PatchException(Name, $"'{Name}' must be a {Describe()}.");
    }

    private string Describe() => Type switch
    {
        PropertyJsonType.Int => "whole number (int)",
        PropertyJsonType.UInt => "non-negative whole number (uint)",
        PropertyJsonType.Float or PropertyJsonType.Double => "finite number",
        PropertyJsonType.Bool => "boolean",
        PropertyJsonType.Enum => "string naming one of " + string.Join(", ", EnumValues!),
        _ => "string",
    };
}

/// <summary>
/// The properties of a model type the API reads and patches: public instance properties that have a
/// public getter and setter somewhere in the hierarchy and no <see cref="XmlIgnoreAttribute"/>, named in camelCase,
/// ordered from the base class down in declaration order. A property redeclared with <c>new</c> is read through
/// its most-derived declaration and is read-only when that one has no public setter.
/// <list type="bullet">
/// <item>Excluded: <c>Guid</c> (exposed as the item or profile <c>id</c>), <c>DisplayItems</c> and <c>Images</c>
/// (exposed as <c>children</c> and <c>images</c>), and <c>Profile.TargetWindow</c> (an object; window placement is
/// not edited in step 1).</item>
/// <item>Read-only: <c>Radius</c>, <c>ReadOnly</c>, <c>DisplayItemsCount</c>, and <c>Type</c>/<c>HttpUrl</c> on
/// <see cref="HttpImageDisplayItem"/>.</item>
/// <item>Types: int, uint, float, double, bool, string and enums. Any other type throws when the map is built,
/// so a model change cannot silently drop a property.</item>
/// <item>Limits (<see cref="PropertyDescriptor.Range"/>, by property name on every type that has it): the values the
/// renderer can draw without overflowing or allocating without bound; see <see cref="Ranges"/>.</item>
/// </list>
/// </summary>
public sealed class PropertyMap
{
    private static readonly ConcurrentDictionary<Type, PropertyMap> Cache = new();

    private static readonly HashSet<string> Excluded = ["Guid", "DisplayItems", "Images"];

    private static readonly Dictionary<Type, HashSet<string>> ExcludedByType = new()
    {
        [typeof(Profile)] = ["TargetWindow"],
    };

    private static readonly HashSet<string> ReadOnlyNames = ["Radius", "ReadOnly", "DisplayItemsCount"];

    private static readonly Dictionary<Type, HashSet<string>> ReadOnlyByType = new()
    {
        [typeof(HttpImageDisplayItem)] = ["Type", "HttpUrl"],
    };

    /// <summary>The largest coordinate an item may be patched to (either sign).</summary>
    public const int MaxCoordinate = 1_000_000;

    /// <summary>The largest item or profile width and height, and the largest stroke, step, radius and spacing.</summary>
    public const int MaxSize = InfoPanel.Persistence.ProfileStore.MaxDimension;

    /// <summary>
    /// Numeric limits by CLR property name, applied wherever the name occurs. Generous: well past anything a panel
    /// shows, low enough that the renderer's int arithmetic and bitmap sizes stay sane. Lower bounds of 0 keep the
    /// 1.x setters' own handling of 0 (a chart <c>width</c> of 0 is ignored, an image <c>width</c> of 0 means its
    /// natural size).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (double Min, double Max)> Ranges = new Dictionary<string, (double, double)>
    {
        ["X"] = (-MaxCoordinate, MaxCoordinate),
        ["Y"] = (-MaxCoordinate, MaxCoordinate),
        ["Width"] = (0, MaxSize),
        ["Height"] = (0, MaxSize),
        ["FontSize"] = (0, 2048),
        ["FontScale"] = (0.1, 10),
        ["Thickness"] = (0, MaxSize),
        ["Step"] = (0, MaxSize),
        ["FrameThickness"] = (0, MaxSize),
        ["CornerRadius"] = (0, MaxSize),
        ["GlowRadius"] = (0, 512),
        ["MarqueeSpacing"] = (0, MaxSize),
        ["MarqueeSpeed"] = (0, 10_000),
        ["MaxRows"] = (0, 1000),
        ["Scale"] = (0, 1000),
        ["Volume"] = (0, 100),
    };

    /// <summary>String formats checked beyond the JSON type, by CLR property name.</summary>
    private static readonly Dictionary<string, Func<string, string?>> Formats = new()
    {
        ["TableFormat"] = CheckTableFormat,
    };

    private readonly Dictionary<string, PropertyDescriptor> _byName;

    private PropertyMap(Type type, IReadOnlyList<PropertyDescriptor> properties)
    {
        Type = type;
        Properties = properties;
        _byName = properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public Type Type { get; }

    public IReadOnlyList<PropertyDescriptor> Properties { get; }

    public static PropertyMap For(Type type) => Cache.GetOrAdd(type, Build);

    public bool TryGet(string name, out PropertyDescriptor descriptor) => _byName.TryGetValue(name, out descriptor!);

    /// <summary>Every property's value, by camelCase name.</summary>
    public JsonObject Read(object instance)
    {
        var result = new JsonObject();
        foreach (var property in Properties)
            result[property.Name] = property.Read(instance);
        return result;
    }

    internal static JsonNode? ToJson(object? value, PropertyJsonType type) => value switch
    {
        null => null,
        float f => float.IsFinite(f) ? JsonValue.Create(f) : null,
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        Enum e => JsonValue.Create(e.ToString()),
        int i => JsonValue.Create(i),
        uint u => JsonValue.Create(u),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        _ => throw new NotSupportedException($"Unsupported property value {value.GetType()} for {type}."),
    };

    /// <summary>
    /// True when every character of <paramref name="text"/> is one XML 1.0 can carry: no control characters other
    /// than tab, line feed and carriage return, no U+FFFE/U+FFFF, and surrogates only in pairs. Profiles are XML, so
    /// anything else could not be saved (or snapshotted for undo).
    /// </summary>
    public static bool IsXmlText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
                continue;
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                i++;
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// A table's <c>tableFormat</c>: <c>column:width</c> pairs separated by <c>|</c>, each a non-negative integer
    /// column index and a width of 0–<see cref="MaxSize"/> (<c>0:120|2:60</c>), or empty. A column the table doesn't
    /// have is fine (it is skipped when drawn).
    /// </summary>
    internal static string? CheckTableFormat(string format)
    {
        if (format.Length == 0)
            return null;

        foreach (var part in format.Split('|'))
        {
            var split = part.Split(':');
            if (split.Length != 2
                || !IsDigits(split[0]) || !int.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !IsDigits(split[1]) || !int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                || width > MaxSize)
            {
                return $"'tableFormat' must be column:width pairs separated by '|', such as 0:120|2:60 (widths up to {MaxSize}); '{part}' isn't one.";
            }
        }

        return null;

        static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
    }

    /// <summary>A binding value from <c>SensorKeys.ToBindingProps</c> as JSON, the same way properties are written.</summary>
    public static JsonNode? ValueToJson(object? value) => value switch
    {
        null => null,
        _ when TryGetJsonType(value.GetType(), out var type) => ToJson(value, type),
        _ => throw new NotSupportedException($"Unsupported value type {value.GetType()}."),
    };

    private static PropertyMap Build(Type type)
    {
        var nullability = new NullabilityInfoContext(); // not thread-safe; Build can run concurrently
        var chain = new List<Type>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            chain.Insert(0, t);

        var excluded = new HashSet<string>(Excluded);
        var readOnly = new HashSet<string>(ReadOnlyNames);
        foreach (var t in chain)
        {
            if (ExcludedByType.TryGetValue(t, out var e))
                excluded.UnionWith(e);
            if (ReadOnlyByType.TryGetValue(t, out var r))
                readOnly.UnionWith(r);
        }

        // Name → (most-derived declaration, some declaration has public get+set without XmlIgnore), in first-seen order.
        var order = new List<string>();
        var found = new Dictionary<string, (PropertyInfo Property, bool Eligible)>();
        foreach (var t in chain)
        {
            var declared = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken);

            foreach (var p in declared)
            {
                var eligible = p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
                    && p.GetCustomAttribute<XmlIgnoreAttribute>() == null;

                if (found.TryGetValue(p.Name, out var previous))
                {
                    found[p.Name] = (p, previous.Eligible || eligible);
                }
                else
                {
                    order.Add(p.Name);
                    found[p.Name] = (p, eligible);
                }
            }
        }

        var properties = new List<PropertyDescriptor>();
        foreach (var name in order)
        {
            var (property, eligible) = found[name];
            if (!eligible || excluded.Contains(name) || property.GetMethod?.IsPublic != true)
                continue;

            // A redeclaration whose getter is not public or which is XmlIgnore'd hides the base property.
            if (property.GetCustomAttribute<XmlIgnoreAttribute>() != null)
                continue;

            if (!TryGetJsonType(property.PropertyType, out var jsonType))
            {
                throw new NotSupportedException(
                    $"{type.Name}.{name} has type {property.PropertyType}, which the API cannot expose. " +
                    "Exclude it in PropertyMap or add the type.");
            }

            var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            properties.Add(new PropertyDescriptor(
                JsonNamingPolicy.CamelCase.ConvertName(name),
                name,
                property,
                jsonType,
                jsonType == PropertyJsonType.Enum ? Enum.GetNames(enumType) : null,
                readOnly.Contains(name) || property.SetMethod?.IsPublic != true)
            {
                Nullable = nullability.Create(property).WriteState == NullabilityState.Nullable,
                Range = jsonType is PropertyJsonType.Int or PropertyJsonType.UInt or PropertyJsonType.Float or PropertyJsonType.Double
                        && Ranges.TryGetValue(name, out var range) ? range : null,
                Format = jsonType == PropertyJsonType.String ? Formats.GetValueOrDefault(name) : null,
            });
        }

        return new PropertyMap(type, properties);
    }

    private static bool TryGetJsonType(Type clrType, out PropertyJsonType type)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t.IsEnum)
        {
            type = PropertyJsonType.Enum;
            return true;
        }

        type = Type.GetTypeCode(t) switch
        {
            TypeCode.Int32 => PropertyJsonType.Int,
            TypeCode.UInt32 => PropertyJsonType.UInt,
            TypeCode.Single => PropertyJsonType.Float,
            TypeCode.Double => PropertyJsonType.Double,
            TypeCode.Boolean => PropertyJsonType.Bool,
            TypeCode.String => PropertyJsonType.String,
            _ => (PropertyJsonType)(-1),
        };
        return (int)type >= 0;
    }
}
