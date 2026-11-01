using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The protocol's messages to and from JSON, closed both ways. Reading refuses (<see cref="HelperProtocolException"/>)
/// anything nested deeper than <see cref="HelperProtocol.MaxDepth"/> (<c>tooDeep</c>), more sensors or hardware than
/// the caps (<c>tooLarge</c>), and (<c>malformed</c>) JSON that isn't one object, a type it doesn't know for its
/// direction, a member missing, unknown, repeated or of the wrong kind, a number past its type, a catalogue whose
/// references don't hold (a parent or hardware it doesn't list, parents that go round in a cycle), a sample whose lists
/// differ in length, a storage interval out of range. Writing checks the same and refuses a message over
/// <see cref="HelperProtocol.MaxFrameBytes"/>, so neither side sends what the other would refuse.
/// </summary>
public static class HelperCodec
{
    /// <summary>The JSON of <paramref name="request"/>.</summary>
    /// <exception cref="HelperProtocolException">The reader would refuse it.</exception>
    public static byte[] EncodeRequest(HelperRequest request)
    {
        Check(request);
        return Write(w =>
        {
            switch (request)
            {
                case HelloRequest hello:
                    w.WriteString("type", "hello");
                    w.WriteNumber("protocol", hello.Protocol);
                    break;
                case CatalogueRequest:
                    w.WriteString("type", "catalogue");
                    break;
                case SampleRequest:
                    w.WriteString("type", "sample");
                    break;
                case ConfigureRequest configure:
                    w.WriteString("type", "configure");
                    w.WriteBoolean("storage", configure.Storage);
                    w.WriteNumber("storageInterval", configure.StorageInterval);
                    break;
                default:
                    throw Malformed("No such request.");
            }
        });
    }

    /// <summary>The JSON of <paramref name="answer"/>.</summary>
    /// <exception cref="HelperProtocolException">The reader would refuse it, or it is over the frame cap (<c>tooLarge</c>).</exception>
    public static byte[] EncodeAnswer(HelperAnswer answer)
    {
        Check(answer);
        return Write(w =>
        {
            switch (answer)
            {
                case HelloAnswer hello:
                    w.WriteString("type", "hello");
                    w.WriteNumber("protocol", hello.Protocol);
                    w.WriteString("helper", hello.Helper);
                    w.WriteBoolean("elevated", hello.Elevated);
                    w.WriteString("driver", hello.Driver);
                    break;
                case CatalogueAnswer catalogue:
                    w.WriteString("type", "catalogue");
                    w.WriteNumber("version", catalogue.Version);
                    w.WriteStartArray("hardware");
                    foreach (var hardware in catalogue.Hardware)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", hardware.Id);
                        w.WriteString("name", hardware.Name);
                        w.WriteString("type", hardware.Type);
                        if (hardware.Parent is null)
                            w.WriteNull("parent");
                        else
                            w.WriteString("parent", hardware.Parent);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteStartArray("sensors");
                    foreach (var sensor in catalogue.Sensors)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", sensor.Id);
                        w.WriteString("name", sensor.Name);
                        w.WriteString("type", sensor.Type);
                        w.WriteNumber("index", sensor.Index);
                        w.WriteString("unit", sensor.Unit);
                        w.WriteString("hw", sensor.Hw);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    break;
                case SampleAnswer sample:
                    w.WriteString("type", "sample");
                    w.WriteNumber("version", sample.Version);
                    WriteValues(w, "value", sample.Value);
                    WriteValues(w, "min", sample.Min);
                    WriteValues(w, "max", sample.Max);
                    break;
                case ConfigureAnswer configure:
                    w.WriteString("type", "configure");
                    w.WriteBoolean("storage", configure.Storage);
                    w.WriteNumber("storageInterval", configure.StorageInterval);
                    break;
                case ErrorAnswer error:
                    w.WriteString("type", "error");
                    w.WriteString("code", error.Code);
                    w.WriteString("message", error.Message);
                    break;
                default:
                    throw Malformed("No such answer.");
            }
        });
    }

    /// <summary>The request <paramref name="json"/> holds.</summary>
    /// <exception cref="HelperProtocolException">It isn't one the protocol has, as it has it.</exception>
    public static HelperRequest DecodeRequest(ReadOnlyMemory<byte> json)
    {
        using var document = Parse(json);
        try
        {
            var message = new Members(document.RootElement);
            HelperRequest request = message.Type switch
            {
                "hello" => new HelloRequest(message.Only("protocol").Int("protocol")),
                "catalogue" => Bare(message, new CatalogueRequest()),
                "sample" => Bare(message, new SampleRequest()),
                "configure" => new ConfigureRequest(message.Only("storage", "storageInterval").Bool("storage"), message.Int("storageInterval")),
                _ => throw Malformed($"No request is called '{message.Type}'."),
            };
            Check(request);
            return request;
        }
        catch (InvalidOperationException)
        {
            throw Malformed("A string holds no text.");
        }
    }

    /// <summary>The answer <paramref name="json"/> holds.</summary>
    /// <exception cref="HelperProtocolException">It isn't one the protocol has, as it has it.</exception>
    public static HelperAnswer DecodeAnswer(ReadOnlyMemory<byte> json)
    {
        using var document = Parse(json);
        try
        {
            var message = new Members(document.RootElement);
            HelperAnswer answer = message.Type switch
            {
                "hello" => new HelloAnswer(
                    message.Only("protocol", "helper", "elevated", "driver").Int("protocol"),
                    message.String("helper"),
                    message.Bool("elevated"),
                    message.String("driver")),
                "catalogue" => Catalogue(message.Only("version", "hardware", "sensors")),
                "sample" => Sample(message.Only("version", "value", "min", "max")),
                "configure" => new ConfigureAnswer(message.Only("storage", "storageInterval").Bool("storage"), message.Int("storageInterval")),
                "error" => new ErrorAnswer(message.Only("code", "message").String("code"), message.String("message")),
                _ => throw Malformed($"No answer is called '{message.Type}'."),
            };
            Check(answer);
            return answer;
        }
        catch (InvalidOperationException)
        {
            throw Malformed("A string holds no text.");
        }
    }

    /// <summary>A request that has no member but its type.</summary>
    private static HelperRequest Bare(Members message, HelperRequest request)
    {
        message.Only();
        return request;
    }

    private static CatalogueAnswer Catalogue(Members message)
    {
        var hardwareList = message.Array("hardware", HelperProtocol.MaxHardware);
        var sensorList = message.Array("sensors", HelperProtocol.MaxSensors);
        var hardware = new List<HelperHardware>(hardwareList.GetArrayLength());
        foreach (var element in hardwareList.EnumerateArray())
        {
            var entry = new Members(element, typed: false).Only("id", "name", "type", "parent");
            hardware.Add(new HelperHardware(entry.String("id"), entry.String("name"), entry.String("type"), entry.NullableString("parent")));
        }

        var sensors = new List<HelperSensor>(sensorList.GetArrayLength());
        foreach (var element in sensorList.EnumerateArray())
        {
            var entry = new Members(element, typed: false).Only("id", "name", "type", "index", "unit", "hw");
            sensors.Add(new HelperSensor(entry.String("id"), entry.String("name"), entry.String("type"), entry.Int("index"), entry.String("unit"), entry.String("hw")));
        }

        return new CatalogueAnswer(message.Long("version"), hardware, sensors);
    }

    private static SampleAnswer Sample(Members message) =>
        new(message.Long("version"), Values(message.Array("value", HelperProtocol.MaxSensors)),
            Values(message.Array("min", HelperProtocol.MaxSensors)), Values(message.Array("max", HelperProtocol.MaxSensors)));

    private static List<float?> Values(JsonElement array)
    {
        var values = new List<float?>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Null)
                values.Add(null);
            else if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) && IsFinite(value))
                values.Add(value);
            else
                throw Malformed("A value is neither a number a float holds nor null.");
        }

        return values;
    }

    private static void WriteValues(Utf8JsonWriter w, string name, IReadOnlyList<float?> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
        {
            if (value is { } number)
                w.WriteNumberValue(number);
            else
                w.WriteNullValue();
        }

        w.WriteEndArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > HelperProtocol.MaxFrameBytes)
            throw new HelperProtocolException("tooLarge", $"The message is {buffer.WrittenCount} bytes; a frame holds at most {HelperProtocol.MaxFrameBytes}.");
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses <paramref name="json"/> after a first pass that refuses nesting past <see cref="HelperProtocol.MaxDepth"/>
    /// on its own code (the parse would refuse it too, as JSON it can't read).
    /// </summary>
    private static JsonDocument Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json.Span, new JsonReaderOptions { MaxDepth = HelperProtocol.MaxDepth + 1 });
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= HelperProtocol.MaxDepth)
                    throw new HelperProtocolException("tooDeep", $"The message nests deeper than {HelperProtocol.MaxDepth} levels.");
            }

            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = HelperProtocol.MaxDepth });
        }
        catch (JsonException e)
        {
            throw Malformed($"The message isn't JSON: {e.Message}");
        }
    }

    private static void Check(HelperRequest request)
    {
        if (request is ConfigureRequest configure)
            CheckInterval(configure.StorageInterval);
    }

    private static void Check(HelperAnswer answer)
    {
        switch (answer)
        {
            case HelloAnswer hello:
                Text(hello.Helper);
                Text(hello.Driver);
                break;
            case CatalogueAnswer catalogue:
                CheckVersion(catalogue.Version);
                if (catalogue.Hardware.Count > HelperProtocol.MaxHardware || catalogue.Sensors.Count > HelperProtocol.MaxSensors)
                    throw TooMany(Math.Max(catalogue.Hardware.Count, catalogue.Sensors.Count));

                var hardwareIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hardware in catalogue.Hardware)
                {
                    if (string.IsNullOrEmpty(hardware.Id) || !hardwareIds.Add(hardware.Id))
                        throw Malformed("Each piece of hardware has an identifier of its own.");
                    Text(hardware.Name);
                    Text(hardware.Type);
                }

                foreach (var hardware in catalogue.Hardware)
                {
                    if (hardware.Parent is { } parent && (parent == hardware.Id || !hardwareIds.Contains(parent)))
                        throw Malformed($"Hardware '{hardware.Id}' names a parent the catalogue doesn't list.");
                }

                if (CycleClosers(catalogue.Hardware.Select(h => h.Id).ToList(), catalogue.Hardware.Select(h => h.Parent).ToList()).Count > 0)
                    throw Malformed("The hardware's parents go round in a cycle.");

                var sensorIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sensor in catalogue.Sensors)
                {
                    if (string.IsNullOrEmpty(sensor.Id) || !sensorIds.Add(sensor.Id))
                        throw Malformed("Each sensor has an identifier of its own.");
                    if (sensor.Hw is null || !hardwareIds.Contains(sensor.Hw))
                        throw Malformed($"Sensor '{sensor.Id}' names hardware the catalogue doesn't list.");
                    Text(sensor.Name);
                    Text(sensor.Type);
                    Text(sensor.Unit);
                }

                break;
            case SampleAnswer sample:
                CheckVersion(sample.Version);
                if (sample.Value.Count > HelperProtocol.MaxSensors || sample.Min.Count > HelperProtocol.MaxSensors || sample.Max.Count > HelperProtocol.MaxSensors)
                    throw TooMany(Math.Max(sample.Value.Count, Math.Max(sample.Min.Count, sample.Max.Count)));
                if (sample.Min.Count != sample.Value.Count || sample.Max.Count != sample.Value.Count)
                    throw Malformed("A sample's value, min and max lists differ in length.");
                foreach (var list in new[] { sample.Value, sample.Min, sample.Max })
                {
                    foreach (var value in list)
                    {
                        if (value is { } number && !IsFinite(number))
                            throw Malformed("A value is not a finite number.");
                    }
                }

                break;
            case ConfigureAnswer configure:
                CheckInterval(configure.StorageInterval);
                break;
            case ErrorAnswer error:
                if (string.IsNullOrEmpty(error.Code))
                    throw Malformed("An error has a code.");
                Text(error.Message);
                break;
        }
    }

    /// <summary>
    /// The pieces of hardware whose parent closes a cycle: walking up from each piece in turn (iteratively, so a chain of
    /// any length is walked without recursion), the last piece on the walk whose parent is already on it. One per cycle,
    /// in the order the walks find them; empty when the parents form a forest. <paramref name="parents"/>[i] is the
    /// parent of <paramref name="ids"/>[i] (null for none); a parent that isn't listed ends the walk.
    /// </summary>
    public static List<int> CycleClosers(IReadOnlyList<string> ids, IReadOnlyList<string?> parents)
    {
        var index = new Dictionary<string, int>(ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
            index.TryAdd(ids[i], i);

        // 0 not walked yet, 1 on the current walk, 2 walked (it leads to no cycle, or its cycle is already counted).
        var state = new byte[ids.Count];
        var closers = new List<int>();
        var walk = new List<int>();
        for (var start = 0; start < ids.Count; start++)
        {
            walk.Clear();
            var at = start;
            while (at >= 0 && state[at] == 0)
            {
                state[at] = 1;
                walk.Add(at);
                at = parents[at] is { } parent && index.TryGetValue(parent, out var next) ? next : -1;
            }

            if (at >= 0 && state[at] == 1)
                closers.Add(walk[^1]);
            foreach (var walked in walk)
                state[walked] = 2;
        }

        return closers;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void Text(string? text)
    {
        if (text is null)
            throw Malformed("A text is missing.");
    }

    private static void CheckVersion(long version)
    {
        if (version < 1)
            throw Malformed("A catalogue version is 1 or more.");
    }

    private static void CheckInterval(int seconds)
    {
        if (seconds is < HelperProtocol.MinStorageInterval or > HelperProtocol.MaxStorageInterval)
            throw Malformed($"The storage interval is {HelperProtocol.MinStorageInterval} to {HelperProtocol.MaxStorageInterval} seconds.");
    }

    private static HelperProtocolException TooMany(int count) =>
        new("tooLarge", $"The message lists {count} entries; the protocol takes at most {HelperProtocol.MaxSensors}.");

    private static HelperProtocolException Malformed(string message) => new("malformed", message);

    /// <summary>A JSON object's members, each name once; with <c>typed</c>, its <c>type</c> member read first.</summary>
    private sealed class Members
    {
        private readonly Dictionary<string, JsonElement> _members = new(StringComparer.Ordinal);

        public Members(JsonElement element, bool typed = true)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw Malformed("A message is a JSON object.");

            foreach (var member in element.EnumerateObject())
            {
                if (!_members.TryAdd(member.Name, member.Value))
                    throw Malformed($"'{member.Name}' appears twice.");
            }

            if (typed)
            {
                Type = _members.TryGetValue("type", out var type) && type.ValueKind == JsonValueKind.String
                    ? type.GetString()!
                    : throw Malformed("A message's type is a string.");
                _members.Remove("type");
            }
        }

        public string Type { get; } = "";

        /// <summary>Refuses a member other than <paramref name="names"/>, or one of them missing.</summary>
        public Members Only(params string[] names)
        {
            foreach (var name in names)
            {
                if (!_members.ContainsKey(name))
                    throw Malformed($"'{name}' is missing.");
            }

            if (_members.Count != names.Length)
            {
                foreach (var name in _members.Keys)
                {
                    if (System.Array.IndexOf(names, name) < 0)
                        throw Malformed($"'{name}' isn't part of the message.");
                }
            }

            return this;
        }

        public int Int(string name) =>
            _members[name] is { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var value)
                ? value
                : throw Malformed($"'{name}' is a whole number.");

        public long Long(string name) =>
            _members[name] is { ValueKind: JsonValueKind.Number } element && element.TryGetInt64(out var value)
                ? value
                : throw Malformed($"'{name}' is a whole number.");

        public bool Bool(string name) =>
            _members[name].ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Malformed($"'{name}' is true or false."),
            };

        public string String(string name) =>
            _members[name] is { ValueKind: JsonValueKind.String } element ? element.GetString()! : throw Malformed($"'{name}' is a string.");

        public string? NullableString(string name) =>
            _members[name].ValueKind == JsonValueKind.Null ? null : String(name);

        public JsonElement Array(string name, int cap)
        {
            var element = _members[name];
            if (element.ValueKind != JsonValueKind.Array)
                throw Malformed($"'{name}' is a list.");
            if (element.GetArrayLength() > cap)
                throw TooMany(element.GetArrayLength());
            return element;
        }
    }
}
