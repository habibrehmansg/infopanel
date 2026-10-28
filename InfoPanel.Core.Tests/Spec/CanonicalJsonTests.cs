using System.Globalization;
using System.Text;
using System.Text.Json;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// The formats' one canonical form (spec/docs/limits.md#sizes): RFC 8785 (JCS). The vectors are RFC 8785's (Appendix B's
/// numbers, section 3.2.4's sample, section 3.2.3's member order), checked against ECMAScript's own JSON.stringify.
/// </summary>
public sealed class CanonicalJsonTests
{
    /// <summary>A JSON <c>\u</c> escape of <paramref name="hex"/>, built so no source text holds one.</summary>
    private static string U(string hex) => "\\" + "u" + hex;

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    private static string Canonical(string json) => CanonicalJson.Write(Parse(json));

    [Theory]
    [InlineData("0000000000000000", "0")]
    [InlineData("8000000000000000", "0")]
    [InlineData("0000000000000001", "5e-324")]
    [InlineData("8000000000000001", "-5e-324")]
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
    [InlineData("ffefffffffffffff", "-1.7976931348623157e+308")]
    [InlineData("4340000000000000", "9007199254740992")]
    [InlineData("c340000000000000", "-9007199254740992")]
    [InlineData("4430000000000000", "295147905179352830000")]
    [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
    [InlineData("44b52d02c7e14af6", "1e+23")]
    [InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
    [InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("41b3de4355555553", "333333333.3333332")]
    [InlineData("41b3de4355555554", "333333333.33333325")]
    [InlineData("41b3de4355555555", "333333333.3333333")]
    [InlineData("41b3de4355555556", "333333333.3333334")]
    [InlineData("41b3de4355555557", "333333333.33333343")]
    [InlineData("becbf647612f3696", "-0.0000033333333333333333")]
    [InlineData("43143ff3c1cb0959", "1424953923781206.2")]
    public void Rfc8785_NumberVectors(string bits, string expected)
    {
        var d = BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        Assert.Equal(expected, CanonicalJson.Number(d));
        Assert.Equal(expected, Canonical(d.ToString("R", CultureInfo.InvariantCulture)));
        Assert.Equal(expected, Canonical(d.ToString("E16", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Rfc8785_TheSampleObject()
    {
        // Section 3.2.4: numbers, a string of escapes (one of each kind) and literals, members out of order.
        var str = U("20ac") + "$" + U("000F") + U("000a") + "A'" + U("0042") + U("0022") + U("005c") + "\\\\" + "\\\"" + "\\/";
        var sample = """{"numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001], "string": """ + "\"" + str + "\"" + """, "literals": [null, true, false]}""";
        // JSON.stringify's output, as RFC 8785 gives it.
        var expected = Convert.FromHexString(
            "7b226c69746572616c73223a5b6e756c6c2c747275652c66616c73655d2c226e756d62657273223a5b3333333333333333332e333333333333332c31652b33302c342e352c302e3030322c31652d32375d2c22737472696e67223a22e282ac245c75303030665c6e4127425c225c5c5c5c5c222f227d");
        Assert.Equal(Encoding.UTF8.GetString(expected), Canonical(sample));
    }

    [Fact]
    public void Rfc8785_MembersAreSortedByUtf16CodeUnits()
    {
        // Section 3.2.3: the emoji (a surrogate pair, D83D DE00) sorts before U+FB33, though its code point is after it.
        var json = "{\"" + U("20ac") + "\": \"Euro Sign\", \"" + U("000d") + "\": \"Carriage Return\", \"" + U("fb33") + "\": \"Hebrew Letter Dalet With Dagesh\", "
            + "\"1\": \"One\", \"" + U("d83d") + U("de00") + "\": \"Emoji: Grinning Face\", \"" + U("0080") + "\": \"Control\", \"" + U("00f6") + "\": \"Latin Small Letter O With Diaeresis\"}";
        var order = Parse(Canonical(json)).EnumerateObject().Select(p => p.Value.GetString()).ToArray();
        Assert.Equal(["Carriage Return", "One", "Control", "Latin Small Letter O With Diaeresis", "Euro Sign", "Emoji: Grinning Face", "Hebrew Letter Dalet With Dagesh"], order);
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("2E1", "20")]
    [InlineData("1E+2", "100")]
    [InlineData("100.000", "100")]
    [InlineData("-0", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("0.0000001", "1e-7")]
    [InlineData("0.000000123", "1.23e-7")]
    [InlineData("1e-6", "0.000001")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("0.0000010", "0.000001")]
    [InlineData("-0.000001", "-0.000001")]
    [InlineData("0.00001234", "0.00001234")]
    [InlineData("1e15", "1000000000000000")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("1e21", "1e+21")]
    [InlineData("1.5e300", "1.5e+300")]
    [InlineData("123456789012345678", "123456789012345680")]
    [InlineData("12345678901234567890", "12345678901234567000")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("1.00000000000000001", "1")]
    [InlineData("123456789012345.6", "123456789012345.6")]
    [InlineData("0.12345678901234567", "0.12345678901234566")]
    [InlineData("0.30000000000000004", "0.30000000000000004")]
    [InlineData("0.1", "0.1")]
    [InlineData("4.35", "4.35")]
    [InlineData("-123.456", "-123.456")]
    [InlineData("5e-324", "5e-324")]
    public void ANumbersSpelling_IsItsDoublesCanonicalForm(string spelled, string expected)
    {
        // ECMAScript's Number.prototype.toString of the double nearest the text, whatever the text.
        Assert.Equal(expected, Canonical(spelled));
        Assert.Equal("[" + expected + "]", Canonical("[ " + spelled + " ]"));
    }

    [Fact]
    public void Strings_HaveOnlyTheEscapesJcsGives()
    {
        // Escapes decoded; ", \ and the C0 controls escaped (the short forms where JSON has them); DEL, U+2028 and every
        // other character written as it is, in UTF-8.
        Assert.Equal("\"é/A\"", Canonical("\"" + U("00e9") + "\\/" + U("0041") + "\""));
        Assert.Equal("\"" + U("001f") + "\\t\\b\\f\\n\\r" + U("0000") + "\"", Canonical("\"" + U("001F") + U("0009") + U("0008") + U("000C") + U("000A") + U("000D") + U("0000") + "\""));
        Assert.Equal("\"" + (char)0x7f + (char)0x2028 + "\"", Canonical("\"" + U("007f") + U("2028") + "\""));
        Assert.Equal("\"\\\"\\\\\"", Canonical("\"" + U("0022") + U("005C") + "\""));
        Assert.Equal("{\"é\":\"" + U("0001") + "\"}", Canonical("{ \"" + U("00e9") + "\" : \"" + U("0001") + "\" }"));
    }

    [Theory]
    [InlineData("scene/valid")]
    [InlineData("document/valid")]
    [InlineData("document/placeholder")]
    [InlineData("commit/valid")]
    public void TheSizeIsTheWrittenLength_AndTheFormIsAFixedPoint(string folder)
    {
        // Size counts what Utf8 writes, without writing it; writing the canonical form again changes nothing.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(SpecExampleTests.SpecDir, "examples", folder), "*.json"))
        {
            var value = Parse(File.ReadAllText(file));
            var bytes = CanonicalJson.Utf8(value);
            Assert.Equal(bytes.LongLength, CanonicalJson.Size(value));
            Assert.Equal(bytes, CanonicalJson.Utf8(Parse(Encoding.UTF8.GetString(bytes))));
            Assert.Equal(Encoding.UTF8.GetString(bytes), CanonicalJson.Write(value));
        }
    }

    [Fact]
    public void ShortDecimals_AreCopied_OnlyWhenTheyAreAlreadyCanonical()
    {
        // The writer copies a number's text only when it is its double's canonical form: every spelling of these
        // decimals, padded, signed, with a zero or an exponent, comes out as the conversion gives it.
        foreach (var whole in new[] { "0", "1", "7", "10", "123", "999999999999999", "1000000000000000", "123456789012345678901" })
            foreach (var fraction in new[] { "", ".5", ".05", ".50", ".000001", ".0000001", ".00000123", ".1234567890123", ".12345678901234567", ".0" })
                foreach (var sign in new[] { "", "-" })
                    foreach (var exponent in new[] { "", "e0", "E2", "e-7", "e+21" })
                    {
                        var spelled = sign + whole + fraction + exponent;
                        var value = JsonNumber.Parse(Encoding.ASCII.GetBytes(spelled));
                        Assert.Equal(CanonicalJson.Number(value), Canonical(spelled));
                        if (CanonicalJson.IsCanonical(Encoding.ASCII.GetBytes(spelled))) Assert.Equal(CanonicalJson.Number(value), spelled);
                    }
    }

    [Fact]
    public void RandomSpellings_AreTheDoublesTheyReadAs()
    {
        // However a number is spelled (shortest, fixed, exponent, padded with zeros), it is written as its double.
        var random = new Random(8785);
        string[] formats = ["R", "G15", "G17", "F0", "F3", "F6", "E3", "E16", "0.000000000000", "0.##########E+0"];
        for (var i = 0; i < 20_000; i++)
        {
            var d = (random.Next(4)) switch
            {
                0 => Math.Round(random.NextDouble() * 2_000 - 1_000, random.Next(0, 6)),
                1 => (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-12, 25)),
                2 => random.Next(-1_000_000, 1_000_000),
                _ => BitConverter.Int64BitsToDouble(random.NextInt64()),
            };
            if (!double.IsFinite(d)) continue;
            var spelled = d.ToString(formats[random.Next(formats.Length)], CultureInfo.InvariantCulture);
            if (random.Next(4) == 0 && spelled.Contains('.') && !spelled.Contains('E')) spelled += "000";
            var value = JsonNumber.Parse(Encoding.ASCII.GetBytes(spelled));
            if (!double.IsFinite(value)) continue;
            Assert.Equal(CanonicalJson.Number(value), Canonical(spelled));
        }
    }
}
