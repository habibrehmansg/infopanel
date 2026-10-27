using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InfoPanel.Import;

/// <summary>The bounds every 1.x XML document is read within (<see cref="SafeXml"/>).</summary>
public sealed record XmlLimits(int MaxDepth, int MaxElements)
{
    public static XmlLimits Default { get; } = new(ImportLimits.Default.XmlDepth, ImportLimits.Default.XmlElements);

    public static XmlLimits From(ImportLimits limits) => new(limits.XmlDepth, limits.XmlElements);
}

/// <summary>
/// An XML document refused before or while it was read: a DTD (and so any entity), malformed XML, or more elements or
/// deeper nesting than its <see cref="XmlLimits"/>. <see cref="Line"/> is where, when known.
/// </summary>
public sealed class XmlRefusedException(string message, int? line = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Line { get; } = line;
}

/// <summary>
/// Reads 1.x XML safely. A DTD is prohibited (so no entity can be declared or expanded) and nothing is resolved
/// (no resolver, so no external file or URL is ever read); 1.x never wrote a DTD. A streaming pre-pass counts elements
/// and depth before the <see cref="XDocument"/> is built, so every recursive walk after it (the serializer, the item
/// tree, the item JSON) is bounded by <see cref="XmlLimits.MaxDepth"/>. Byte limits are the callers': they read at most
/// their limit before handing the bytes here.
/// </summary>
public static class SafeXml
{
    /// <summary>The reader settings: DTD prohibited, no resolver, no entity characters.</summary>
    public static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        CloseInput = false,
    };

    /// <summary>Loads <paramref name="bytes"/> (any encoding XML declares) with line information and whitespace kept.</summary>
    /// <exception cref="XmlRefusedException">A DTD, malformed XML, or over <paramref name="limits"/>.</exception>
    public static XDocument Load(byte[] bytes, XmlLimits? limits = null)
    {
        limits ??= XmlLimits.Default;
        using (var stream = new MemoryStream(bytes, writable: false))
            PrePass(stream, limits);
        using (var stream = new MemoryStream(bytes, writable: false))
            return Build(stream);
    }

    /// <inheritdoc cref="Load(byte[], XmlLimits)"/>
    public static XDocument Load(string text, XmlLimits? limits = null)
    {
        limits ??= XmlLimits.Default;
        using (var reader = new StringReader(text))
            PrePass(reader, limits);
        using (var reader = new StringReader(text))
            return Build(reader);
    }

    /// <summary>Reads <paramref name="stream"/> to its end and loads it (<see cref="Load(byte[], XmlLimits)"/>).</summary>
    public static XDocument Load(Stream stream, XmlLimits? limits = null)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), limits);
    }

    private static void PrePass(Stream stream, XmlLimits limits)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings());
        Count(reader, limits);
    }

    private static void PrePass(TextReader text, XmlLimits limits)
    {
        using var reader = XmlReader.Create(text, ReaderSettings());
        Count(reader, limits);
    }

    private static void Count(XmlReader reader, XmlLimits limits)
    {
        var elements = 0L;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (++elements > limits.MaxElements)
                    throw new XmlRefusedException($"The XML has more than {limits.MaxElements} elements.", LineOf(reader));
                // XmlReader.Depth is 0 for the root element, which is at depth 1 here.
                if (reader.Depth + 1 > limits.MaxDepth)
                    throw new XmlRefusedException($"The XML nests elements deeper than {limits.MaxDepth} levels.", LineOf(reader));
            }
        }
        catch (XmlException ex)
        {
            throw Refused(ex);
        }
    }

    private static XDocument Build(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, ReaderSettings());
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw Refused(ex);
        }
    }

    private static XDocument Build(TextReader text)
    {
        try
        {
            using var reader = XmlReader.Create(text, ReaderSettings());
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw Refused(ex);
        }
    }

    private static XmlRefusedException Refused(XmlException ex) =>
        new("The XML can't be read: " + ex.Message, ex.LineNumber > 0 ? ex.LineNumber : null, ex);

    private static int? LineOf(XmlReader reader) => reader is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    /// <summary>The 1-based line of <paramref name="node"/> in the document it was loaded from, or null.</summary>
    public static int? LineOf(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    /// <summary>UTF-8 without a byte order mark, for tests and messages.</summary>
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);
}
