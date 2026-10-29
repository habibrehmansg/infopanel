using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using InfoPanel.Spec;

namespace InfoPanel.Persistence;

/// <summary>
/// A member of a JSON object as its file has it: <see cref="NameToken"/> is the name's token as written (quotes and
/// escapes included), <see cref="Name"/> its text (null when it holds none, such as a lone surrogate), and
/// <see cref="Value"/> the value's JSON text, compacted.
/// </summary>
public sealed record JsonMemberText(byte[] NameToken, string? Name, byte[] Value);

/// <summary>
/// How the engine reads and rewrites its own files (docs/2.0/data-folder.md, "The engine's files are read
/// tolerantly"): members it doesn't know are kept as JSON text, exactly as written but for the whitespace between tokens,
/// and written back as that text, so a value of any kind (a string or a name that holds no text included) survives a
/// rewrite, never re-nested or re-indented. Every engine file is read at one depth, <see cref="MaxDepth"/> (the
/// formats' <c>jsonDepth</c>); what is kept counts toward the file's own byte cap.
/// </summary>
public static class EngineJson
{
    /// <summary>The nesting depth every engine file is read at: <c>jsonDepth</c>.</summary>
    public const int MaxDepth = SpecJson.SchemaDepth;

    /// <summary>For a <see cref="JsonDocument"/> or a serializer reading an engine file.</summary>
    public static JsonDocumentOptions DocumentOptions => new() { MaxDepth = MaxDepth };

    /// <summary>
    /// The members of the JSON object that is all of <paramref name="json"/>, in order, each as written
    /// (<see cref="JsonMemberText"/>).
    /// </summary>
    /// <exception cref="JsonException">It isn't one well-formed JSON object nested at most <see cref="MaxDepth"/> deep.</exception>
    public static List<JsonMemberText> Members(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("It isn't a JSON object.");

        var members = new List<JsonMemberText>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var nameStart = (int)reader.TokenStartIndex;
            var nameToken = json.Slice(nameStart, reader.ValueSpan.Length + 2).ToArray();
            string? name;
            try
            {
                name = reader.GetString();
            }
            catch (InvalidOperationException)
            {
                name = null; // a name that holds no text (a lone surrogate): kept as written, never matched
            }

            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            members.Add(new JsonMemberText(nameToken, name, Compact(json[start..(int)reader.BytesConsumed])));
        }

        // Anything after the object is refused by the reader.
        while (reader.Read())
        {
        }

        return members;
    }

    /// <summary>The JSON text of each element of the JSON array that is all of <paramref name="json"/>, compacted.</summary>
    /// <exception cref="JsonException">It isn't one well-formed JSON array nested at most <see cref="MaxDepth"/> deep.</exception>
    public static List<byte[]> Elements(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("It isn't a JSON array.");

        var elements = new List<byte[]>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            elements.Add(Compact(json[start..(int)reader.BytesConsumed]));
        }

        while (reader.Read())
        {
        }

        return elements;
    }

    /// <summary>
    /// Whether <paramref name="json"/> is well-formed JSON nested deeper than <see cref="MaxDepth"/>: a file the engine
    /// can't hold whole rather than a damaged one (read without recursion, at any depth).
    /// </summary>
    public static bool IsTooDeep(ReadOnlySpan<byte> json)
    {
        try
        {
            return SpecJson.Depth(json) > MaxDepth;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A JSON object of <paramref name="members"/>, each written as it was read, in order.</summary>
    public static byte[] Object(IEnumerable<JsonMemberText> members) => Append("{}"u8, members);

    /// <summary>
    /// <paramref name="obj"/>, a JSON object the engine wrote, compact (ending in <c>}</c>), with
    /// <paramref name="members"/> after its own, each as it was read.
    /// </summary>
    public static byte[] Append(ReadOnlySpan<byte> obj, IEnumerable<JsonMemberText>? members)
    {
        using var output = new MemoryStream(obj.Length);
        output.Write(obj[..^1]);
        var empty = obj.Length == 2;
        foreach (var member in members ?? [])
        {
            if (!empty)
                output.WriteByte((byte)',');
            empty = false;
            output.Write(member.NameToken);
            output.WriteByte((byte)':');
            output.Write(member.Value);
        }

        output.WriteByte((byte)'}');
        return output.ToArray();
    }

    /// <summary>Well-formed JSON text without the whitespace between its tokens: never longer, and never re-nested.</summary>
    public static byte[] Compact(ReadOnlySpan<byte> json)
    {
        var output = new byte[json.Length];
        var length = 0;
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var b = json[i];
            if (inString)
            {
                output[length++] = b;
                if (b == '\\')
                    output[length++] = json[++i];
                else if (b == '"')
                    inString = false;
            }
            else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                output[length++] = b;
                inString = b == '"';
            }
        }

        return output.AsSpan(0, length).ToArray();
    }
}
