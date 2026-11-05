using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfoPanel.Api;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// <see cref="PairingStore"/> on its own (spec/docs/api.md, "Pairing"): the paired clients in <c>pairings.json</c>, the
/// handover of a replaced pairing, the limit, the tolerant read, an unreadable file, privacy and the remembered port.
/// </summary>
public sealed class PairingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infopanel-pairing-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero));
    private readonly List<LogEvent> _logged = [];

    public PairingStoreTests() => Directory.CreateDirectory(_dir);

    private string File_ => Path.Combine(_dir, "pairings.json");

    private PairingStore Open(IWindowsSecurity? windows = null) =>
        PairingStore.Open(File_, _dir, _time, new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Collect(_logged)).CreateLogger(), windows);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string Stored(string id, string? replacedBy = null) =>
        $$"""{"id":"{{id}}","name":"Stored","created":"2026-11-01T08:00:00Z","secret":"{{new string('A', 43)}}"{{(replacedBy is null ? "" : $",\"replacedBy\":\"{replacedBy}\",\"replaced\":\"2026-11-01T09:00:00Z\"")}}}""";

    [Fact]
    public void APairing_IsCreatedListedAndRemoved_AndKeptAcrossARestart()
    {
        var store = Open();
        var a = store.Create("Chrome on macOS", null, 51234)!;
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = store.Create("Edge on Windows", null, 51234)!;

        Assert.Matches("^[0-9a-f]{32}$", a.Id);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Secret);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(a.Secret).Length);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.Equal([a.Id, b.Id], store.List().Select(p => p.Id));

        var reopened = Open();
        Assert.True(reopened.Readable);
        Assert.Equal(51234, reopened.Port);
        Assert.Equal([(a.Id, "Chrome on macOS", a.Secret, a.Created), (b.Id, "Edge on Windows", b.Secret, b.Created)],
            reopened.List().Select(p => (p.Id, p.Name, p.Secret, p.Created)));

        Assert.True(store.Remove(a.Id));
        Assert.False(store.Remove(a.Id));
        Assert.Equal([b.Id], Open().List().Select(p => p.Id));
    }

    [Fact]
    public void Replaces_HandsTheOldPairingOver_UnlistedUntilItsReplacementIsRemoved()
    {
        var store = Open();
        var a = store.Create("Chrome", null, 5000)!;
        _time.Advance(TimeSpan.FromMinutes(5));
        var b = store.Create("Chrome", a.Id, 5000)!;

        Assert.Equal([b.Id], store.List().Select(p => p.Id));
        var old = store.Find(a.Id)!;
        Assert.Equal(b.Id, old.ReplacedBy);
        Assert.Equal(_time.GetUtcNow(), old.Replaced);
        Assert.Equal(b.Id, Open().Find(a.Id)?.ReplacedBy);

        // Not listed, so not removable by itself; it goes with its replacement.
        Assert.False(store.Remove(a.Id));
        Assert.True(store.Remove(b.Id));
        Assert.Null(store.Find(a.Id));
        Assert.Null(Open().Find(a.Id));
    }

    [Fact]
    public void Replaces_APairingThatIsntThere_IsANoOp()
    {
        var store = Open();
        var a = store.Create("Chrome", "00000000000000000000000000000000", 5000)!;

        Assert.Equal([a.Id], store.List().Select(p => p.Id));
        Assert.Null(a.ReplacedBy);
    }

    [Fact]
    public void AReplacedPairing_Goes30DaysAfterItWasReplaced_AtTheNextExchangeOrStart()
    {
        var store = Open();
        var a = store.Create("Chrome", null, 5000)!;
        store.Create("Chrome", a.Id, 5000);

        _time.Advance(PairingStore.HandoverPeriod - TimeSpan.FromSeconds(1));
        store.Create("Other", null, 5000);
        Assert.NotNull(store.Find(a.Id));
        Assert.NotNull(Open().Find(a.Id));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(Open().Find(a.Id));
        Assert.NotNull(store.Find(a.Id));
        store.Create("Another", null, 5000);
        Assert.Null(store.Find(a.Id));
    }

    [Fact]
    public void The33rdPairing_DropsAReplacedOneFirst_ThenTheLeastRecentlyUsed()
    {
        var store = Open();
        var replaced = store.Create("p0", null, 5000)!;
        _time.Advance(TimeSpan.FromSeconds(1));
        var used = store.Create("p1", replaced.Id, 5000)!;
        var rest = new List<PairingRecord>();
        for (var i = 2; i < PairingStore.MaxPairings; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            rest.Add(store.Create($"p{i}", null, 5000)!);
        }

        // 32 in the file: the replaced one and 31 listed. The oldest listed one is used last.
        Assert.Equal(PairingStore.MaxPairings - 1, store.List().Count);
        _time.Advance(TimeSpan.FromMinutes(5));
        store.Touch(used.Id);

        _time.Advance(TimeSpan.FromSeconds(1));
        store.Create("p32", null, 5000);
        Assert.Null(store.Find(replaced.Id));
        Assert.NotNull(store.Find(used.Id));
        Assert.Equal(PairingStore.MaxPairings, store.List().Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        store.Create("p33", null, 5000);
        Assert.NotNull(store.Find(used.Id));
        Assert.Null(store.Find(rest[0].Id));
        Assert.Equal(PairingStore.MaxPairings, Open().List().Count);
    }

    [Fact]
    public void LastUse_IsWrittenToWithinAMinute()
    {
        var store = Open();
        var a = store.Create("Chrome", null, 5000)!;
        Assert.Null(a.LastUsed);

        store.Touch(a.Id);
        var first = Open().Find(a.Id)!.LastUsed;
        Assert.Equal(_time.GetUtcNow(), first);
        _time.Advance(TimeSpan.FromSeconds(30));
        store.Touch(a.Id);
        Assert.Equal(first, Open().Find(a.Id)!.LastUsed);
        _time.Advance(TimeSpan.FromSeconds(31));
        store.Touch(a.Id);
        Assert.Equal(_time.GetUtcNow(), Open().Find(a.Id)!.LastUsed);
    }

    /// <summary>
    /// Writes <paramref name="file"/> with <paramref name="count"/> stored pairings (named <c>Stored</c>) and
    /// <paramref name="port"/>, padded by a member the engine doesn't know to <paramref name="slack"/> bytes under
    /// <see cref="PairingStore.MaxFileBytes"/>; answers their ids.
    /// </summary>
    internal static string[] WriteAtTheCap(string file, int count, int slack, int port)
    {
        var ids = Enumerable.Range(0, count).Select(i => i.ToString("x32")).ToArray();
        string Content(int pad) => $$"""{"pairings":[{{string.Join(",", ids.Select(id => Stored(id)))}}],"port":{{port}},"pad":"{{new string('x', pad)}}"}""";
        var bare = Encoding.UTF8.GetByteCount(Content(0));
        File.WriteAllText(file, Content(PairingStore.MaxFileBytes - slack - bare));
        Assert.Equal(PairingStore.MaxFileBytes - slack, new FileInfo(file).Length);
        return ids;
    }

    [Fact]
    public void ARemoval_IsWritten_WithTheFileAtItsCap_AfterUsesThatCouldntBe()
    {
        // 20 bytes under the cap: no use can be written, since each adds a lastUsed member.
        var ids = WriteAtTheCap(File_, PairingStore.MaxPairings, slack: 20, port: 5000);
        var store = Open();
        Assert.True(store.Readable);
        foreach (var id in ids)
            store.Touch(id);
        Assert.All(store.List(), p => Assert.Equal(_time.GetUtcNow(), p.LastUsed));
        Assert.DoesNotContain("lastUsed", File.ReadAllText(File_));
        Assert.Contains(_logged, e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.StartsWith("Could not record the use"));

        // DELETE's removal, then a reused code's.
        Assert.True(store.Remove(ids[0]));
        Assert.True(store.RemoveAny(ids[1]));
        Assert.Equal(ids[2..], store.List().Select(p => p.Id));
        Assert.Equal(ids[2..], Open().List().Select(p => p.Id));
        // The uses that couldn't be written are still known for the limit's order, and tried again a minute later.
        Assert.All(store.List(), p => Assert.Equal(_time.GetUtcNow(), p.LastUsed));
    }

    [Fact]
    public void AWriteThatFailsBeforeTheFileIsReplaced_ChangesNothing()
    {
        // At the limit, replacing one: the write that fails would hand one over and drop it.
        var store = Open();
        var all = new List<PairingRecord>();
        for (var i = 0; i < PairingStore.MaxPairings; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            all.Add(store.Create($"p{i}", null, 5000)!);
        }

        var before = File.ReadAllBytes(File_);
        var removed = new List<string>();
        store.Removed += removed.Add;
        _logged.Clear();
        store.Saving = () => throw new IOException("The disk is full.");

        Assert.Throws<IOException>(() => store.Create("Chrome", all[^1].Id, 5001));
        Assert.Equal(all.Select(p => p.Id), store.List().Select(p => p.Id));
        Assert.All(all, p => Assert.Null(store.Find(p.Id)!.ReplacedBy));
        Assert.Equal(5000, store.Port);
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Empty(removed);
        Assert.DoesNotContain(_logged, e => e.MessageTemplate.Text.StartsWith("Removed") || e.MessageTemplate.Text.StartsWith("Paired"));
    }

    [Fact]
    public void AWriteThatFailsAfterTheFileIsReplaced_LeavesTheStoreAsTheFileIs()
    {
        var store = Open();
        var a = store.Create("Chrome", null, 5000)!;
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Flushing = () => throw new IOException("The folder couldn't be flushed.");

        // The file holds B, with A handed over to it: so does the store, and B is answered.
        var b = store.Create("Chrome", a.Id, 5001);
        Assert.NotNull(b);
        var file = Open();
        Assert.Equal([b.Id], store.List().Select(p => p.Id));
        Assert.Equal([(b.Id, b.Secret)], file.List().Select(p => (p.Id, p.Secret)));
        Assert.Equal(b.Id, store.Find(a.Id)?.ReplacedBy);
        Assert.Equal(b.Id, file.Find(a.Id)?.ReplacedBy);
        Assert.Equal(5001, store.Port);
        Assert.Contains(_logged, e => e.Level == LogEventLevel.Warning && e.Exception?.Message == "The folder couldn't be flushed.");

        // A removal the same: gone from the file, so gone from the store.
        Assert.True(store.Remove(b.Id));
        Assert.Null(store.Find(b.Id));
        Assert.Null(store.Find(a.Id));
        Assert.Null(Open().Find(b.Id));
    }

    [Fact]
    public void UnknownMembers_AreKept_AtTheTopAndInEachPairing()
    {
        var id = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(File_, $$$"""{"pairings":[{{{Stored(id)[..^1]}}},"later":{"x":[1,2]}}],"port":5000,"future":"kept"}""");

        var store = Open();
        Assert.True(store.Readable);
        Assert.Equal([id], store.List().Select(p => p.Id));
        store.Create("Chrome", null, 5001);

        var text = File.ReadAllText(File_);
        Assert.Contains("\"future\":\"kept\"", text);
        Assert.Contains("\"later\":{\"x\":[1,2]}", text);
        Assert.Equal(5001, Open().Port);
    }

    public static TheoryData<string> Unreadable() =>
    [
        "{ not json",
        "[]",
        """{"pairings":{}}""",
        $$"""{"pairings":[{{Stored("0123456789ABCDEF0123456789ABCDEF")}}]}""",
        $$"""{"pairings":[{{Stored("0123456789abcdef0123456789abcdef").Replace(new string('A', 43), "short")}}]}""",
        $$"""{"pairings":[],"port":"5000"}""",
        $$"""{"pairings":[],"pad":"{{new string('x', 1024 * 1024)}}"}""",
    ];

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void AnUnreadableFile_IsNeverRewritten_AndTheRunHasNoPairings(string content)
    {
        File.WriteAllText(File_, content);
        var before = File.ReadAllBytes(File_);

        var store = Open();
        Assert.False(store.Readable);
        Assert.Equal(File_, store.FilePath);
        Assert.Empty(store.List());
        Assert.Null(store.Port);
        Assert.Null(store.Create("Chrome", null, 5000));
        store.RememberPort(5001);
        Assert.Equal(before, File.ReadAllBytes(File_));
    }

    [Fact]
    public void AFolderAtThePath_IsUnreadable_AndLeftAsItIs()
    {
        Directory.CreateDirectory(File_);
        var store = Open();
        Assert.False(store.Readable);
        Assert.Null(store.Create("Chrome", null, 5000));
        Assert.True(Directory.Exists(File_));
    }

    [Fact]
    public void TheFile_IsOwnerOnly_OnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes: Windows' access list is the seam's (below).");
        Open().Create("Chrome", null, 5000);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
    }

    [Fact]
    public void OnWindows_EveryWrite_GoesThroughATempFileMadeByTheSecuritySeam()
    {
        var seam = new CreatingSeam();
        var store = Open(seam);
        store.Create("Chrome", null, 5000);
        store.RememberPort(5001);

        Assert.Equal(2, seam.Created.Count);
        Assert.All(seam.Created, path => Assert.Matches("pairings\\.json\\.[0-9a-f]{32}\\.tmp$", path));
        Assert.Equal(5001, Open().Port);
    }

    [Fact]
    public void ThePort_IsWrittenWithAPairing_AndAtStartWhenItDiffers_AndNoFileWithoutPairings()
    {
        var store = Open();
        store.RememberPort(5000);
        Assert.False(File.Exists(File_));
        Assert.Null(store.Port);

        store.Create("Chrome", null, 5001);
        Assert.Equal(5001, Open().Port);
        var writes = 0;
        store.Saving = () => writes++;
        store.RememberPort(5001);
        Assert.Equal(0, writes);
        store.RememberPort(5002);
        Assert.Equal(1, writes);
        Assert.Equal(5002, Open().Port);
    }

    private sealed class Collect(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>The Windows seam: each temp file it is asked for, created plainly.</summary>
    private sealed class CreatingSeam : IWindowsSecurity
    {
        public List<string> Created { get; } = [];

        public string CurrentUser => "S-1-5-21-1-2-3-1001";

        public WindowsFolderAcl Read(string folder) => throw new NotSupportedException();

        public void CreateFolder(string folder, string user) => throw new NotSupportedException();

        public void Protect(string folder, string user) => throw new NotSupportedException();

        public FileStream CreateFile(string path, string user)
        {
            Assert.Equal(CurrentUser, user);
            Created.Add(path);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
    }
}

/// <summary><see cref="PairingCodes"/>: the one-time codes a link carries.</summary>
public sealed class PairingCodesTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public void ACode_Is22Base64UrlCharacters_AndNewEachTime()
    {
        var codes = new PairingCodes(_time, _ => true);
        var minted = Enumerable.Range(0, 8).Select(_ => codes.Mint()).ToList();
        Assert.All(minted, code => Assert.Matches("^[A-Za-z0-9_-]{22}$", code));
        Assert.Equal(minted.Count, minted.Distinct().Count());
    }

    [Fact]
    public void AtMost8AreOutstanding_TheOldestDropped()
    {
        var codes = new PairingCodes(_time, _ => true);
        var minted = Enumerable.Range(0, PairingCodes.MaxOutstanding + 1).Select(_ => codes.Mint()).ToList();

        Assert.Equal(CodeOutcome.Refused, codes.Redeem(minted[0], () => "p0").Outcome);
        Assert.All(minted.Skip(1), code => Assert.Equal(CodeOutcome.Paired, codes.Redeem(code, () => "p").Outcome));
    }

    [Fact]
    public void ACode_LastsTwoMinutesFromMinting()
    {
        var codes = new PairingCodes(_time, _ => true);
        var early = codes.Mint();
        var late = codes.Mint();
        _time.Advance(PairingCodes.Lifetime - TimeSpan.FromTicks(1));
        Assert.Equal(CodeOutcome.Paired, codes.Redeem(early, () => "p").Outcome);
        _time.Advance(TimeSpan.FromTicks(1));
        var asked = false;
        Assert.Equal(CodeOutcome.Refused, codes.Redeem(late, () => { asked = true; return "p"; }).Outcome);
        Assert.False(asked);
    }

    [Fact]
    public void TheFirstUse_Pairs_ASecondIsRefusedAndNamesThePairingToRemove_UntilTheTwoMinutesEnd()
    {
        var codes = new PairingCodes(_time, _ => true);
        var code = codes.Mint();

        Assert.Equal(new CodeUse(CodeOutcome.Paired, "p1"), codes.Redeem(code, () => "p1"));
        Assert.Equal(new CodeUse(CodeOutcome.Reused, "p1"), codes.Redeem(code, () => throw new InvalidOperationException("paired twice")));
        Assert.Equal(CodeOutcome.Refused, codes.Redeem("AAAAAAAAAAAAAAAAAAAAAA", () => "p2").Outcome);
        _time.Advance(PairingCodes.Lifetime);
        Assert.Equal(new CodeUse(CodeOutcome.Refused, null), codes.Redeem(code, () => "p3"));
    }

    [Fact]
    public void AFirstUseThatPairsNothing_IsStillUsed()
    {
        var codes = new PairingCodes(_time, _ => true);
        var code = codes.Mint();
        Assert.Equal(new CodeUse(CodeOutcome.Paired, null), codes.Redeem(code, () => null));
        Assert.Equal(new CodeUse(CodeOutcome.Reused, null), codes.Redeem(code, () => "p"));
    }
}

/// <summary>
/// The used codes' history with real pairings and proofs, on a manual clock: a used code's second use within its two
/// minutes revokes its pairing however many codes were used since, while the history stays bounded.
/// </summary>
public sealed class PairingCodeHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infopanel-code-history-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start;
    private readonly EngineRuntime _runtime = new(47201, "history-token_0123456789", []);
    private readonly PairingStore _store;
    private readonly PairingProofs _proofs;
    private readonly PairingCodes _codes;
    private readonly ListLogger _logger = new();

    public PairingCodeHistoryTests()
    {
        Directory.CreateDirectory(_dir);
        _start = _time.GetUtcNow();
        _store = PairingStore.Open(Path.Combine(_dir, "pairings.json"), _dir, _time, Serilog.Core.Logger.None);
        _proofs = new PairingProofs(_store, _runtime, "run-instance", _time, Serilog.Core.Logger.None);
        // As the engine makes it (Program.cs).
        _codes = new PairingCodes(_time, id => _store.Find(id) is not null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void At(int seconds) => _time.Advance(_start + TimeSpan.FromSeconds(seconds) - _time.GetUtcNow());

    private (CodeOutcome Outcome, PairingRecord? Created) Use(string code) =>
        PairingEndpoints.Redeem(_codes, _store, code, "Chrome", null, _runtime.Port, _logger);

    private PairingRecord Exchange(string code)
    {
        var (outcome, created) = Use(code);
        Assert.Equal(CodeOutcome.Paired, outcome);
        return created!;
    }

    private void Exchanges(int count)
    {
        for (var i = 0; i < count; i++)
            Exchange(_codes.Mint());
    }

    /// <summary>A successful proof of <paramref name="pairing"/>, which records its use.</summary>
    private void Prove(PairingRecord pairing)
    {
        var np = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PairingProofs.NonceBytes);
        var challenge = _proofs.Challenge(pairing.Id, np)!;
        var key = System.Buffers.Text.Base64Url.DecodeFromChars(pairing.Secret);
        Assert.NotNull(_proofs.Prove(pairing.Id, challenge.Nonce, PairingProofs.ClientProof(key, np, challenge.Nonce, _runtime.Port)));
    }

    [Fact]
    public void AReusedCode_RevokesThePairingProofsKeptAlive_ThoughMoreThan64CodesWereUsedSince()
    {
        // Codex's sequence, all within the first code's two minutes.
        var c0 = _codes.Mint();
        var first = Exchange(c0);
        At(1);
        Exchanges(31);
        At(2);
        Prove(first);
        At(3);
        Exchanges(31);
        At(62);
        Prove(first);
        At(63);
        Exchanges(2);
        Assert.NotNull(_store.Find(first.Id));
        Assert.Equal(PairingStore.MaxPairings, _store.List().Count);

        At(64);
        var (outcome, created) = Use(c0);
        Assert.Equal(CodeOutcome.Reused, outcome);
        Assert.Null(created);
        Assert.Null(_store.Find(first.Id));
        Assert.Null(PairingStore.Open(_store.FilePath, _dir, _time, Serilog.Core.Logger.None).Find(first.Id));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("used twice") && e.Message.Contains(first.Id));
        Assert.True(_codes.Remembered <= PairingCodes.MaxRemembered);
    }

    [Fact]
    public void TheUsedCodes_AreAtMost64_EveryPairingsCodeAmongThem()
    {
        // Each existing pairing holds one used code, and there are at most 32, so 64 is enough for them all.
        var made = new List<(string Code, PairingRecord Pairing)>();
        for (var i = 0; i < 4 * PairingCodes.MaxRemembered; i++)
        {
            var code = _codes.Mint();
            made.Add((code, Exchange(code)));
            Assert.True(_codes.Remembered <= PairingCodes.MaxRemembered);
        }

        Assert.Equal(PairingCodes.MaxRemembered, _codes.Remembered);
        var kept = made.Where(m => _store.Find(m.Pairing.Id) is not null).ToList();
        Assert.Equal(PairingStore.MaxPairings, kept.Count);
        Assert.All(kept, m => Assert.Equal(CodeOutcome.Reused, Use(m.Code).Outcome));
        Assert.Empty(_store.List());
    }

    /// <summary>Keeps what is logged, formatted.</summary>
    internal sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// The pairing endpoints on the test host: <c>POST /auth/pairing/code</c> (public, CORS) and <c>/api/pairings</c>
/// (the session token only), with their refusals; a <c>pairings.json</c> the engine can't read.
/// </summary>
public class PairingEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Mint() => factory.Services.GetRequiredService<PairingCodes>().Mint();

    private static Task<HttpResponseMessage> Exchange(HttpClient client, object body) =>
        client.PostAsync("/auth/pairing/code", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), Ct);

    private async Task<string[]> ListedIds()
    {
        using var session = factory.CreateApiClient();
        var listed = await session.GetFromJsonAsync<JsonElement>("/api/pairings", Ct);
        return [.. listed.EnumerateArray().Select(p => p.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task ACode_ExchangesOnce_ForTheRunsTokenAndALastingPairing_AndASecondUseRemovesIt()
    {
        using var anonymous = factory.CreateRawClient();
        var code = Mint();

        using var granted = await Exchange(anonymous, new { code, name = "Chrome on macOS" });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal("no-store", granted.Headers.CacheControl?.ToString());
        var grant = await granted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(EngineFactory.Token, grant.GetProperty("token").GetString());
        using (var session = factory.CreateApiClient())
            Assert.Equal((await session.GetFromJsonAsync<JsonElement>("/api/session", Ct)).GetProperty("instance").GetString(), grant.GetProperty("instance").GetString());
        var pairing = grant.GetProperty("pairing");
        var id = pairing.GetProperty("id").GetString()!;
        var secret = pairing.GetProperty("secret").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.Equal("Chrome on macOS", pairing.GetProperty("name").GetString());

        using (var session = factory.CreateApiClient())
        {
            using var listed = await session.GetAsync("/api/pairings", Ct);
            Assert.Equal("no-store", listed.Headers.CacheControl?.ToString());
            var text = await listed.Content.ReadAsStringAsync(Ct);
            Assert.Contains(id, text);
            Assert.DoesNotContain(secret, text);
        }

        using var again = await Exchange(anonymous, new { code, name = "Elsewhere" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        var refused = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.DoesNotContain(id, await ListedIds());
        var recent = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(recent, e => e.Level == "warning" && e.Message.Contains("used twice") && e.Message.Contains(id));

        using var unknown = await Exchange(anonymous, new { code = "AAAAAAAAAAAAAAAAAAAAAA", name = "Chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(refused.GetProperty("detail").GetString(), (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());

        // Nothing logged carries the code, the secret or the token.
        var logged = string.Join("\n", factory.Services.GetRequiredService<RecentLog>().After(0).Select(e => e.Message + e.Exception));
        Assert.DoesNotContain(code, logged);
        Assert.DoesNotContain(secret, logged);
        Assert.DoesNotContain(EngineFactory.Token, logged);
    }

    [Fact]
    public async Task Replaces_HandsTheOldPairingOver_AndOnlyTheNewOneIsListed()
    {
        using var anonymous = factory.CreateRawClient();
        using var first = await Exchange(anonymous, new { code = Mint(), name = "Chrome" });
        var old = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;
        using var second = await Exchange(anonymous, new { code = Mint(), name = "Chrome", replaces = old });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var next = (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;

        var listed = await ListedIds();
        Assert.Contains(next, listed);
        Assert.DoesNotContain(old, listed);
        Assert.Equal(next, factory.Services.GetRequiredService<PairingStore>().Find(old)?.ReplacedBy);
    }

    [Fact]
    public async Task Pairings_AreTheSessionTokensOnly()
    {
        using var anonymous = factory.CreateRawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/pairings", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/pairings/00000000000000000000000000000000", Ct)).StatusCode);

        using var session = factory.CreateApiClient();
        using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "pairing-scope", scopes = new[] { "edit" } }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
        using var integration = factory.CreateRawClient();
        integration.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var listed = await integration.GetAsync("/api/pairings", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal("session", (await listed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await integration.DeleteAsync("/api/pairings/00000000000000000000000000000000", Ct)).StatusCode);
    }

    [Fact]
    public async Task Remove_Answers204_ThenTheIdIs404()
    {
        using var anonymous = factory.CreateRawClient();
        using var granted = await Exchange(anonymous, new { code = Mint(), name = "Chrome" });
        var id = (await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;

        using var session = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.NoContent, (await session.DeleteAsync($"/api/pairings/{id}", Ct)).StatusCode);
        Assert.DoesNotContain(id, await ListedIds());
        Assert.Equal(HttpStatusCode.NotFound, (await session.DeleteAsync($"/api/pairings/{id}", Ct)).StatusCode);
    }

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "over", new string(' ', 4097) + "{}", "limit.tokenRequestBytes" },
        { "not json", "{ not json", "json" },
        { "no code", """{"name":"Chrome"}""", "schema" },
        { "short code", """{"code":"short","name":"Chrome"}""", "schema" },
        { "bad name", """{"code":"AAAAAAAAAAAAAAAAAAAAAA","name":" Chrome"}""", "schema" },
        { "bad replaces", """{"code":"AAAAAAAAAAAAAAAAAAAAAA","name":"Chrome","replaces":"X"}""", "schema" },
        { "unknown member", """{"code":"AAAAAAAAAAAAAAAAAAAAAA","name":"Chrome","token":"x"}""", "schema" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task TheExchange_RefusesABodyThatBreaksItsDefinition_422(string what, string body, string code)
    {
        using var anonymous = factory.CreateRawClient();
        using var response = await anonymous.PostAsync("/auth/pairing/code", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, what);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(code, problem.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheExchange_AnswersTheCrossOrigin_AndRefusesOthers()
    {
        using var client = factory.CreateRawClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/auth/pairing/code");
        preflight.Headers.Add("Origin", EngineFactory.CrossOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        preflight.Headers.Add("Access-Control-Request-Private-Network", "true");
        using (var answered = await client.SendAsync(preflight, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, answered.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("POST", answered.Headers.GetValues("Access-Control-Allow-Methods").Single());
            Assert.Equal("true", answered.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
        }

        using var post = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/code")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code = Mint(), name = "Chrome" }), Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("Origin", EngineFactory.CrossOrigin);
        using (var granted = await client.SendAsync(post, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, granted.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }

        using var foreign = new HttpRequestMessage(HttpMethod.Options, "/auth/pairing/code");
        foreign.Headers.Add("Origin", "https://other.example");
        foreign.Headers.Add("Access-Control-Request-Method", "POST");
        using var refused = await client.SendAsync(foreign, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>An engine of its own on a new data folder, and a client for it with the session token.</summary>
    private (WebApplicationFactory<Program> Engine, HttpClient Client, string Data) OwnEngine(string name, Action<string>? prepare = null)
    {
        var data = Path.Combine(factory.Root, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);
        prepare?.Invoke(data);
        var engine = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", data));
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        return (engine, client, data);
    }

    [Fact]
    public async Task AtItsCap_AfterUsesThatCouldntBeWritten_DeleteAndAReusedCode_StillRemove()
    {
        string[] ids = [];
        var (engine, client, data) = OwnEngine("pairings-cap",
            data => ids = PairingStoreTests.WriteAtTheCap(Path.Combine(data, "pairings.json"), PairingStore.MaxPairings, slack: 20, port: EngineFactory.Port));
        using var ownEngine = engine;
        using var ownClient = client;
        var file = Path.Combine(data, "pairings.json");
        var store = engine.Services.GetRequiredService<PairingStore>();
        foreach (var id in ids)
            store.Touch(id);
        Assert.DoesNotContain("lastUsed", File.ReadAllText(file));

        using (var removed = await client.DeleteAsync($"/api/pairings/{ids[0]}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain(ids[0], File.ReadAllText(file));

        // The exchange's pairing takes the removed one's room; its use can't be written either.
        var code = engine.Services.GetRequiredService<PairingCodes>().Mint();
        using var granted = await Exchange(client, new { code, name = "Chrome" });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.True((await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("pairing", out var pairing));
        var made = pairing.GetProperty("id").GetString()!;
        store.Touch(made);
        Assert.Contains(made, File.ReadAllText(file));

        using var again = await Exchange(client, new { code, name = "Elsewhere" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Null(store.Find(made));
        Assert.DoesNotContain(made, File.ReadAllText(file));
        Assert.Contains(engine.Services.GetRequiredService<RecentLog>().After(0),
            e => e.Level == "warning" && e.Message.Contains("used twice") && e.Message.Contains(made));
    }

    [Fact]
    public async Task AWriteThatFails_BeforeTheFileIsReplaced_PairsNothing_AndAfter_GivesTheClientThePairingTheFileHolds()
    {
        var (engine, client, data) = OwnEngine("pairings-flush");
        using var ownEngine = engine;
        using var ownClient = client;
        var store = engine.Services.GetRequiredService<PairingStore>();
        var codes = engine.Services.GetRequiredService<PairingCodes>();
        using var first = await Exchange(client, new { code = codes.Mint(), name = "Chrome" });
        var a = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;

        // Before: nothing changes, and the client connects for this run only.
        var before = File.ReadAllBytes(Path.Combine(data, "pairings.json"));
        store.Saving = () => throw new IOException("The disk is full.");
        using (var unpaired = await Exchange(client, new { code = codes.Mint(), name = "Chrome", replaces = a }))
        {
            Assert.Equal(HttpStatusCode.Created, unpaired.StatusCode);
            Assert.False((await unpaired.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("pairing", out _));
        }

        Assert.Equal([a], await ListedIds(client));
        Assert.Null(store.Find(a)!.ReplacedBy);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(data, "pairings.json")));
        store.Saving = null;

        // After: the file holds B, handed A over; the engine follows it, and the client gets B's secret.
        store.Flushing = () => throw new IOException("The folder couldn't be flushed.");
        using var granted = await Exchange(client, new { code = codes.Mint(), name = "Chrome", replaces = a });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.True((await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("pairing", out var pairing));
        var b = pairing.GetProperty("id").GetString()!;
        Assert.Equal([b], await ListedIds(client));
        Assert.Equal(b, store.Find(a)?.ReplacedBy);
        var file = PairingStore.Open(Path.Combine(data, "pairings.json"), data, TimeProvider.System, Serilog.Core.Logger.None);
        Assert.Equal(pairing.GetProperty("secret").GetString(), file.Find(b)?.Secret);
        Assert.Equal(b, file.Find(a)?.ReplacedBy);
    }

    [Fact]
    public async Task AReusedCode_RevokesItsPairing_ThoughMoreThan64CodesWereUsedSince()
    {
        // The first pairing outlives 64 more exchanges, each replacing the one before (replaced ones go first at the limit).
        var (engine, client, _) = OwnEngine("pairings-history");
        using var _e = engine;
        using var _c = client;
        var codes = engine.Services.GetRequiredService<PairingCodes>();
        var c0 = codes.Mint();
        using var first = await Exchange(client, new { code = c0, name = "Chrome" });
        var a = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;
        string? previous = null;
        for (var i = 0; i < PairingCodes.MaxRemembered; i++)
        {
            using var next = previous is null
                ? await Exchange(client, new { code = codes.Mint(), name = "Edge" })
                : await Exchange(client, new { code = codes.Mint(), name = "Edge", replaces = previous });
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
            previous = (await next.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;
        }

        Assert.Contains(a, await ListedIds(client));
        using var again = await Exchange(client, new { code = c0, name = "Elsewhere" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.DoesNotContain(a, await ListedIds(client));
        Assert.Contains(engine.Services.GetRequiredService<RecentLog>().After(0),
            e => e.Level == "warning" && e.Message.Contains("used twice") && e.Message.Contains(a));
    }

    private static async Task<string[]> ListedIds(HttpClient session)
    {
        var listed = await session.GetFromJsonAsync<JsonElement>("/api/pairings", Ct);
        return [.. listed.EnumerateArray().Select(p => p.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task AnUnreadablePairingsFile_Is409_TheExchangeConnectsWithoutAPairing_AndTheFileIsLeft()
    {
        var data = Path.Combine(factory.Root, "pairings-unreadable");
        Directory.CreateDirectory(data);
        var file = Path.Combine(data, "pairings.json");
        File.WriteAllText(file, "{ not json");
        using var engine = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", data));
        using var session = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        session.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);

        using var listed = await session.GetAsync("/api/pairings", Ct);
        Assert.Equal(HttpStatusCode.Conflict, listed.StatusCode);
        Assert.Contains("pairings.json", (await listed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await session.DeleteAsync("/api/pairings/00000000000000000000000000000000", Ct)).StatusCode);

        using var granted = await Exchange(session, new { code = engine.Services.GetRequiredService<PairingCodes>().Mint(), name = "Chrome" });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var grant = await granted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(grant.TryGetProperty("pairing", out _));
        Assert.Equal(EngineFactory.Token, grant.GetProperty("token").GetString());
        Assert.Equal("{ not json", File.ReadAllText(file));
    }

    // POST /api/pairings/codes: a session mints a one-time code for a client on another computer.

    private static async Task<(string Code, string Expires, string[] Origins)> Issue(HttpClient session)
    {
        using var issued = await session.PostAsync("/api/pairings/codes", null, Ct);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("code").GetString()!, body.GetProperty("expires").GetString()!,
            [.. body.GetProperty("origins").EnumerateArray().Select(o => o.GetString()!)]);
    }

    private static Task<HttpResponseMessage> ExchangeFrom(HttpClient client, string origin, string code)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/code")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code, name = "Chrome elsewhere" }), Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("Origin", origin);
        return client.SendAsync(post, Ct);
    }

    [Fact]
    public async Task AnIssuedCode_Is201_NoStore_WithItsExpiryAndTheCrossOrigins_AndExchangesOnceFromOne()
    {
        using var session = factory.CreateApiClient();
        var before = DateTimeOffset.UtcNow;
        using var issued = await session.PostAsync("/api/pairings/codes", null, Ct);
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.Equal("no-store", issued.Headers.CacheControl?.ToString());
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["code", "expires", "origins"], body.EnumerateObject().Select(p => p.Name).Order());
        var code = body.GetProperty("code").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{22}$", code);
        var expiresText = body.GetProperty("expires").GetString()!;
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", expiresText);
        var expires = DateTimeOffset.Parse(expiresText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires, before + PairingCodes.Lifetime - TimeSpan.FromSeconds(1), after + PairingCodes.Lifetime);
        // The cross origins only: not the engine's own loopback origins, nor a dev origin (it gets no CORS).
        Assert.Equal([EngineFactory.CrossOrigin], body.GetProperty("origins").EnumerateArray().Select(o => o.GetString()!));

        // A second code is a new one.
        Assert.NotEqual(code, (await Issue(session)).Code);

        using var anonymous = factory.CreateRawClient();
        using (var granted = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, code))
        {
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, granted.Headers.GetValues("Access-Control-Allow-Origin").Single());
            var grant = await granted.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(EngineFactory.Token, grant.GetProperty("token").GetString());
            Assert.Contains(grant.GetProperty("pairing").GetProperty("id").GetString()!, await ListedIds());
        }

        using var again = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, code);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(PairingEndpoints.RefusedCode, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task IssuingACode_IsTheSessionTokensOnly()
    {
        using var anonymous = factory.CreateRawClient();
        using (var none = await anonymous.PostAsync("/api/pairings/codes", null, Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var session = factory.CreateApiClient();
        foreach (var scopes in new[] { new[] { "read" }, ["edit"], ["read", "edit"] })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "codes-" + string.Join("-", scopes), scopes }, Ct);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var integration = factory.CreateRawClient();
            integration.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var refused = await integration.PostAsync("/api/pairings/codes", null, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("session", problem.GetProperty("scope").GetString());
        }
    }

    [Fact]
    public async Task AnIssuedCode_IsAnyOtherCode_ItCountsTowardTheEightOutstanding_AndLastsTwoMinutes()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero));
        var data = Path.Combine(factory.Root, $"pairing-codes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);
        using var engine = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Engine:DataDir", data);
            b.ConfigureTestServices(services => services.AddSingleton(sp =>
                new PairingCodes(clock, id => sp.GetRequiredService<PairingStore>().Find(id) is not null)));
        });
        using var session = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        session.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        using var anonymous = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var codes = engine.Services.GetRequiredService<PairingCodes>();

        // Its time: two minutes from issuing, as the code link's.
        var (timely, expires, _) = await Issue(session);
        Assert.Equal("2026-11-05T09:32:00Z", expires);
        var (late, _, _) = await Issue(session);
        clock.Advance(PairingCodes.Lifetime - TimeSpan.FromSeconds(1));
        using (var granted = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, timely))
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (var expired = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, late))
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);

        // The same store as the code link's: issued and minted codes share the 8, the oldest dropped.
        var (oldest, _, _) = await Issue(session);
        var newer = new List<string>();
        for (var i = 0; i < PairingCodes.MaxOutstanding / 2; i++)
            newer.Add(codes.Mint());
        for (var i = 0; i < PairingCodes.MaxOutstanding / 2; i++)
            newer.Add((await Issue(session)).Code);
        using (var dropped = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, oldest))
            Assert.Equal(HttpStatusCode.Forbidden, dropped.StatusCode);
        // A minted code is still taken, and an issued one at the end.
        using (var minted = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, newer[0]))
            Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        using (var last = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, newer[^1]))
            Assert.Equal(HttpStatusCode.Created, last.StatusCode);
    }

    [Fact]
    public async Task IssuingACode_IsLogged_AndNoLogHoldsTheCode()
    {
        using var session = factory.CreateApiClient();
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var from = recent.After(0).Select(e => e.Seq).DefaultIfEmpty(0).Max();
        var (code, _, _) = await Issue(session);
        using (var anonymous = factory.CreateRawClient())
        using (var granted = await ExchangeFrom(anonymous, EngineFactory.CrossOrigin, code))
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);

        var entries = recent.After(from);
        Assert.Contains(entries, e => e.Level == "information" && e.Message.Contains("pairing code was issued", StringComparison.Ordinal));
        var logged = string.Join("\n", recent.After(0).Select(e => e.Message + e.Exception));
        foreach (var file in Directory.GetFiles(Path.Combine(factory.DataDir, "logs")))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            logged += "\n" + new StreamReader(stream).ReadToEnd();
        }

        Assert.DoesNotContain(code, logged);
        Assert.DoesNotContain(EngineFactory.Token, logged);
        // GET /api/logs answers the same entries: none carries the code.
        using var logs = await session.GetAsync("/api/logs?after=0", Ct);
        Assert.DoesNotContain(code, await logs.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void TheCodes_AreOutOfReachOfTheMcpToolsAndTheRelay()
    {
        // Every tool a call from elsewhere reaches declares read or edit; issuing a code needs the session.
        var host = factory.Services.GetRequiredService<InfoPanel.Mcp.Tools.McpToolHost>();
        Assert.NotEmpty(host.All);
        Assert.All(host.All, t => Assert.True(t.Scope is ApiScope.Read or ApiScope.Edit, t.Name));
        Assert.DoesNotContain(InfoPanel.Mcp.Tools.McpToolHost.ToolNames, n => n.Contains("pair", StringComparison.OrdinalIgnoreCase));

        // Nothing the tools or the relay client are built from, or hold, reaches the codes, the pairings, the run's token
        // or the services (from which anything could be asked for).
        Type[] forbidden = [typeof(PairingCodes), typeof(PairingStore), typeof(EngineRuntime), typeof(IServiceProvider), typeof(HttpClient)];
        var relay = typeof(InfoPanel.Service.RelayClient);
        var types = typeof(InfoPanel.Mcp.Tools.McpToolHost).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && (ns == "InfoPanel.Mcp" || ns.StartsWith("InfoPanel.Mcp.", StringComparison.Ordinal)))
            .Concat(relay.Assembly.GetTypes().Where(t => t == relay || t.FullName!.StartsWith(relay.FullName + "+", StringComparison.Ordinal)))
            .ToList();
        Assert.Contains(typeof(InfoPanel.Mcp.Tools.McpToolHost), types);
        Assert.Contains(relay, types);
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        var reaching = types
            .SelectMany(t => t.GetConstructors(All).SelectMany(c => c.GetParameters()).Select(p => (t, p.ParameterType))
                .Concat(t.GetFields(All).Select(f => (t, f.FieldType))))
            .Where(m => forbidden.Any(f => f.IsAssignableFrom(m.Item2) || (m.Item2.IsGenericType && m.Item2.GetGenericArguments().Any(f.IsAssignableFrom))))
            .Select(m => $"{m.t.FullName}: {m.Item2}")
            .ToList();
        Assert.Empty(reaching);
    }
}
