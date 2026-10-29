using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Import;
using InfoPanel.Spec;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Import;

/// <summary>An engine whose import refuses an archive of more than one entry, so every profile's archive is over it.</summary>
public sealed class OneEntryEngineFactory : EngineFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton(new ImportLimits { Entries = 1 }));
    }
}

/// <summary>The profile archive's requests (spec/docs/archive.md): a profile's export and the strict import, over HTTP.</summary>
public class ArchiveEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
    }

    private static void AssertError(JsonElement problem, string code, string path) =>
        Assert.Contains((code, path), problem.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("code").GetString(), e.GetProperty("path").GetString())));

    private static async Task<string> CreateProfile(HttpClient client, string name)
    {
        var id = NewId();
        var document = Doc(name, 200, 100, Scene("a", 10, 10));
        document["id"] = id;
        await Expect(await client.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = document }), Ct), HttpStatusCode.Created);
        return id;
    }

    private static List<string> Names(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public async Task AProfile_ExportsAsAnArchive_ThatAnotherEngineOpens_AndCreatesFrom()
    {
        using var client = factory.CreateApiClient();
        var id = await CreateProfile(client, "Desk: Main/1");

        using var response = await client.GetAsync($"/api/profiles/{id}/archive", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^Desk__Main_1-[0-9]+\.infopanel2$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var zip = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(["manifest.json", "document.json"], Names(zip));
        var exported = await (await client.GetAsync($"/api/profiles/{id}/document", Ct)).Content.ReadAsStringAsync(Ct);

        using var other = new EngineFactory();
        using var otherClient = other.CreateApiClient();
        var contents = await Expect(await otherClient.PostAsync("/api/import/archive", V1Fixture.Bytes(zip), Ct), HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(exported), JsonNode.Parse(contents.GetProperty("document").GetRawText())));
        Assert.Empty(contents.GetProperty("blobs").EnumerateArray());
        Assert.Empty(contents.GetProperty("missing").EnumerateArray());

        var created = new JsonObject { ["id"] = NewId(), ["document"] = JsonNode.Parse(contents.GetProperty("document").GetRawText()) };
        await Expect(await otherClient.PostAsync("/api/profiles", Json(created), Ct), HttpStatusCode.Created);
    }

    [Fact]
    public async Task AProfileThisEngineCantOpen_IsNotExported()
    {
        using var seeded = new SeededEngineFactory();
        using var client = seeded.CreateApiClient();

        var problem = await Expect(await client.GetAsync($"/api/profiles/{SeededEngineFactory.Unsupported}/archive", Ct), HttpStatusCode.Conflict);

        Assert.Equal("unsupported", problem.GetProperty("code").GetString());
        Assert.Equal("unsupported.format", problem.GetProperty("problem").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnArchiveItsImportWouldRefuse_IsNotExported_AndSaysWithTheImportsCode()
    {
        using var engine = new OneEntryEngineFactory();
        using var client = engine.CreateApiClient();
        var id = await CreateProfile(client, "Two entries");

        var problem = await Expect(await client.GetAsync($"/api/profiles/{id}/archive", Ct), HttpStatusCode.Conflict);

        Assert.Equal("export", problem.GetProperty("code").GetString());
        AssertError(problem, "archive.entries", "");
    }

    [Fact]
    public async Task NoSuchProfile_Is404()
    {
        using var client = factory.CreateApiClient();

        await Expect(await client.GetAsync("/api/profiles/00000000-0000-4000-8000-0000000000aa/archive", Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnArchiveBodyOverArchiveBytes_Is413()
    {
        using var engine = new SmallArchiveEngineFactory();
        using var client = engine.CreateApiClient();

        await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes(new byte[SmallArchiveEngineFactory.ArchiveBytes + 1]), Ct), HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task AnArchiveOfTheWrongKind_OrThatIsntOne_IsRefused()
    {
        using var client = factory.CreateApiClient();

        AssertError(await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes(File.ReadAllBytes(V1Fixture.AllKindsArchive)), Ct),
            HttpStatusCode.UnprocessableEntity), "archive.v1", "");
        AssertError(await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes("not a zip"u8.ToArray()), Ct), HttpStatusCode.UnprocessableEntity), "archive.zip", "");
        AssertError(await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes(V1Fixture.Zip(("document.json", "{}"u8.ToArray()))), Ct),
            HttpStatusCode.UnprocessableEntity), "archive.manifest", "");
    }

    [Fact]
    public async Task AManifestThatRequiresACapabilityThisEngineLacks_IsRefusedAsUnsupported()
    {
        using var client = factory.CreateApiClient();
        var manifest = """{"format":"infopanel.archive/1","requires":["profiles.many"],"profiles":["document.json"]}"""u8.ToArray();
        var document = Encoding.UTF8.GetBytes(Doc("Plain", 100, 50).ToJsonString());

        var problem = await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes(V1Fixture.Zip(("manifest.json", manifest), ("document.json", document))), Ct),
            HttpStatusCode.UnprocessableEntity);

        AssertError(problem, "unsupported.capability", "/manifest/requires/0");
    }

    [Fact]
    public async Task TheSession_ListsTheArchiveFormat_WithTheArchiveLimits()
    {
        using var client = factory.CreateApiClient();

        var support = (await Expect(await client.GetAsync("/api/session", Ct), HttpStatusCode.OK)).GetProperty("formatSupport");

        var archive = support.GetProperty(SpecSchemas.ArchiveFormat);
        Assert.Equal("1.0", archive.GetProperty("version").GetString());
        Assert.Empty(archive.GetProperty("capabilities").EnumerateArray());
        foreach (var name in new[] { "archiveBytes", "archiveEntries", "expandedBytes", "entryNameBytes", "entryRecordBytes", "manifestBytes" })
        {
            Assert.True(archive.GetProperty("limits").TryGetProperty(name, out _), name);
            Assert.False(support.GetProperty(SpecSchemas.DocumentFormat).GetProperty("limits").TryGetProperty(name, out _), name);
        }
    }
}
