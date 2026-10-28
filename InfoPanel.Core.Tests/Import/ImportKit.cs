using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A workspace in a temporary data folder, a sensor hub with no sources (every binding reads as unavailable), a fixed
/// font list, and builders for 1.x XML and archives.
/// </summary>
internal sealed class ImportKit : IDisposable
{
    public static readonly HashSet<string> Fonts = new(["Inter", "Arial", "Consolas"], StringComparer.OrdinalIgnoreCase);

    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public ImportKit()
    {
        Data = new DataPaths(Temp.Combine("data"));
        var documents = new DocumentStore(Data);
        var blobs = new BlobStore(Data);
        documents.Open();
        blobs.Open();
        Workspace = new Workspace(documents, blobs);
    }

    public TempDirectory Temp { get; } = new();
    public DataPaths Data { get; }
    public Workspace Workspace { get; }
    public SensorHub Sensors { get; } = new([]);

    public V1ReadContext Context(ImportLimits? limits = null) => new(Workspace, Sensors, limits, Fonts.Contains);

    public V1Profile ReadArchive(byte[] zip, ImportLimits? limits = null) => V1Reader.ReadArchive(new MemoryStream(zip), Context(limits));

    /// <summary>Whether <paramref name="bytes"/> are stored as a blob.</summary>
    public bool Stored(byte[] bytes) => Workspace.Blobs.Exists(BlobStore.IdOf(bytes));

    public int BlobCount => Directory.Exists(Data.BlobsDirectory) ? Directory.GetFiles(Data.BlobsDirectory).Length : 0;

    public void Dispose()
    {
        Workspace.Dispose();
        Temp.Dispose();
    }

    public static byte[] Asset(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets", name));

    /// <summary>A file that probes as MP4 video: not a media asset of the formats.</summary>
    public static readonly byte[] VideoStub = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'];

    /// <summary>A BMP header: not a media asset of the formats.</summary>
    public static readonly byte[] BmpStub = [(byte)'B', (byte)'M', 0x3A, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0x18, 0];

    /// <summary>An SVG outside the SVG profile (a script).</summary>
    public static readonly byte[] ScriptSvg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script></svg>");

    /// <summary>A profile's XML (one <c>Profile</c>), as 1.x writes it.</summary>
    public static string ProfileXml(Profile profile)
    {
        using var stream = new MemoryStream();
        ProfileSerializer.WriteProfile(stream, profile);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>An item list of the given item elements.</summary>
    public static string Items(params string[] items) =>
        $"""<?xml version="1.0" encoding="utf-8"?>""" + "\n" +
        $"""<ArrayOfDisplayItem xmlns:xsi="{Xsi}" xmlns:xsd="http://www.w3.org/2001/XMLSchema">""" + "\n" +
        string.Join("\n", items) + "\n</ArrayOfDisplayItem>";

    public static string Text(string name, string font = "Arial", string extra = "") =>
        $"""<DisplayItem xsi:type="TextDisplayItem"><Name>{name}</Name><Font>{font}</Font>{extra}</DisplayItem>""";

    public static string Image(string name, string path, bool relative = true, string type = "FILE") =>
        $"""<DisplayItem xsi:type="ImageDisplayItem"><Name>{name}</Name><Type>{type}</Type><FilePath>{System.Security.SecurityElement.Escape(path)}</FilePath><RelativePath>{(relative ? "true" : "false")}</RelativePath></DisplayItem>""";

    public static string Group(string name, params string[] children) =>
        $"""<DisplayItem xsi:type="GroupDisplayItem"><Name>{name}</Name><DisplayItems>{string.Concat(children)}</DisplayItems></DisplayItem>""";

    public static string Gauge(string name, params string[] frames) =>
        $"""<DisplayItem xsi:type="GaugeDisplayItem"><Name>{name}</Name><Images>{string.Concat(frames)}</Images></DisplayItem>""";

    public static string Frame(string name, string path, string type = "") =>
        $"""<ImageDisplayItem{type}><Name>{name}</Name><Type>FILE</Type><FilePath>{path}</FilePath><RelativePath>true</RelativePath></ImageDisplayItem>""";

    public static string Guide(string name, int x, int y, int length = 300, string color = "#FFFF0000", int rotation = 90, bool locked = false) =>
        $"""<DisplayItem xsi:type="GuideDisplayItem"><Name>{name}</Name><Hidden>false</Hidden><X>{x}</X><Y>{y}</Y><IsLocked>{(locked ? "true" : "false")}</IsLocked><Rotation>{rotation}</Rotation><Length>{length}</Length><Color>{color}</Color></DisplayItem>""";

    /// <summary>A 1.x archive: <c>Profile.xml</c>, <c>DisplayItems.xml</c>, then the given entries.</summary>
    public static byte[] V1Archive(string itemsXml, params (string Name, byte[] Bytes)[] entries) =>
        V1Archive(ProfileXml(new Profile("Imported", 640, 480)), itemsXml, entries);

    public static byte[] V1Archive(string profileXml, string itemsXml, params (string Name, byte[] Bytes)[] entries) =>
        ZipKit.Zip([("Profile.xml", Encoding.UTF8.GetBytes(profileXml)), ("DisplayItems.xml", Encoding.UTF8.GetBytes(itemsXml)), .. entries]);

    /// <summary>The diagnostics with <paramref name="code"/>.</summary>
    public static IReadOnlyList<ImportDiagnostic> With(V1Profile envelope, string code) => envelope.Diagnostics.Where(d => d.Code == code).ToList();

    public static ImportDiagnostic Single(V1Profile envelope, string code) => Assert.Single(With(envelope, code));

    /// <summary>The item JSON with every item's <c>id</c> left out (not the props' <c>id</c>) (1.x files carry none, so a read gives fresh ones).</summary>
    public static JsonNode WithoutIds(object nodes)
    {
        var json = JsonSerializer.SerializeToNode(nodes, SpecJson.Options)!;
        Strip(json);
        return json;

        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o.ContainsKey("kind"))
                        o.Remove("id");
                    foreach (var (_, child) in o.ToList())
                        Strip(child);
                    break;
                case JsonArray a:
                    foreach (var child in a)
                        Strip(child);
                    break;
            }
        }
    }
}
