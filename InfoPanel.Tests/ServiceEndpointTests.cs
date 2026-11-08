using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The configured service over the API (spec/docs/api.md, "The configured service"): <c>GET /api/service</c>, <c>PUT</c>
/// and <c>DELETE /api/service/credential</c>, the session token's only; the address as the <c>service.address</c>
/// setting, else the build's web client origin (none in a build from source); the credential kept in the store, never in
/// <c>settings.json</c>, the log file, the recent log entries or any answer; removed by a change of address and by
/// <c>--fresh</c>. Each test has an engine of its own, since what it keeps is kept in its data folder.
/// </summary>
public sealed class ServiceEndpointTests
{
    private const string Address = "https://service.example";
    private const string Credential = "test-credential.Zq8~+/Xy==";

    /// <summary>
    /// A part of <see cref="Credential"/> no JSON writer or log escapes (its <c>+</c> becomes <c>\u002B</c> in the
    /// settings' and the log's JSON), so a copy is found however it was written.
    /// </summary>
    private const string Marker = "test-credential.Zq8";

    /// <summary>Another credential, and the part of it the scans look for, as <see cref="Marker"/> is for <see cref="Credential"/>.</summary>
    private const string Replacement = "replacement-credential.Rp4~+/==";

    private const string ReplacementMarker = "replacement-credential.Rp4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Sha16(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];

    private static async Task<(HttpStatusCode Status, string Text)> Send(HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = json == null ? null : Body(json) };
        using var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> State(HttpClient api)
    {
        var (status, text) = await Send(api, HttpMethod.Get, "/api/service");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Marker, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<(HttpStatusCode, string)> Put(HttpClient api, string credential) =>
        Send(api, HttpMethod.Put, "/api/service/credential", JsonSerializer.Serialize(new { credential }));

    private static async Task SetAddress(HttpClient api, string? address)
    {
        var (status, text) = await Send(api, HttpMethod.Patch, "/api/settings", JsonSerializer.Serialize(new { service = new { address } }));
        Assert.True(status == HttpStatusCode.OK, text);
    }

    private static string StoreFile(EngineFactory factory) => Path.Combine(factory.DataDir,
        OperatingSystem.IsWindows() ? FileServiceCredentialStore.ProtectedFileName : FileServiceCredentialStore.PrivateFileName);

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task WithoutAnAddress_ThereIsNoService_AndACredentialIsRefused409()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var state = await State(api);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("address").ValueKind);
        Assert.Equal("absent", state.GetProperty("credential").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("store").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("fingerprint").ValueKind);

        var (status, text) = await Put(api, Credential);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("noService", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(Marker, text);
        Assert.False(File.Exists(StoreFile(factory)));
    }

    [Fact]
    public async Task TheDefaultAddress_IsTheBuildsWebClientOrigin_NoneInABuildFromSource()
    {
        // The test engine is a build from source: no origin built in, so no default, whatever --allow-origin says.
        Assert.Null(EngineOptions.BuiltInAllowOrigin);
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        Assert.Equal(JsonValueKind.Null, (await State(api)).GetProperty("address").ValueKind);
        var settings = JsonDocument.Parse((await Send(api, HttpMethod.Get, "/api/settings")).Text).RootElement;
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("service").GetProperty("address").ValueKind);
    }

    [Theory]
    [InlineData(null, null, "https://client.example")]
    [InlineData("--allow-origin", "https://other.example", "https://client.example")]
    public void TheDefaultAddress_IsTheOriginTheBuildCarries_WhateverTheFlags(string? flag, string? value, string expected)
    {
        var parsed = EngineCommandLine.Parse(flag is null ? [] : [flag, value!]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();

        var options = EngineOptions.Load(configuration, out var errors, "https://Client.Example/");

        Assert.Empty(errors);
        Assert.Equal(expected, options!.DefaultServiceAddress);
        Assert.Null(EngineOptions.Load(new ConfigurationBuilder().Build(), out _, builtInOrigin: null)!.DefaultServiceAddress);
    }

    [Fact]
    public void ABuiltInOriginThatIsntAServiceAddress_StopsStartUp_EvenWithFlags()
    {
        var parsed = EngineCommandLine.Parse(["--allow-origin", "https://other.example"]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();

        Assert.Null(EngineOptions.Load(configuration, out var errors, "http://client.example"));
        Assert.Contains(errors, e => e.Contains("InfoPanelAllowOrigin"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABuiltInOriginThatPassesAsAnOrigin_ButIsntAServiceAddress_StopsStartUp_WithOrWithoutFlags(bool flags)
    {
        // An empty user part: an exact origin to the allow-origin check, not a service address.
        var parsed = EngineCommandLine.Parse(flags ? ["--allow-origin", "https://other.example"] : []);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();

        Assert.Null(EngineOptions.Load(configuration, out var errors, "https://@client.example"));
        Assert.Contains(errors, e => e.Contains("InfoPanelAllowOrigin") && e.Contains("service"));
    }

    [Fact]
    public async Task AnAddressWithItsSchemeInUpperCase_IsTaken_InItsNormalForm()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        await SetAddress(api, "HTTPS://Service.Example/");

        Assert.Equal(Address, (await State(api)).GetProperty("address").GetString());
        var settings = JsonDocument.Parse((await Send(api, HttpMethod.Get, "/api/settings")).Text).RootElement;
        Assert.Equal(Address, settings.GetProperty("service").GetProperty("address").GetString());
    }

    [Fact]
    public async Task AChangeOfAddress_ThatCantRemoveTheCredential_Is503_AndChangesNothing_SoNoRecordOutlivesItsAddress()
    {
        using var factory = new EngineFactory();
        var store = new FailingDeleteStore(FileServiceCredentialStore.ForPrivateFile(factory.DataDir));
        using var engine = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IServiceCredentialStore>(store)));
        using var api = engine.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        await SetAddress(api, Address);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, Credential)).Item1);

        store.FailDelete = true;
        var (status, text) = await Send(api, HttpMethod.Patch, "/api/settings", """{"service":{"address":"http://127.0.0.1:8787"}}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("storeUnavailable", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(Marker, text);
        // Nothing changed: the address, and the credential provided for it.
        var settings = JsonDocument.Parse((await Send(api, HttpMethod.Get, "/api/settings")).Text).RootElement;
        Assert.Equal(Address, settings.GetProperty("service").GetProperty("address").GetString());
        var state = await State(api);
        Assert.Equal((Address, "present"), (state.GetProperty("address").GetString(), state.GetProperty("credential").GetString()));

        // Once the store can remove it, the change removes it; changed back, nothing comes back, nor at the next start.
        store.FailDelete = false;
        await SetAddress(api, "http://127.0.0.1:8787");
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
        await SetAddress(api, Address);
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
        Assert.Null(FileServiceCredentialStore.ForPrivateFile(factory.DataDir).Read());
    }

    [Fact]
    public async Task ASettingsChangeThatKeepsTheAddress_DoesntWaitForTheCredentialStore()
    {
        using var factory = new EngineFactory();
        var store = new BlockingWriteStore(FileServiceCredentialStore.ForPrivateFile(factory.DataDir));
        using var engine = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IServiceCredentialStore>(store)));
        using var api = engine.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        await SetAddress(api, Address);

        // A credential being stored: the store's write is under way, and stays so until released.
        store.Block = true;
        var put = Put(api, Credential);
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // A change of another setting meanwhile is in effect at once: it doesn't wait for the credential.
            var (status, text) = await Send(api, HttpMethod.Patch, "/api/settings", """{"logLevel":"debug"}""")
                .WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(status == HttpStatusCode.OK, text);
            var settings = JsonDocument.Parse((await Send(api, HttpMethod.Get, "/api/settings")).Text).RootElement;
            Assert.Equal("debug", settings.GetProperty("logLevel").GetString());
            Assert.False(put.IsCompleted);
        }
        finally
        {
            store.Release.TrySetResult();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await put).Item1);
        Assert.Equal("present", (await State(api)).GetProperty("credential").GetString());
    }

    [Fact]
    public async Task ACredential_IsKept_ItsStateAndFingerprintAnswered_NeverItself_AndRemoved()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);

        var (status, text) = await Put(api, Credential);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Empty(text);

        var state = await State(api);
        Assert.Equal(Address, state.GetProperty("address").GetString());
        Assert.Equal("present", state.GetProperty("credential").GetString());
        Assert.Equal(OperatingSystem.IsWindows() ? "protected" : "private", state.GetProperty("store").GetString());
        Assert.Equal(Sha16(Credential), state.GetProperty("fingerprint").GetString());
        Assert.True(File.Exists(StoreFile(factory)));

        // Replaced: another fingerprint.
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, "another-credential")).Item1);
        Assert.Equal(Sha16("another-credential"), (await State(api)).GetProperty("fingerprint").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, "/api/service/credential")).Status);
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
        Assert.False(File.Exists(StoreFile(factory)));
        // Nothing to remove: 204 all the same.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, "/api/service/credential")).Status);
    }

    [Fact]
    public async Task TheCredential_IsNeverInTheSettings_TheLogFile_TheRecentLog_OrAnyAnswer()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, Credential)).Item1);
        // A refused one too: its 422 doesn't echo it.
        var (refused, refusedText) = await Put(api, Credential + " x");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.DoesNotContain(Marker, refusedText);
        // Replaced by another: the engine asks the service to forget the first (offline here, so it is refused, and
        // logged); neither credential is anywhere but the store, which holds the second.
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, Replacement)).Item1);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!factory.Services.GetRequiredService<RecentLog>().After(0).Any(e => e.Message.Contains("credential this engine replaced")))
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the line about the replaced credential");
            await Task.Delay(10, Ct);
        }

        string[] markers = [Marker, ReplacementMarker];
        var answers = new List<string>
        {
            (await Send(api, HttpMethod.Get, "/api/service")).Text,
            (await Send(api, HttpMethod.Get, "/api/settings")).Text,
            (await Send(api, HttpMethod.Get, "/api/session")).Text,
            (await Send(api, HttpMethod.Get, "/api/logs")).Text,
        };
        foreach (var answer in answers)
            foreach (var marker in markers)
                Assert.DoesNotContain(marker, answer);

        var settingsFile = Path.Combine(factory.DataDir, DataPaths.SettingsFileName);
        Assert.True(File.Exists(settingsFile));
        foreach (var marker in markers)
            Assert.DoesNotContain(marker, File.ReadAllText(settingsFile));

        var recent = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(recent, e => e.Message.Contains("A client provided a credential"));
        foreach (var marker in markers)
            Assert.DoesNotContain(recent, e => (e.Message + e.Exception).Contains(marker));

        var logs = Directory.GetFiles(Path.Combine(factory.DataDir, "logs"), "*.log");
        Assert.NotEmpty(logs);
        foreach (var log in logs)
        {
            foreach (var marker in markers)
                Assert.DoesNotContain(marker, ReadShared(log));
        }

        // Only the store's own file holds it, among every file in the data folder.
        foreach (var file in Directory.GetFiles(factory.DataDir, "*", SearchOption.AllDirectories))
        {
            if (file == StoreFile(factory) || file.EndsWith("engine.lock", StringComparison.Ordinal))
                continue;
            foreach (var marker in markers)
                Assert.DoesNotContain(marker, ReadShared(file));
        }
    }

    [Theory]
    [InlineData("""{"credential":"has space"}""", "/credential")]
    [InlineData("""{"credential":"ab=c"}""", "/credential")]
    [InlineData("""{"credential":"abc\n"}""", "/credential")]
    [InlineData("""{"credential":""}""", "/credential")]
    [InlineData("""{"credential":"abc","label":"x"}""", "/label")]
    [InlineData("""{}""", "")]
    [InlineData("""not json""", "")]
    public async Task AMalformedCredential_Is422_NothingKept(string body, string path)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);

        var (status, text) = await Send(api, HttpMethod.Put, "/api/service/credential", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var error = JsonDocument.Parse(text).RootElement.GetProperty("errors")[0];
        Assert.Equal(path, error.GetProperty("path").GetString());
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
        Assert.False(File.Exists(StoreFile(factory)));
    }

    [Fact]
    public async Task ACredentialOver4096Characters_Is422()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Put(api, new string('a', 4097))).Item1);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, new string('a', 4096))).Item1);
    }

    [Fact]
    public async Task OnlyTheSessionToken_ReadsTheServiceOrGivesItsCredential()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        var created = await api.PostAsJsonAsync("/api/tokens", new { name = "service-test", scopes = new[] { "edit" } }, Ct);
        var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("token").GetString();
        using var integration = factory.CreateRawClient();
        integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(integration, HttpMethod.Get, "/api/service")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(integration, Credential)).Item1);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(integration, HttpMethod.Delete, "/api/service/credential")).Status);
        Assert.False(File.Exists(StoreFile(factory)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8787")]
    [InlineData(null)]
    public async Task AChangeOfAddress_RemovesTheCredential(string? next)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, Credential)).Item1);

        await SetAddress(api, next);

        var state = await State(api);
        Assert.Equal("absent", state.GetProperty("credential").GetString());
        Assert.False(File.Exists(StoreFile(factory)));
        // The same address set again doesn't bring it back.
        await SetAddress(api, Address);
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
    }

    [Fact]
    public async Task AStoreThatRefuses_Is503_AndKeepsWhatItHad()
    {
        using var factory = new EngineFactory();
        using var refusing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IServiceCredentialStore>(new RefusingServiceStore())));
        using var api = refusing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        await SetAddress(api, Address);

        var (put, putText) = await Put(api, Credential);
        var (delete, deleteText) = await Send(api, HttpMethod.Delete, "/api/service/credential");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable), (put, delete));
        Assert.Equal("storeUnavailable", JsonDocument.Parse(putText).RootElement.GetProperty("code").GetString());
        Assert.Equal("storeUnavailable", JsonDocument.Parse(deleteText).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(Marker, putText);
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
    }

    [Fact]
    public void Fresh_RemovesTheCredentialsFiles_AndTheirTempFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "infopanel-service-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var lockPath = Path.Combine(dir, "engine.lock");
            using (InstanceLock.TryAcquire(lockPath, out _))
            {
                foreach (var name in new[] { FileServiceCredentialStore.PrivateFileName, FileServiceCredentialStore.ProtectedFileName })
                {
                    File.WriteAllText(Path.Combine(dir, name), "x");
                    File.WriteAllText(Path.Combine(dir, $"{name}.{Guid.NewGuid():N}.tmp"), "x");
                    Assert.True(DataPaths.IsEngineEntry(name));
                }

                Assert.Null(DataFolder.CheckWipeable(dir));
                DataFolder.Wipe(dir, lockPath);

                Assert.Equal([lockPath], Directory.GetFileSystemEntries(dir));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OnWindows_TheCredentialIsEncryptedForThisUser_InAFileOnlyThisUserMayRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: DPAPI and the file's access list.");
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(api, Credential)).Item1);

        var file = Path.Combine(factory.DataDir, FileServiceCredentialStore.ProtectedFileName);
        Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker)));
        Assert.False(File.Exists(Path.Combine(factory.DataDir, FileServiceCredentialStore.PrivateFileName)));
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(file).GetAccessControl();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            Assert.All(rules, r => Assert.Equal(user, r.IdentityReference));
        }
    }

    /// <summary>A store whose removals fail while <see cref="FailDelete"/> is set.</summary>
    private sealed class FailingDeleteStore(IServiceCredentialStore inner) : IServiceCredentialStore
    {
        public volatile bool FailDelete;

        public string Name => inner.Name;

        public StoredServiceCredential? Read() => inner.Read();

        public void Write(StoredServiceCredential record) => inner.Write(record);

        public void Delete()
        {
            if (FailDelete)
                throw new ServiceStoreException("The store couldn't remove it (a test).");
            inner.Delete();
        }
    }

    /// <summary>
    /// A store whose writes, while <see cref="Block"/> is set, signal <see cref="Entered"/> and wait for
    /// <see cref="Release"/> before writing.
    /// </summary>
    private sealed class BlockingWriteStore(IServiceCredentialStore inner) : IServiceCredentialStore
    {
        public volatile bool Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => inner.Name;

        public StoredServiceCredential? Read() => inner.Read();

        public void Write(StoredServiceCredential record)
        {
            if (Block)
            {
                Entered.TrySetResult();
                Release.Task.Wait();
            }

            inner.Write(record);
        }

        public void Delete() => inner.Delete();
    }

    /// <summary>A store that holds nothing, or <paramref name="held"/>, and refuses every write and delete.</summary>
    internal sealed class RefusingServiceStore(StoredServiceCredential? held = null) : IServiceCredentialStore
    {
        public string Name => "private";

        public StoredServiceCredential? Read() => held;

        public void Write(StoredServiceCredential record) => throw new ServiceStoreException("The store refused (a test).");

        public void Delete() => throw new ServiceStoreException("The store refused (a test).");
    }
}
