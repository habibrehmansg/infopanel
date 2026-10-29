using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Import;

/// <summary>The 1.x folder of the Core tests' fixtures (<c>InfoPanel.Core.Tests/Fixtures/v1-folder</c>), and helpers for 1.x data.</summary>
internal static class V1Fixture
{
    public const string Desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The fixture folder, found above the test's output folder (the repository's checkout).</summary>
    public static string Folder { get; } = Find(Path.Combine("InfoPanel.Core.Tests", "Fixtures", "v1-folder"));

    /// <summary>The 1.x export with all 14 kinds the e2e tests import (<c>InfoPanel.Core.Tests/Fixtures/e2e/all-kinds.infopanel</c>).</summary>
    public static string AllKindsArchive { get; } = Find(Path.Combine("InfoPanel.Core.Tests", "Fixtures", "e2e", "all-kinds.infopanel"));

    private static string Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(path) || File.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Copies the fixture folder to <paramref name="target"/>.</summary>
    public static void CopyTo(string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(Folder, dir)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(Folder, file)));
    }

    /// <summary>Every file and folder under <paramref name="root"/>: names, kinds, sizes, write times and bytes.</summary>
    public static string TreeHash(string root)
    {
        var text = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(entry);
            text.Append(Path.GetRelativePath(root, entry)).Append('|').Append(info.Attributes).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|');
            if (File.Exists(entry))
                text.Append(info.Length).Append('|').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A profile's XML, as 1.x writes it.</summary>
    public static string ProfileXml(Profile profile)
    {
        using var stream = new MemoryStream();
        ProfileSerializer.WriteProfile(stream, profile);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>An item list of the given item elements.</summary>
    public static string Items(params string[] items) =>
        $"""<?xml version="1.0" encoding="utf-8"?><ArrayOfDisplayItem xmlns:xsi="{Xsi}" xmlns:xsd="http://www.w3.org/2001/XMLSchema">{string.Concat(items)}</ArrayOfDisplayItem>""";

    public static string Text(string name) => $"""<DisplayItem xsi:type="TextDisplayItem"><Name>{name}</Name><Font>Inter</Font></DisplayItem>""";

    /// <summary>A zip of the given entries.</summary>
    public static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>A 1.x archive: <c>Profile.xml</c> and <c>DisplayItems.xml</c>.</summary>
    public static byte[] V1Archive(string itemsXml) =>
        Zip(("Profile.xml", Encoding.UTF8.GetBytes(ProfileXml(new Profile("Imported", 640, 480)))), ("DisplayItems.xml", Encoding.UTF8.GetBytes(itemsXml)));

    public static ByteArrayContent Bytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }
}

/// <summary>An engine whose 1.x folder is a copy of the Core fixtures' 1.x folder.</summary>
public class V1FolderEngineFactory : EngineFactory
{
    public V1FolderEngineFactory() => V1Fixture.CopyTo(V1Dir);
}

/// <summary>An engine whose archive bodies are held to 1 KiB, so a body over <c>archiveBytes</c> stays small.</summary>
public sealed class SmallArchiveEngineFactory : EngineFactory
{
    public const int ArchiveBytes = 1024;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton(new ImportLimits { ArchiveBytes = ArchiveBytes }));
    }
}

/// <summary>The 1.x import's requests (spec/docs/import.md), over HTTP.</summary>
public class ImportEndpointTests(V1FolderEngineFactory factory) : IClassFixture<V1FolderEngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
    }

    private static void AssertError(JsonElement problem, string code, string path)
    {
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("code").GetString(), e.GetProperty("path").GetString())).ToList();
        Assert.Contains((code, path), errors);
    }

    private static string Request(string settings = "imported", string outcomes = "") =>
        $$"""{"profiles":[{{outcomes}}],"settings":"{{settings}}"}""";

    private const string DeskOutcome = $$"""{"v1Id":"{{V1Fixture.Desk}}","id":"{{V1Fixture.Desk}}","outcome":"imported"}""";

    [Fact]
    public async Task ATestEngine_ReadsItsOwn1xFolder_NeverTheUsersOne()
    {
        using var engine = new EngineFactory();
        using var client = engine.CreateApiClient();

        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);

        Assert.Equal("none", state.GetProperty("state").GetString());
        Assert.Equal(Path.GetFullPath(engine.V1Dir), state.GetProperty("folder").GetString());
        Assert.NotEqual(V1Folder.DefaultRoot(), state.GetProperty("folder").GetString());
        Assert.False(Directory.Exists(engine.V1Dir));
    }

    [Fact]
    public async Task TheState_IsPendingWith1xData_ThenDoneOnceARecordIsStored()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();

        var pending = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal(Path.GetFullPath(engine.V1Dir), pending.GetProperty("folder").GetString());
        Assert.Equal(V1Fixture.Desk, pending.GetProperty("profiles")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, pending.GetProperty("settings").ValueKind);
        Assert.False(pending.TryGetProperty("record", out _));

        var done = await Expect(await client.PutAsync("/api/import/v1", Json(Request(outcomes: DeskOutcome)), Ct), HttpStatusCode.OK);
        Assert.Equal("done", done.GetProperty("state").GetString());
        var record = done.GetProperty("record");
        Assert.Equal(Path.GetFullPath(engine.V1Dir), record.GetProperty("folder").GetString());
        Assert.Equal("imported", record.GetProperty("settings").GetString());
        Assert.Equal(V1Fixture.Desk, record.GetProperty("profiles")[0].GetProperty("id").GetString());
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", record.GetProperty("completed").GetString());

        var again = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal("done", again.GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(engine.DataDir, "import-v1.json")));
    }

    [Fact]
    public async Task ARecord_IsCheckedAsEveryBody_AndARefusedOneStoresNothing()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();

        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json("""{"profiles":[],"settings":"imported","completed":"2026-10-29T09:30:00Z"}"""), Ct),
            HttpStatusCode.UnprocessableEntity), "schema", "/completed");
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json(Request(settings: "applied")), Ct), HttpStatusCode.UnprocessableEntity), "schema", "/settings");
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json("{ not json"), Ct), HttpStatusCode.UnprocessableEntity), "json", "");

        // The outcomes are counted before the schema, and the body's size as received.
        var many = string.Join(",", Enumerable.Range(0, 1001).Select(_ => DeskOutcome));
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json(Request(outcomes: many)), Ct), HttpStatusCode.UnprocessableEntity), "limit.recordOutcomes", "/profiles");
        var big = Request() + new string(' ', 1024 * 1024);
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json(big), Ct), HttpStatusCode.UnprocessableEntity), "limit.recordBytes", "");

        Assert.Equal("pending", (await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK)).GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(engine.DataDir, "import-v1.json")));
    }

    [Fact]
    public async Task AReason_IsCountedInCharacters_AsTheSchemaCountsThem()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        string WithReason(string reason) => Request(outcomes: $$"""{"v1Id":"{{V1Fixture.Desk}}","id":null,"outcome":"failed","reason":"{{reason}}"}""");
        var emoji = char.ConvertFromUtf32(0x1F600); // one character, two UTF-16 units

        // 256 characters fit however many UTF-16 units they take; 257 are the schema's refusal, at the reason.
        await Expect(await client.PutAsync("/api/import/v1", Json(WithReason(string.Concat(Enumerable.Repeat(emoji, 256)))), Ct), HttpStatusCode.OK);
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json(WithReason(string.Concat(Enumerable.Repeat(emoji, 257)))), Ct),
            HttpStatusCode.UnprocessableEntity), "schema", "/profiles/0/reason");
        // A lone surrogate isn't Unicode text: json, at the string.
        AssertError(await Expect(await client.PutAsync("/api/import/v1", Json(WithReason("\\ud800")), Ct), HttpStatusCode.UnprocessableEntity), "json", "/profiles/0/reason");
    }

    [Fact]
    public async Task ARecordWithMembersALaterEngineAdded_ReadsAsDone_AndKeepsThemWhenReplaced()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        var file = Path.Combine(engine.DataDir, "import-v1.json");
        await Expect(await client.GetAsync("/api/session", Ct), HttpStatusCode.OK);
        File.WriteAllText(file, $$$"""{"completed":"2026-10-01T08:00:00Z","folder":{{{JsonSerializer.Serialize(engine.V1Dir)}}},"profiles":[],"settings":"skipped","laterEngine":{"kept":[1,2]}}""");

        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal("done", state.GetProperty("state").GetString());
        Assert.Equal("skipped", state.GetProperty("record").GetProperty("settings").GetString());

        await Expect(await client.PutAsync("/api/import/v1", Json(Request(outcomes: DeskOutcome)), Ct), HttpStatusCode.OK);
        var stored = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal("""{"kept":[1,2]}""", stored["laterEngine"]!.ToJsonString());
        Assert.Equal("imported", (string)stored["settings"]!);
    }

    [Fact]
    public async Task ARecordThatCantBeRead_CountsAsDone_WithADiagnostic()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        await Expect(await client.GetAsync("/api/session", Ct), HttpStatusCode.OK);
        File.WriteAllText(Path.Combine(engine.DataDir, "import-v1.json"), "{ not json");

        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);

        Assert.Equal("done", state.GetProperty("state").GetString());
        Assert.False(state.TryGetProperty("record", out _));
        Assert.Contains(state.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "import.record");
    }

    [Fact]
    public async Task AFolderProfile_IsReadIntoItsEnvelope_WithItsImagesStored()
    {
        using var client = factory.CreateApiClient();

        var envelope = await Expect(await client.PostAsync($"/api/import/v1/profiles/{V1Fixture.Desk}", null, Ct), HttpStatusCode.OK);

        Assert.Equal("folder", envelope.GetProperty("source").GetString());
        Assert.Equal(V1Fixture.Desk, envelope.GetProperty("id").GetString());
        Assert.Equal("Desk Overlay", envelope.GetProperty("profile").GetProperty("name").GetString());
        Assert.NotEmpty(envelope.GetProperty("items").EnumerateArray());
        var stored = envelope.GetProperty("assets").EnumerateArray().Where(a => a.TryGetProperty("blob", out _)).ToList();
        Assert.NotEmpty(stored);
        foreach (var asset in stored)
        {
            using var blob = await client.GetAsync($"/api/blobs/{asset.GetProperty("blob").GetProperty("id").GetString()}", Ct);
            Assert.Equal(HttpStatusCode.OK, blob.StatusCode);
        }
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-0000000000aa")]
    [InlineData("not-a-guid")]
    public async Task AProfileTheFolderDoesntHave_Is404(string v1Id)
    {
        using var client = factory.CreateApiClient();

        await Expect(await client.PostAsync($"/api/import/v1/profiles/{v1Id}", null, Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A11xFolder_RefusesItsProfiles_WithImportVersion()
    {
        using var engine = new V1FolderEngineFactory();
        File.WriteAllText(Path.Combine(engine.V1Dir, "settings.xml"), """<?xml version="1.0" encoding="utf-8"?><Settings><Version>114</Version></Settings>""");
        using var client = engine.CreateApiClient();

        var problem = await Expect(await client.PostAsync($"/api/import/v1/profiles/{V1Fixture.Desk}", null, Ct), HttpStatusCode.UnprocessableEntity);

        AssertError(problem, "import.version", "");
    }

    [Fact]
    public async Task A1xArchive_IsReadIntoItsEnvelope()
    {
        using var client = factory.CreateApiClient();

        var envelope = await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(File.ReadAllBytes(V1Fixture.AllKindsArchive)), Ct), HttpStatusCode.OK);

        Assert.Equal("archive", envelope.GetProperty("source").GetString());
        var kinds = new HashSet<string>();
        void Walk(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                kinds.Add(item.GetProperty("kind").GetString()!);
                if (item.TryGetProperty("children", out var children)) Walk(children);
                if (item.TryGetProperty("images", out var images)) Walk(images);
            }
        }
        Walk(envelope.GetProperty("items"));
        Assert.Equal(14, kinds.Count);
    }

    [Fact]
    public async Task AnItemOfATypeOutsideThe14Kinds_IsLeftOut_WithADiagnostic_NotRefused()
    {
        using var client = factory.CreateApiClient();
        var archive = V1Fixture.V1Archive(V1Fixture.Items(
            V1Fixture.Text("Kept"),
            """<DisplayItem xsi:type="FutureDisplayItem"><Name>From a newer 1.x</Name></DisplayItem>"""));

        var envelope = await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(archive), Ct), HttpStatusCode.OK);

        var item = Assert.Single(envelope.GetProperty("items").EnumerateArray());
        Assert.Equal("text", item.GetProperty("kind").GetString());
        var diagnostic = Assert.Single(envelope.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "import.itemType");
        Assert.Equal("/items", diagnostic.GetProperty("path").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AnArchiveThatIsntA1xExport_IsRefused()
    {
        using var client = factory.CreateApiClient();

        AssertError(await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes("not a zip"u8.ToArray()), Ct), HttpStatusCode.UnprocessableEntity), "archive.zip", "");
        AssertError(await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(V1Fixture.Zip(("readme.txt", "hi"u8.ToArray()))), Ct),
            HttpStatusCode.UnprocessableEntity), "import.archive", "");
        AssertError(await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(V1Fixture.Zip(("../x", "hi"u8.ToArray()))), Ct),
            HttpStatusCode.UnprocessableEntity), "archive.name", "/entries/..~1x");
    }

    [Fact]
    public async Task AnArchiveBodyOverArchiveBytes_Is413_AndNothingIsRead()
    {
        using var engine = new SmallArchiveEngineFactory();
        using var client = engine.CreateApiClient();

        await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(new byte[SmallArchiveEngineFactory.ArchiveBytes + 1]), Ct), HttpStatusCode.RequestEntityTooLarge);
        // Within the limit it is read (and refused for what it is).
        AssertError(await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(new byte[SmallArchiveEngineFactory.ArchiveBytes]), Ct),
            HttpStatusCode.UnprocessableEntity), "archive.zip", "");
    }

    [Fact]
    public async Task The1xFolder_IsNeverWritten_ByAnyRequest()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        var before = V1Fixture.TreeHash(engine.V1Dir);

        await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        foreach (var profile in state.GetProperty("profiles").EnumerateArray())
            await client.PostAsync($"/api/import/v1/profiles/{profile.GetProperty("id").GetString()}", null, Ct);
        await Expect(await client.PutAsync("/api/import/v1", Json(Request(outcomes: DeskOutcome)), Ct), HttpStatusCode.OK);
        await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);

        Assert.Equal(before, V1Fixture.TreeHash(engine.V1Dir));
    }

    [Fact]
    public async Task AFirstRun_With1xDataWaiting_StoresNoExample_AndOneWithout_Does()
    {
        using var waiting = new V1FolderEngineFactory();
        using var none = new EngineFactory();
        using var waitingClient = waiting.CreateApiClient();
        using var noneClient = none.CreateApiClient();

        Assert.Empty((await Expect(await waitingClient.GetAsync("/api/profiles", Ct), HttpStatusCode.OK)).EnumerateArray());
        Assert.Single((await Expect(await noneClient.GetAsync("/api/profiles", Ct), HttpStatusCode.OK)).EnumerateArray());
    }
}
