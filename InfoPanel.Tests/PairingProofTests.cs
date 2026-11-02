using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Tests.Desktop;
using InfoPanel.Tests.Editing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>spec/docs/api.md's pairing test vector: K, Np and Ne are the bytes 00…1f, 20…3f and 40…5f; P is 51234.</summary>
internal static class PairingVector
{
    public static readonly byte[] Key = [.. Enumerable.Range(0x00, 32).Select(b => (byte)b)];
    public static readonly byte[] ClientNonce = [.. Enumerable.Range(0x20, 32).Select(b => (byte)b)];
    public static readonly byte[] EngineNonce = [.. Enumerable.Range(0x40, 32).Select(b => (byte)b)];
    public const int Port = 51234;
    public const string Token = "Q7u4i3XzE2pW9vLkR8tY1aBcDeFgHiJkLmNoPqRsTuV";
    public const string Instance = "0123456789abcdef0123456789abcdef";

    /// <summary><c>Ünïcode-実行</c>, in NFC (its UTF-8 bytes <c>c39c6ec3af636f64652de5ae9fe8a18c</c>).</summary>
    public const string UnicodeInstance = "Ünïcode-実行";

    public const string E = "47dc18c9d81c68892f6f4c48cbcad6d0a5f31657c7ff53b13dd40c84c2fedcc8";
    public const string EBase64Url = "R9wYydgcaIkvb0xIy8rW0KXzFlfH_1OxPdQMhML-3Mg";
    public const string C = "19b0468fbbfd9086fd45ca4a89dd0400ecfdbcb2e3c4baaf7e6bc383bf521274";
    public const string CBase64Url = "GbBGj7v9kIb9RcpKid0EAOz9vLLjxLqvfmvDg79SEnQ";
    public const string A = "0068bfa88ff3dbd916a8b8ff67ac57428504bfe2dd4dabad52e178116b7f677f";
    public const string ABase64Url = "AGi_qI_z29kWqLj_Z6xXQoUEv-LdTautUuF4EWt_Z38";
    public const string ERelay = "161eddb30012e486e6aab7de036287e6c0660eae54fdccc22e7bfe59c886e1de";
    public const string AUnicode = "086d164f991d4d44247a063e70eee574264ef260772e0feee927adf1ef2a0258";
    public const string AUnicodeBase64Url = "CG0WT5kdTUQkegY-cO7ldCZO8mB3Lg_u6Set8e8qAlg";
    public const string AUnicodeAsUtf16 = "893fab19a5d7a7da510965a99a8159e0531db0ec64ae23986313b508fddb33d5";

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static string B64(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static byte[] Bytes(string base64Url) => System.Buffers.Text.Base64Url.DecodeFromChars(base64Url);
}

/// <summary><see cref="PairingProofs"/>'s keyed hashes against the test vector, byte for byte.</summary>
public class PairingVectorTests
{
    [Fact]
    public void TheProofs_AreTheVectors()
    {
        var (k, np, ne) = (PairingVector.Key, PairingVector.ClientNonce, PairingVector.EngineNonce);
        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", PairingVector.B64(k));
        Assert.Equal(PairingVector.E, PairingVector.Hex(PairingProofs.EngineProof(k, np, ne, PairingVector.Port)));
        Assert.Equal(PairingVector.EBase64Url, PairingVector.B64(PairingProofs.EngineProof(k, np, ne, PairingVector.Port)));
        Assert.Equal(PairingVector.C, PairingVector.Hex(PairingProofs.ClientProof(k, np, ne, PairingVector.Port)));
        Assert.Equal(PairingVector.CBase64Url, PairingVector.B64(PairingProofs.ClientProof(k, np, ne, PairingVector.Port)));
        Assert.Equal(PairingVector.A, PairingVector.Hex(PairingProofs.AnswerProof(k, np, ne, PairingVector.Port, PairingVector.Token, PairingVector.Instance)));
        Assert.Equal(PairingVector.ABase64Url, PairingVector.B64(PairingProofs.AnswerProof(k, np, ne, PairingVector.Port, PairingVector.Token, PairingVector.Instance)));
        // The relay's case: the same, over the port next to it.
        Assert.Equal(PairingVector.ERelay, PairingVector.Hex(PairingProofs.EngineProof(k, np, ne, PairingVector.Port + 1)));
    }

    [Fact]
    public void TheAnswer_HashesTheInstanceAsUtf8()
    {
        var a = PairingProofs.AnswerProof(PairingVector.Key, PairingVector.ClientNonce, PairingVector.EngineNonce, PairingVector.Port,
            PairingVector.Token, PairingVector.UnicodeInstance);
        Assert.Equal("c39c6ec3af636f64652de5ae9fe8a18c", Convert.ToHexStringLower(Encoding.UTF8.GetBytes(PairingVector.UnicodeInstance)));
        Assert.Equal(PairingVector.AUnicode, PairingVector.Hex(a));
        Assert.Equal(PairingVector.AUnicodeBase64Url, PairingVector.B64(a));
        Assert.NotEqual(PairingVector.AUnicodeAsUtf16, PairingVector.Hex(a));
    }
}

/// <summary>
/// <see cref="PairingProofs"/> on its own, on a manual clock: single-use challenges for 30 s, at most 64, of their own
/// pairing; a removed or replaced pairing; the failed-proof warning.
/// </summary>
public sealed class PairingProofsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infopanel-proof-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero));
    private readonly List<LogEvent> _logged = [];
    private readonly PairingStore _store;
    private readonly EngineRuntime _runtime = new(47200, "proof-token_0123456789", []);
    private readonly PairingProofs _proofs;

    public PairingProofsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = PairingStore.Open(Path.Combine(_dir, "pairings.json"), _dir, _time, Serilog.Core.Logger.None);
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Collect(_logged)).CreateLogger();
        _proofs = new PairingProofs(_store, _runtime, "run-instance", _time, logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Nonce() => RandomNumberGenerator.GetBytes(32);

    /// <summary>A challenge's answer and the proof a client holding <paramref name="pairing"/>'s secret sends back for it.</summary>
    private (byte[] Np, PairingChallengeAnswer Challenge, byte[] C) Ask(PairingRecord pairing, int? port = null)
    {
        var np = Nonce();
        var challenge = _proofs.Challenge(pairing.Id, np)!;
        return (np, challenge, PairingProofs.ClientProof(PairingVector.Bytes(pairing.Secret), np, challenge.Nonce, port ?? _runtime.Port));
    }

    [Fact]
    public void AProof_AnswersTheToken_TheInstance_AndA_AndRecordsTheUse()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var (np, challenge, c) = Ask(pairing);
        var key = PairingVector.Bytes(pairing.Secret);
        Assert.Equal(PairingProofs.EngineProof(key, np, challenge.Nonce, _runtime.Port), challenge.Proof);

        var answer = _proofs.Prove(pairing.Id, challenge.Nonce, c)!;
        Assert.Equal(("proof-token_0123456789", "run-instance"), (answer.Token, answer.Instance));
        Assert.Equal(PairingProofs.AnswerProof(key, np, challenge.Nonce, _runtime.Port, answer.Token, answer.Instance), answer.Proof);
        Assert.Equal(_time.GetUtcNow(), _store.Find(pairing.Id)!.LastUsed);
    }

    [Fact]
    public void AChallenge_IsSingleUse()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var (_, challenge, c) = Ask(pairing);
        Assert.NotNull(_proofs.Prove(pairing.Id, challenge.Nonce, c));
        Assert.Null(_proofs.Prove(pairing.Id, challenge.Nonce, c));
    }

    [Fact]
    public void AChallenge_Lasts30Seconds()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var (_, early, earlyC) = Ask(pairing);
        var (_, late, lateC) = Ask(pairing);
        _time.Advance(PairingProofs.ChallengeLifetime - TimeSpan.FromTicks(1));
        Assert.NotNull(_proofs.Prove(pairing.Id, early.Nonce, earlyC));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(_proofs.Prove(pairing.Id, late.Nonce, lateC));
    }

    [Fact]
    public void AChallenge_IsItsPairingsOnly()
    {
        var mine = _store.Create("Chrome", null, _runtime.Port)!;
        var other = _store.Create("Edge", null, _runtime.Port)!;
        var np = Nonce();
        var challenge = _proofs.Challenge(mine.Id, np)!;
        // The other pairing's own proof over this challenge: the challenge isn't its.
        var c = PairingProofs.ClientProof(PairingVector.Bytes(other.Secret), np, challenge.Nonce, _runtime.Port);
        Assert.Null(_proofs.Prove(other.Id, challenge.Nonce, c));
    }

    [Fact]
    public void The65thChallenge_DropsTheFirst()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var asked = Enumerable.Range(0, PairingProofs.MaxChallenges + 1).Select(_ => Ask(pairing)).ToList();
        Assert.Null(_proofs.Prove(pairing.Id, asked[0].Challenge.Nonce, asked[0].C));
        Assert.NotNull(_proofs.Prove(pairing.Id, asked[1].Challenge.Nonce, asked[1].C));
        Assert.NotNull(_proofs.Prove(pairing.Id, asked[^1].Challenge.Nonce, asked[^1].C));
    }

    [Fact]
    public void ARemovedPairing_LosesItsChallenges_AndAnUnknownOneGetsNone()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var (_, challenge, c) = Ask(pairing);
        Assert.True(_store.Remove(pairing.Id));
        Assert.Null(_proofs.Prove(pairing.Id, challenge.Nonce, c));
        Assert.Null(_proofs.Challenge(pairing.Id, Nonce()));
    }

    [Fact]
    public void AReplacedPairing_AnswersTheChallenge_ButNeverTheProof()
    {
        var old = _store.Create("Chrome", null, _runtime.Port)!;
        _store.Create("Chrome", old.Id, _runtime.Port);
        var (np, challenge, c) = Ask(old);
        Assert.Equal(PairingProofs.EngineProof(PairingVector.Bytes(old.Secret), np, challenge.Nonce, _runtime.Port), challenge.Proof);
        Assert.Null(_proofs.Prove(old.Id, challenge.Nonce, c));
    }

    [Fact]
    public void AProofOverAnotherPort_IsRefused()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        var (_, challenge, c) = Ask(pairing, port: _runtime.Port + 1);
        Assert.Null(_proofs.Prove(pairing.Id, challenge.Nonce, c));
    }

    [Fact]
    public void AFailedProof_IsAWarning_AtMostOnceAMinute_WithACount()
    {
        var pairing = _store.Create("Chrome", null, _runtime.Port)!;
        for (var i = 0; i < 3; i++)
            Assert.Null(_proofs.Prove(pairing.Id, Ask(pairing).Challenge.Nonce, new byte[32]));
        var warnings = _logged.Where(e => e.Level == LogEventLevel.Warning).ToList();
        Assert.Single(warnings);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_proofs.Prove(pairing.Id, Ask(pairing).Challenge.Nonce, new byte[32]));
        warnings = [.. _logged.Where(e => e.Level == LogEventLevel.Warning)];
        Assert.Equal(2, warnings.Count);
        Assert.Contains("3", warnings[1].RenderMessage());
    }

    private sealed class Collect(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}

/// <summary>
/// The pairing proofs on the test host (spec/docs/api.md, "Pairing"): <c>POST /auth/pairing/challenge</c> and
/// <c>/proof</c>, the vector end to end, the refusals, CORS, a real relay on another port, and what is logged.
/// </summary>
public class PairingProofEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>A new pairing on <paramref name="host"/>, through a code's exchange: its id and key.</summary>
    private static async Task<(string Id, byte[] Key)> Pair(IServiceProvider services, HttpClient client, string? replaces = null)
    {
        var code = services.GetRequiredService<PairingCodes>().Mint();
        using var granted = await client.PostAsync("/auth/pairing/code", Body(replaces is null ? new { code, name = "Chrome" } : new { code, name = "Chrome", replaces }), Ct);
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var pairing = (await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing");
        return (pairing.GetProperty("id").GetString()!, PairingVector.Bytes(pairing.GetProperty("secret").GetString()!));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Text)> Post(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsync(path, Body(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default, text);
    }

    [Fact]
    public async Task TheVector_EndToEnd_OnAHostListeningOn51234()
    {
        var data = Path.Combine(factory.Root, "vector");
        Directory.CreateDirectory(data);
        const string id = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(Path.Combine(data, "pairings.json"),
            $$"""{"pairings":[{"id":"{{id}}","name":"Vector","created":"2026-11-02T09:30:00Z","secret":"{{PairingVector.B64(PairingVector.Key)}}"}]}""");
        using var engine = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", data).UseSetting("Engine:Port", PairingVector.Port.ToString()));
        using var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        engine.Services.GetRequiredService<PairingProofs>().NewNonce = () => [.. PairingVector.EngineNonce];

        var (status, challenge, _) = await Post(client, "/auth/pairing/challenge", new { pairing = id, nonce = PairingVector.B64(PairingVector.ClientNonce) });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(PairingVector.B64(PairingVector.EngineNonce), challenge.GetProperty("nonce").GetString());
        Assert.Equal(PairingVector.EBase64Url, challenge.GetProperty("proof").GetString());

        var (proved, session, _) = await Post(client, "/auth/pairing/proof", new { pairing = id, nonce = challenge.GetProperty("nonce").GetString(), proof = PairingVector.CBase64Url });
        Assert.Equal(HttpStatusCode.OK, proved);
        var token = session.GetProperty("token").GetString()!;
        var instance = session.GetProperty("instance").GetString()!;
        Assert.Equal(EngineFactory.Token, token);
        Assert.Equal(PairingVector.Hex(PairingProofs.AnswerProof(PairingVector.Key, PairingVector.ClientNonce, PairingVector.EngineNonce, PairingVector.Port, token, instance)),
            PairingVector.Hex(PairingVector.Bytes(session.GetProperty("proof").GetString()!)));
    }

    [Fact]
    public async Task AClientThatPaired_ProvesItself_AndGetsTheToken_OverTheEnginesPort()
    {
        using var client = factory.CreateRawClient();
        var (id, key) = await Pair(factory.Services, client);
        var np = RandomNumberGenerator.GetBytes(32);

        using var response = await client.PostAsync("/auth/pairing/challenge", Body(new { pairing = id, nonce = PairingVector.B64(np) }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        // Step 1 answers no token.
        Assert.DoesNotContain(EngineFactory.Token, text);
        var challenge = JsonDocument.Parse(text).RootElement;
        var ne = PairingVector.Bytes(challenge.GetProperty("nonce").GetString()!);
        Assert.Equal(PairingProofs.EngineProof(key, np, ne, EngineFactory.Port), PairingVector.Bytes(challenge.GetProperty("proof").GetString()!));

        var c = PairingProofs.ClientProof(key, np, ne, EngineFactory.Port);
        var (status, session, _) = await Post(client, "/auth/pairing/proof", new { pairing = id, nonce = PairingVector.B64(ne), proof = PairingVector.B64(c) });
        Assert.Equal(HttpStatusCode.OK, status);
        var token = session.GetProperty("token").GetString()!;
        var instance = session.GetProperty("instance").GetString()!;
        Assert.Equal(EngineFactory.Token, token);
        using (var api = factory.CreateApiClient())
            Assert.Equal((await api.GetFromJsonAsync<JsonElement>("/api/session", Ct)).GetProperty("instance").GetString(), instance);
        Assert.Equal(PairingProofs.AnswerProof(key, np, ne, EngineFactory.Port, token, instance), PairingVector.Bytes(session.GetProperty("proof").GetString()!));
    }

    [Fact]
    public async Task TheEnginesProof_IsOverItsOwnPort_WhateverTheHostHeaderSays()
    {
        using var client = factory.CreateRawClient();
        var (id, key) = await Pair(factory.Services, client);
        var np = RandomNumberGenerator.GetBytes(32);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/challenge") { Content = Body(new { pairing = id, nonce = PairingVector.B64(np) }) };
        request.Headers.Host = "127.0.0.1:1";
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ne = PairingVector.Bytes(challenge.GetProperty("nonce").GetString()!);
        Assert.Equal(PairingProofs.EngineProof(key, np, ne, EngineFactory.Port), PairingVector.Bytes(challenge.GetProperty("proof").GetString()!));
    }

    [Fact]
    public async Task TheRefusals_UnknownPairing404_ReflectionAndReplays403_OneText()
    {
        using var client = factory.CreateRawClient();
        var (id, key) = await Pair(factory.Services, client);

        var (unknown, _, _) = await Post(client, "/auth/pairing/challenge", new { pairing = "00000000000000000000000000000000", nonce = PairingVector.B64(RandomNumberGenerator.GetBytes(32)) });
        Assert.Equal(HttpStatusCode.NotFound, unknown);

        // The reflection: the engine's own E for Ne₁, sent back as the client's proof for Ne₁.
        var (_, challenge, _) = await Post(client, "/auth/pairing/challenge", new { pairing = id, nonce = PairingVector.B64(RandomNumberGenerator.GetBytes(32)) });
        var (reflected, refusal, _) = await Post(client, "/auth/pairing/proof",
            new { pairing = id, nonce = challenge.GetProperty("nonce").GetString(), proof = challenge.GetProperty("proof").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, reflected);

        // A proof over another port, then the same challenge again with the right proof: both refused, in the same words.
        var np = RandomNumberGenerator.GetBytes(32);
        var (_, second, _) = await Post(client, "/auth/pairing/challenge", new { pairing = id, nonce = PairingVector.B64(np) });
        var ne = PairingVector.Bytes(second.GetProperty("nonce").GetString()!);
        var (otherPort, otherRefusal, _) = await Post(client, "/auth/pairing/proof",
            new { pairing = id, nonce = PairingVector.B64(ne), proof = PairingVector.B64(PairingProofs.ClientProof(key, np, ne, EngineFactory.Port + 1)) });
        Assert.Equal(HttpStatusCode.Forbidden, otherPort);
        var (replayed, _, _) = await Post(client, "/auth/pairing/proof",
            new { pairing = id, nonce = PairingVector.B64(ne), proof = PairingVector.B64(PairingProofs.ClientProof(key, np, ne, EngineFactory.Port)) });
        Assert.Equal(HttpStatusCode.Forbidden, replayed);
        Assert.Equal(refusal.GetProperty("detail").GetString(), otherRefusal.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AReplacedPairing_AnswersTheChallenge_ButItsProofIs403()
    {
        using var client = factory.CreateRawClient();
        var (old, key) = await Pair(factory.Services, client);
        await Pair(factory.Services, client, replaces: old);
        var np = RandomNumberGenerator.GetBytes(32);
        var (status, challenge, _) = await Post(client, "/auth/pairing/challenge", new { pairing = old, nonce = PairingVector.B64(np) });
        Assert.Equal(HttpStatusCode.OK, status);
        var ne = PairingVector.Bytes(challenge.GetProperty("nonce").GetString()!);
        Assert.Equal(PairingProofs.EngineProof(key, np, ne, EngineFactory.Port), PairingVector.Bytes(challenge.GetProperty("proof").GetString()!));
        var (proved, _, text) = await Post(client, "/auth/pairing/proof", new { pairing = old, nonce = PairingVector.B64(ne), proof = PairingVector.B64(PairingProofs.ClientProof(key, np, ne, EngineFactory.Port)) });
        Assert.Equal(HttpStatusCode.Forbidden, proved);
        Assert.DoesNotContain(EngineFactory.Token, text);
    }

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "/auth/pairing/challenge", """{"pairing":"00000000000000000000000000000000","nonce":"short"}""", "schema" },
        { "/auth/pairing/challenge", """{"nonce":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"}""", "schema" },
        { "/auth/pairing/challenge", "{ not json", "json" },
        { "/auth/pairing/proof", """{"pairing":"00000000000000000000000000000000","nonce":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"}""", "schema" },
        { "/auth/pairing/proof", new string(' ', 4097) + "{}", "limit.tokenRequestBytes" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ABodyThatBreaksItsDefinition_Is422(string path, string body, string code)
    {
        using var client = factory.CreateRawClient();
        using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/auth/pairing/challenge")]
    [InlineData("/auth/pairing/proof")]
    public async Task TheProofs_AnswerTheCrossOrigin_RefuseOthers_AndGiveTheEnginesOwnOriginNoCors(string path)
    {
        using var client = factory.CreateRawClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.Add("Origin", EngineFactory.CrossOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        using (var answered = await client.SendAsync(preflight, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, answered.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }

        using var foreign = new HttpRequestMessage(HttpMethod.Options, path);
        foreign.Headers.Add("Origin", "https://other.example");
        foreign.Headers.Add("Access-Control-Request-Method", "POST");
        using (var refused = await client.SendAsync(foreign, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var own = new HttpRequestMessage(HttpMethod.Post, path) { Content = Body(new { pairing = "00000000000000000000000000000000", nonce = "x" }) };
        own.Headers.Add("Origin", $"http://127.0.0.1:{EngineFactory.Port}");
        using var ownAnswer = await client.SendAsync(own, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ownAnswer.StatusCode);
        Assert.False(ownAnswer.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ThroughARelayOnAnotherPort_TheEnginesProofDoesntCheck_AndAProofOverTheRelaysPortIs403()
    {
        // An engine of its own on real Kestrel (the fixture's test host holds its own data folder).
        using var engine = new EngineFactory();
        using var live = LiveEngine.Start(engine);
        using var direct = new HttpClient { BaseAddress = live.BaseAddress };
        var (id, key) = await Pair(live.Services, direct);
        await using var relay = TcpRelay.Start(live.Port);
        using var through = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{relay.Port}") };

        var np = RandomNumberGenerator.GetBytes(32);
        var (status, challenge, _) = await Post(through, "/auth/pairing/challenge", new { pairing = id, nonce = PairingVector.B64(np) });
        Assert.Equal(HttpStatusCode.OK, status);
        var ne = PairingVector.Bytes(challenge.GetProperty("nonce").GetString()!);
        var e = PairingVector.Bytes(challenge.GetProperty("proof").GetString()!);
        // The client checks E over the port it dialled, the relay's: it doesn't check, so it sends nothing more.
        Assert.NotEqual(PairingProofs.EngineProof(key, np, ne, relay.Port), e);
        Assert.Equal(PairingProofs.EngineProof(key, np, ne, live.Port), e);
        // Had it sent C, it would be over the relay's port, and the engine checks over its own.
        var (proved, _, text) = await Post(through, "/auth/pairing/proof", new { pairing = id, nonce = PairingVector.B64(ne), proof = PairingVector.B64(PairingProofs.ClientProof(key, np, ne, relay.Port)) });
        Assert.Equal(HttpStatusCode.Forbidden, proved);
        Assert.DoesNotContain(EngineFactory.Token, text);
    }

    [Fact]
    public async Task AFailedProof_IsAWarning_AndNothingLoggedCarriesTheCodeTokenSecretNoncesOrProofs()
    {
        using var client = factory.CreateRawClient();
        var code = factory.Services.GetRequiredService<PairingCodes>().Mint();
        using var granted = await client.PostAsync("/auth/pairing/code", Body(new { code, name = "Chrome" }), Ct);
        var pairing = (await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing");
        var id = pairing.GetProperty("id").GetString()!;
        var secret = pairing.GetProperty("secret").GetString()!;
        var key = PairingVector.Bytes(secret);
        var secrets = new List<string> { code, secret, EngineFactory.Token };

        foreach (var good in new[] { true, false })
        {
            var np = RandomNumberGenerator.GetBytes(32);
            var (_, challenge, _) = await Post(client, "/auth/pairing/challenge", new { pairing = id, nonce = PairingVector.B64(np) });
            var ne = challenge.GetProperty("nonce").GetString()!;
            var c = good ? PairingProofs.ClientProof(key, np, PairingVector.Bytes(ne), EngineFactory.Port) : new byte[32];
            var (status, session, _) = await Post(client, "/auth/pairing/proof", new { pairing = id, nonce = ne, proof = PairingVector.B64(c) });
            Assert.Equal(good ? HttpStatusCode.OK : HttpStatusCode.Forbidden, status);
            secrets.AddRange([PairingVector.B64(np), ne, challenge.GetProperty("proof").GetString()!, PairingVector.B64(c)]);
            if (good)
                secrets.Add(session.GetProperty("proof").GetString()!);
        }

        var recent = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(recent, e => e.Level == "warning" && e.Message.Contains("pairing proof", StringComparison.OrdinalIgnoreCase));
        var logged = string.Join("\n", recent.Select(e => e.Message + e.Exception));
        foreach (var file in Directory.GetFiles(Path.Combine(factory.DataDir, "logs")))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            logged += "\n" + new StreamReader(stream).ReadToEnd();
        }

        foreach (var s in secrets)
            Assert.DoesNotContain(s, logged);
    }

    /// <summary>A TCP relay on a loopback port of its own that pipes every connection to <c>127.0.0.1:{target}</c>.</summary>
    private sealed class TcpRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        private TcpRelay(int target)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _accepting = Accept(target);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static TcpRelay Start(int target) => new(target);

        private async Task Accept(int target)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Pipe(inbound, target);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task Pipe(TcpClient inbound, int target)
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                try
                {
                    await outbound.ConnectAsync(IPAddress.Loopback, target, _stop.Token);
                    var a = inbound.GetStream();
                    var b = outbound.GetStream();
                    await Task.WhenAny(a.CopyToAsync(b, _stop.Token), b.CopyToAsync(a, _stop.Token));
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            _stop.Dispose();
        }
    }
}
