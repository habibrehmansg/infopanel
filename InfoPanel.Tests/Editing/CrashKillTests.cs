using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// A crash never exposes a mixed generation (the document store's crash safety, end to end): the built engine runs as
/// a child process on a temp data folder and an ephemeral port while a client drives commits, saves, creates, deletes
/// and blob uploads, and is killed with SIGKILL at a seeded random point. After each kill it starts again on the same
/// folder, and every profile's saved document is the last one saved or the save in flight, its current document that or
/// one of the generations acknowledged since (unsaved changes kept across the restart, never older than the saved one),
/// and after a seeded share of kills that came once the client had paused for 3 s, exactly the last one acknowledged;
/// every blob a document refers to
/// is stored, every acknowledged upload still within its 24-hour grace reads back byte for byte under its SHA-256, no
/// deleted profile comes back, no temp or deleted entry is listed or left behind, and the engine serves. The client may
/// see a broken connection only once the kill has started; a transport error before it, or a client that stops before
/// it, fails. Each kill is attributed to the request in flight at that moment (a write, a read, or none), and enough of
/// them must land on a write. Opt-in with <c>INFOPANEL_CRASH_TESTS=1</c> (Unix only; <c>INFOPANEL_CRASH_KILLS</c>,
/// default 50, and <c>INFOPANEL_CRASH_SEED</c> override the loop); skipped otherwise.
/// </summary>
public sealed partial class CrashKillTests
{
    private const string Token = "crash-test-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_CRASH_TESTS") == "1";

    /// <summary>
    /// The share of kills that must land while a write request (commit, save, blob upload, create or delete) is in
    /// flight. Over 400 kills (seeds 1 to 8, 50 each) 385 did (96%; 44 to 50 per run): writes go to disk and take most of
    /// the client's time, the reads before them and the client's own work little. At the observed rate a run of 50 kills
    /// falls under three quarters with a probability of about 3 in 100 million (binomial), and still about 1 in 1,000 at
    /// 90%, while a loop whose kills mostly land between writes fails it. Checked on runs of at least
    /// <see cref="MinKillsForShare"/> kills; a shorter run is too small a sample.
    /// </summary>
    private const double MinWriteShare = 0.75;

    private const int MinKillsForShare = 20;

    /// <summary>How long after its last acknowledged upload a blob is kept even when no document refers to it (api.md, "Blobs").</summary>
    private static readonly TimeSpan BlobGrace = TimeSpan.FromHours(24);

    private static readonly string[] Writes = ["commit", "save", "blob", "create", "delete"];

    /// <summary>
    /// How long the client pauses before a paused kill: past the engine's 2 s after the last change at which it keeps
    /// unsaved changes (docs/2.0/data-folder.md, "Unsaved changes"), with room for the write.
    /// </summary>
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task AnEngineKilledAtRandom_RestartsWithOnlySavedGenerations()
    {
        Assert.SkipUnless(Enabled, "Kills engine processes: set INFOPANEL_CRASH_TESTS=1 on macOS or Linux to run it.");
        var kills = int.TryParse(Environment.GetEnvironmentVariable("INFOPANEL_CRASH_KILLS"), out var k) ? k : 50;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("INFOPANEL_CRASH_SEED"), out var s) ? s : Random.Shared.Next();
        Log($"Seed {seed} (INFOPANEL_CRASH_SEED={seed} repeats it), {kills} kills");
        var rng = new Random(seed);
        var root = Path.Combine(Path.GetTempPath(), "infopanel-crash-tests-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var model = new Model();
        var tally = new Dictionary<string, int>();
        var leftovers = 0;
        var paused = 0;
        var afterPause = false;

        try
        {
            for (var cycle = 0; cycle <= kills; cycle++)
            {
                var found = Leftovers(data);
                leftovers += found.Count > 0 ? 1 : 0;
                await using var engine = await Engine.Start(data, $"seed {seed}, cycle {cycle}");
                using var api = engine.Client();
                await Verify(engine, api, data, model, $"seed {seed}, after kill {cycle}", afterPause);
                if (cycle == kills)
                    break;

                // Drive until killed: the kill comes after a seeded delay, wherever the client is then. The driver must
                // still be running when the delay is up (it stops only on a transport error, which before the kill is a
                // failure), and the kill is attributed to the request in flight at that moment.
                var delay = TimeSpan.FromMilliseconds(rng.Next(10, 400));
                afterPause = rng.Next(5) == 0;
                var run = new Run();
                var driver = Drive(api, model, run, new Random(rng.Next()), Ct);
                if (await Task.WhenAny(driver, Task.Delay(delay, Ct)) == driver)
                {
                    await driver;
                    Assert.Fail($"seed {seed}, cycle {cycle}: the client stopped before the kill");
                }

                if (afterPause)
                {
                    // The client finishes what it sent and stops; the engine keeps its unsaved changes; then the kill.
                    run.Quiesce();
                    await driver;
                    await Task.Delay(Pause, Ct);
                    run.Kill(engine);
                    paused++;
                    continue;
                }

                var inFlight = run.Kill(engine);
                await driver;
                tally[inFlight] = tally.GetValueOrDefault(inFlight) + 1;
            }
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Seed {seed}: {ex.Message}", ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var writes = tally.Where(t => Writes.Contains(t.Key)).Sum(t => t.Value);
        var unpaused = kills - paused;
        Log($"{kills} kills, {paused} after a pause; in flight at the others: {string.Join(", ", tally.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} {t.Value}"))} " +
            $"({writes} with a write in flight); {leftovers} restarts found temp or deleted entries to clean up; " +
            $"{model.Saves} saves, {model.Creates} creates, {model.Deletes} deletes, {model.Uploads} uploads acknowledged ({model.Blobs.Count} blobs)");
        Assert.True(unpaused < MinKillsForShare || writes >= MinWriteShare * unpaused, $"Seed {seed}: only {writes} of {unpaused} kills without a pause landed with a write request in flight");
    }

    /// <summary>
    /// The client's place, shared with the killer: the request it has in flight (sent, its answer not yet read), and
    /// whether the kill has started. The kill freezes the place while the process dies, so it is attributed exactly;
    /// nothing is sent once it has started.
    /// </summary>
    private sealed class Run
    {
        private readonly object _gate = new();
        private string _phase = "none";
        private bool _killing;
        private bool _quiescing;

        /// <summary>The client is to stop after what it has sent (a paused kill follows).</summary>
        public bool Quiescing
        {
            get { lock (_gate) return _quiescing; }
        }

        public void Quiesce()
        {
            lock (_gate)
                _quiescing = true;
        }

        /// <summary>Sends a request as <paramref name="phase"/> (a write's kind, or <c>read</c>) and reads its answer.</summary>
        /// <exception cref="Killed">The kill had started before it could be sent, or broke it.</exception>
        public async Task<T> Send<T>(string phase, Func<Task<T>> request)
        {
            lock (_gate)
            {
                if (_killing)
                    throw new Killed();
                _phase = phase;
            }

            try
            {
                return await request();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lock (_gate)
                {
                    if (!_killing)
                        throw new Xunit.Sdk.XunitException($"A {phase} request failed before the kill: {ex}");
                }

                throw new Killed();
            }
            finally
            {
                lock (_gate)
                {
                    if (!_killing)
                        _phase = "none";
                }
            }
        }

        /// <summary>Kills the engine; returns what was in flight then.</summary>
        public string Kill(Engine engine)
        {
            lock (_gate)
            {
                _killing = true;
                engine.Kill();
                return _phase;
            }
        }

        public sealed class Killed : Exception;
    }

    /// <summary>What the client knows was stored: per profile, the bytes of its last acknowledged save and of any in flight.</summary>
    private sealed class Model
    {
        public Dictionary<string, Profile> Profiles { get; } = [];

        /// <summary>Profiles whose delete was acknowledged.</summary>
        public HashSet<string> Deleted { get; } = [];

        /// <summary>A create whose answer never came: its id and the document it would have stored.</summary>
        public (string Id, byte[] Bytes)? CreateInFlight { get; set; }

        public string? DeleteInFlight { get; set; }

        /// <summary>Blobs whose upload was acknowledged, by id, in the order first acknowledged; documents refer only to these.</summary>
        public List<string> StoredBlobs { get; } = [];

        /// <summary>Every acknowledged blob's bytes and when its upload was last acknowledged.</summary>
        public Dictionary<string, (byte[] Bytes, DateTimeOffset Acknowledged)> Blobs { get; } = [];

        /// <summary>The document every create sends, and its stored form under its own id (an export of it).</summary>
        public (JsonObject Document, string Id, byte[] Stored)? Template { get; set; }

        public int Saves { get; set; }

        public int Creates { get; set; }

        public int Deletes { get; set; }

        public int Uploads { get; set; }
    }

    private sealed class Profile
    {
        public required byte[] Saved { get; set; }

        public byte[]? SaveInFlight { get; set; }

        /// <summary>
        /// The current document after each commit acknowledged since the last acknowledged save (an export right after
        /// it), oldest first: what the engine may have kept as unsaved changes.
        /// </summary>
        public List<byte[]> Since { get; } = [];

        /// <summary>
        /// A commit was sent and its answer (and the export after it) not read yet: the document it makes, worked out
        /// before it was sent (an export of it once applied); null when none is in flight.
        /// </summary>
        public byte[]? CommitInFlight { get; set; }

        public long Rev { get; set; }

        /// <summary>The document's tag in the running engine, which its commits and its delete name.</summary>
        public string Tag { get; set; } = "";

        public int Inserted { get; set; }
    }

    /// <summary>The checks after a (re)start, then the model reset to what is stored now.</summary>
    private static async Task Verify(Engine engine, HttpClient api, string data, Model model, string context, bool afterPause)
    {
        // Start-up removed every temp and deleted entry the crash left.
        Assert.True(Leftovers(data) is { Count: 0 } left, $"{context}: left after start-up: {string.Join(", ", Leftovers(data))}");

        var listed = (await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).EnumerateArray().ToList();
        foreach (var entry in listed)
            Assert.True(entry.GetProperty("status").GetString() == "ok", $"{context}: {entry}");
        var ids = listed.Select(e => e.GetProperty("id").GetString()!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(UuidPattern(), id));

        if (model.Template == null)
        {
            // The first start: the example profile is the only one, saved.
            var only = Assert.Single(ids);
            model.Profiles[only] = new Profile { Saved = File.ReadAllBytes(SavedFile(data, only)) };
        }

        foreach (var id in model.Deleted)
            Assert.False(ids.Contains(id), $"{context}: the deleted profile {id} came back");
        foreach (var (id, _) in model.Profiles)
        {
            if (id != model.DeleteInFlight)
                Assert.True(ids.Contains(id), $"{context}: the profile {id} is gone without a delete");
        }

        foreach (var id in ids)
        {
            // The saved document on disk, and the current one (unsaved changes kept across the restart, or the saved one).
            var stored = File.ReadAllBytes(SavedFile(data, id));
            var current = await Export(api, id);
            var state = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
            if (model.Profiles.TryGetValue(id, out var profile))
            {
                var savedIsLast = stored.AsSpan().SequenceEqual(profile.Saved);
                var savedInFlight = profile.SaveInFlight != null && stored.AsSpan().SequenceEqual(profile.SaveInFlight);
                Assert.True(savedIsLast || savedInFlight,
                    $"{context}: {id} is neither its last saved document nor the one in flight:\n{Encoding.UTF8.GetString(stored)}");
                Assert.True(IsKnownCurrent(profile, stored, savedIsLast, current),
                    $"{context}: {id}'s current document is neither its saved one, a generation acknowledged since, nor the one its commit in flight makes:\n{Encoding.UTF8.GetString(current)}");
                if (afterPause)
                {
                    var last = profile.Since.Count > 0 ? profile.Since[^1] : profile.Saved;
                    Assert.True(current.AsSpan().SequenceEqual(last), $"{context}: after a pause, {id} isn't its last acknowledged generation:\n{Encoding.UTF8.GetString(current)}");
                }
                profile.Saved = stored;
                profile.SaveInFlight = null;
                profile.Since.Clear();
                if (!current.AsSpan().SequenceEqual(stored))
                    profile.Since.Add(current);
                profile.CommitInFlight = null;
            }
            else
            {
                Assert.True(model.CreateInFlight is { } create && create.Id == id, $"{context}: {id} was never created");
                Assert.True(stored.AsSpan().SequenceEqual(model.CreateInFlight!.Value.Bytes), $"{context}: {id} isn't the document its create sent:\n{Encoding.UTF8.GetString(stored)}");
                Assert.True(current.AsSpan().SequenceEqual(stored), $"{context}: {id}, created and never changed, has unsaved changes");
                model.Profiles[id] = new Profile { Saved = stored };
            }

            // Dirty exactly when the current document isn't the saved one, and then restored: revision 2, recovered.
            var dirty = !current.AsSpan().SequenceEqual(stored);
            Assert.Equal(dirty, state.GetProperty("dirty").GetBoolean());
            Assert.Equal(dirty, state.TryGetProperty("recovered", out _));
            Assert.Equal(dirty ? 2 : 1, state.GetProperty("rev").GetInt64());

            // Every blob it refers to, saved or unsaved, is one the client uploaded, stored on disk and through the API.
            foreach (Match blob in BlobPattern().Matches(Encoding.UTF8.GetString(stored) + Encoding.UTF8.GetString(current)))
            {
                Assert.True(model.Blobs.ContainsKey(blob.Value), $"{context}: {id} refers to {blob.Value}, which was never acknowledged");
                Assert.True(File.Exists(Path.Combine(data, "blobs", blob.Groups[1].Value)), $"{context}: {id} refers to {blob.Value}, which isn't stored");
                using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{blob.Value}");
                using var response = await api.SendAsync(head, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            model.Profiles[id].Rev = state.GetProperty("rev").GetInt64();
            model.Profiles[id].Tag = state.GetProperty("instance").GetString()!;
        }

        foreach (var id in model.Profiles.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            model.Profiles.Remove(id);
            model.Deleted.Add(id);
        }

        model.CreateInFlight = null;
        model.DeleteInFlight = null;

        // Every acknowledged upload still within its grace, referred to or not, reads back byte for byte.
        var now = DateTimeOffset.UtcNow;
        foreach (var (blob, (bytes, acknowledged)) in model.Blobs)
        {
            if (now - acknowledged >= BlobGrace - TimeSpan.FromMinutes(1))
                continue;
            using var response = await api.GetAsync($"/api/blobs/{blob}", Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{context}: the acknowledged blob {blob} answers {(int)response.StatusCode}");
            Assert.True((await response.Content.ReadAsByteArrayAsync(Ct)).AsSpan().SequenceEqual(bytes), $"{context}: the blob {blob} isn't the bytes uploaded");
        }

        // The engine serves: a thumbnail of each profile, drawn from its stored document.
        foreach (var id in ids)
        {
            using var thumbnail = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=64", Ct);
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        }

        if (model.Template == null)
            await MakeTemplate(api, model);
    }

    /// <summary>The document every create sends: a media item on a stored blob. Its stored form is learnt once, from an export.</summary>
    private static async Task MakeTemplate(HttpClient api, Model model)
    {
        var blob = await Upload(api, model, Png(new SKColor(0x20, 0x80, 0xc0)));
        var document = Doc("Crash", 320, 120, Scene("media", 10, 10, MediaScene(blob)), Scene("box", 100, 10));
        var id = NewId();
        string tag;
        using (var created = await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = document.DeepClone() }), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            tag = (await LiveEngine.Body(created, Ct)).GetProperty("instance").GetString()!;
        }
        var stored = await Export(api, id);
        Assert.Single(Occurrences(stored, Encoding.UTF8.GetBytes(id)));
        model.Template = (document, id, stored);
        model.Profiles[id] = new Profile { Saved = stored, Rev = 1, Tag = tag };
    }

    /// <summary>
    /// Runs seeded operations, one at a time, until the kill stops it. Each operation is entered in the model before its
    /// write is sent and settled when the answer comes; every request goes through <paramref name="run"/>, so a read that
    /// prepares a write (a save's or a commit's export) is a read, not the write.
    /// </summary>
    private static async Task Drive(HttpClient api, Model model, Run run, Random rng, CancellationToken ct)
    {
        try
        {
            while (!run.Quiescing)
            {
                var ids = model.Profiles.Keys.Order(StringComparer.Ordinal).ToList();
                var roll = rng.Next(100);
                var kind = roll switch
                {
                    < 35 => "commit",
                    < 60 => "save",
                    < 75 => "blob",
                    < 88 => ids.Count < 6 ? "create" : "delete",
                    _ => ids.Count > 1 ? "delete" : "create",
                };
                switch (kind)
                {
                    case "commit":
                        await Commit(api, model, run, ids[rng.Next(ids.Count)], rng, ct);
                        break;
                    case "save":
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var current = await run.Send("read", () => Export(api, id));
                        model.Profiles[id].SaveInFlight = current;
                        await run.Send(kind, async () =>
                        {
                            using var save = await api.PostAsync($"/api/profiles/{id}/save", null, ct);
                            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
                            return await save.Content.ReadAsByteArrayAsync(ct);
                        });
                        model.Profiles[id].Saved = current;
                        model.Profiles[id].SaveInFlight = null;
                        model.Profiles[id].Since.Clear();
                        model.Saves++;
                        break;
                    }
                    case "blob":
                    {
                        var png = Png(new SKColor((uint)rng.Next() | 0xff000000));
                        await run.Send(kind, () => Upload(api, model, png));
                        model.Uploads++;
                        break;
                    }
                    case "create":
                    {
                        var (document, templateId, stored) = model.Template!.Value;
                        var id = NewId();
                        model.CreateInFlight = (id, Replace(stored, templateId, id));
                        var entry = await run.Send(kind, async () =>
                        {
                            using var created = await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = document.DeepClone() }), ct);
                            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                            return await LiveEngine.Body(created, ct);
                        });
                        model.Profiles[id] = new Profile { Saved = model.CreateInFlight.Value.Bytes, Rev = 1, Tag = entry.GetProperty("instance").GetString()! };
                        model.CreateInFlight = null;
                        model.Creates++;
                        break;
                    }
                    case "delete":
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var tag = model.Profiles[id].Tag;
                        model.DeleteInFlight = id;
                        await run.Send(kind, async () =>
                        {
                            using var deleted = await api.DeleteAsync($"/api/profiles/{id}?instance={Uri.EscapeDataString(tag)}", ct);
                            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                            return deleted.StatusCode;
                        });
                        model.Profiles.Remove(id);
                        model.Deleted.Add(id);
                        model.DeleteInFlight = null;
                        model.Deletes++;
                        break;
                    }
                }
            }
        }
        catch (Run.Killed)
        {
        }
    }

    /// <summary>A commit: move an item, insert a media item on a stored blob, or remove an inserted one.</summary>
    private static async Task Commit(HttpClient api, Model model, Run run, string id, Random rng, CancellationToken ct)
    {
        var profile = model.Profiles[id];
        var document = JsonNode.Parse(await run.Send("read", () => Export(api, id)))!.AsObject();
        var items = document["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList();
        var inserted = items.Where(i => i.StartsWith("m-", StringComparison.Ordinal)).ToList();
        var roll = rng.Next(3);
        JsonObject op;
        if (roll == 0 && items.Count < 40)
        {
            var blob = model.StoredBlobs[rng.Next(model.StoredBlobs.Count)];
            op = Insert(Scene($"m-{++profile.Inserted}-{rng.Next(1_000_000)}", rng.Next(0, 300), rng.Next(0, 100), MediaScene(blob)));
        }
        else if (roll == 1 && inserted.Count > 0)
        {
            op = TestDocs.Remove(inserted[rng.Next(inserted.Count)]);
        }
        else if (items.Count > 0)
        {
            op = Place(items[rng.Next(items.Count)], rng.Next(0, 300), rng.Next(0, 100), profile.Rev);
        }
        else
        {
            op = Profile(new JsonObject { ["name"] = $"Renamed {rng.Next(1000)}" });
        }

        var bytes = LiveEngine.Commit(profile.Tag, profile.Rev, null, op);
        profile.CommitInFlight = Applied(document, op);
        var body = await run.Send("commit", async () =>
        {
            using var response = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, ct);
            var answer = await LiveEngine.Body(response, ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {answer}");
            return answer;
        });
        profile.Rev = body.GetProperty("rev").GetInt64();
        // The generation it made, as the engine would keep it (a read): the one worked out before it was sent.
        var made = await run.Send("read", () => Export(api, id));
        Assert.True(made.AsSpan().SequenceEqual(profile.CommitInFlight), $"The commit made another document than the one worked out for it:\n{Encoding.UTF8.GetString(made)}");
        profile.Since.Add(made);
        profile.CommitInFlight = null;
    }

    /// <summary>
    /// Whether <paramref name="current"/>, a profile's current document after a restart, is one the client may find: its
    /// saved document (<paramref name="stored"/>), or, when that is its last acknowledged save, a generation acknowledged
    /// since or the one its commit in flight makes (kept only once 2 s passed after it). Never older than the saved
    /// document: with the save in flight done, nothing kept before it applies.
    /// </summary>
    private static bool IsKnownCurrent(Profile profile, byte[] stored, bool savedIsLast, byte[] current)
    {
        var allowed = new List<byte[]> { stored };
        if (savedIsLast)
        {
            allowed.AddRange(profile.Since);
            if (profile.CommitInFlight is { } pending)
                allowed.Add(pending);
        }
        return allowed.Any(a => a.AsSpan().SequenceEqual(current));
    }

    /// <summary>
    /// The document <paramref name="op"/> makes of <paramref name="document"/> (an export), as stored: applied to an
    /// in-memory document opened from it, with its revisions as of that document.
    /// </summary>
    private static byte[] Applied(JsonObject document, JsonObject op)
    {
        using var json = JsonDocument.Parse(document.ToJsonString());
        var (opened, check) = ProfileDocument.Open(json.RootElement);
        Assert.True(opened is not null, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
        using var doc = opened;
        var local = op.DeepClone().AsObject();
        if (local.ContainsKey("baseRev"))
            local["baseRev"] = doc.Rev;
        var outcome = doc.Commit(LiveEngine.Commit(doc.Instance, doc.Rev, null, local));
        Assert.True(outcome.Result is not null, $"The commit doesn't apply to the document it was made for: {outcome.Status}");
        return Workspace.Serialize(doc.Current);
    }

    [Fact]
    public void TheOracle_AcceptsACommitInFlightOnlyAsTheDocumentItMakes()
    {
        var saved = "{\"saved\":1}"u8.ToArray();
        var acknowledged = "{\"acknowledged\":1}"u8.ToArray();
        var pending = "{\"pending\":1}"u8.ToArray();
        var other = "{\"other\":1}"u8.ToArray();
        var profile = new Profile { Saved = saved };
        profile.Since.Add(acknowledged);

        Assert.True(IsKnownCurrent(profile, saved, true, saved));
        Assert.True(IsKnownCurrent(profile, saved, true, acknowledged));
        Assert.False(IsKnownCurrent(profile, saved, true, pending));
        Assert.False(IsKnownCurrent(profile, saved, true, other));

        profile.CommitInFlight = pending;
        Assert.True(IsKnownCurrent(profile, saved, true, pending));
        Assert.False(IsKnownCurrent(profile, saved, true, other));
        // After the save in flight landed, nothing made before it applies.
        Assert.False(IsKnownCurrent(profile, saved, false, pending));
        Assert.False(IsKnownCurrent(profile, saved, false, acknowledged));
    }

    private static JsonObject MediaScene(string blob) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "media", ["w"] = 16, ["h"] = 16, ["source"] = new JsonObject { ["asset"] = blob } }),
    };

    /// <summary>
    /// Uploads a blob; once the engine acknowledges it, checks the id it answers against the SHA-256 of the bytes, and
    /// enters the bytes in the model as acknowledged now. Returns the id.
    /// </summary>
    private static async Task<string> Upload(HttpClient api, Model model, byte[] bytes)
    {
        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var response = await api.PostAsync("/api/blobs", new ByteArrayContent(bytes), Ct);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, response.StatusCode.ToString());
        var id = (await LiveEngine.Body(response, Ct)).GetProperty("id").GetString()!;
        Assert.Equal(expected, id);
        if (!model.Blobs.ContainsKey(id))
            model.StoredBlobs.Add(id);
        model.Blobs[id] = (bytes, DateTimeOffset.UtcNow);
        return id;
    }

    private static byte[] Png(SKColor color) => RetryTests.Png(color, 8);

    private static async Task<byte[]> Export(HttpClient api, string id)
    {
        using var response = await api.GetAsync($"/api/profiles/{id}/document", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>The temp files, staging folders and deleted folders the store writes, wherever a crash may leave them.</summary>
    private static List<string> Leftovers(string data)
    {
        var found = new List<string>();
        if (Directory.Exists(data))
            found.AddRange(Directory.EnumerateFiles(data, "profiles.json.*.tmp"));
        foreach (var folder in new[] { Path.Combine(data, "profiles"), Path.Combine(data, "blobs") })
        {
            if (Directory.Exists(folder))
                found.AddRange(Directory.EnumerateFileSystemEntries(folder).Where(p => p.EndsWith(".tmp", StringComparison.Ordinal) || p.Contains(".deleted-", StringComparison.Ordinal)));
        }

        var profiles = Path.Combine(data, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var folder in Directory.EnumerateDirectories(profiles))
                found.AddRange(Directory.EnumerateFiles(folder, "*.tmp"));
        }

        return found;
    }

    /// <summary>A profile's saved document on disk (read only by the test; a client reads the current one).</summary>
    private static string SavedFile(string data, string id) => Path.Combine(data, "profiles", id, "document.json");

    private static byte[] Replace(byte[] bytes, string from, string to)
    {
        var at = Assert.Single(Occurrences(bytes, Encoding.UTF8.GetBytes(from)));
        var copy = (byte[])bytes.Clone();
        Encoding.UTF8.GetBytes(to).CopyTo(copy, at);
        return copy;
    }

    private static List<int> Occurrences(byte[] bytes, byte[] value)
    {
        var found = new List<int>();
        for (var i = bytes.AsSpan().IndexOf(value); i >= 0;)
        {
            found.Add(i);
            var next = bytes.AsSpan(i + 1).IndexOf(value);
            i = next < 0 ? -1 : i + 1 + next;
        }

        return found;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("sha256:([0-9a-f]{64})")]
    private static partial Regex BlobPattern();

    [GeneratedRegex("address already in use", RegexOptions.IgnoreCase)]
    private static partial Regex AddressInUse();

    /// <summary>The built engine as a child process on <c>127.0.0.1</c> and an ephemeral port, in mock mode, without windows.</summary>
    private sealed class Engine : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private Engine(Process process, int port)
        {
            _process = process;
            Port = port;
        }

        public int Port { get; }

        /// <summary>
        /// Starts the engine and waits until it answers with this test's token. The port is free when chosen but released
        /// before the engine binds it: when another process took it first, start-up is retried on a fresh port.
        /// </summary>
        public static async Task<Engine> Start(string data, string context)
        {
            for (var attempt = 1; ; attempt++)
            {
                var engine = Launch(data, EngineRuntime.FindFreePort());
                if (await engine.Ready(context, attempt < LiveEngine.StartAttempts))
                    return engine;
                await engine.DisposeAsync();
            }
        }

        private static Engine Launch(string data, int port)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Its own 1.x folder, never created: a test engine never reads the 1.x data of the user running the tests.
            foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", data, "--v1-dir", data + "-v1", "--token", Token, "--no-browser", "--no-windows", "--mock-seed", "42" })
                start.ArgumentList.Add(arg);

            var engine = new Engine(Process.Start(start)!, port);
            engine._process.OutputDataReceived += (_, e) => engine.Append(e.Data);
            engine._process.ErrorDataReceived += (_, e) => engine.Append(e.Data);
            engine._process.BeginOutputReadLine();
            engine._process.BeginErrorReadLine();
            return engine;
        }

        /// <summary>
        /// Waits until the engine answers <c>/api/session</c> with this test's token (so not another server that took the
        /// port); false when it exited because the port was taken and <paramref name="retry"/> allows another port.
        /// </summary>
        private async Task<bool> Ready(string context, bool retry)
        {
            using var probe = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(2) };
            probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            while (true)
            {
                if (_process.HasExited)
                {
                    _process.WaitForExit();
                    if (retry && AddressInUse().IsMatch(Output))
                        return false;
                    Assert.Fail($"{context}: the engine exited with {_process.ExitCode} on start:\n{Output}");
                }

                try
                {
                    using var response = await probe.GetAsync("/api/session", Ct);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                }

                if (Stopwatch.GetTimestamp() > deadline)
                    Assert.Fail($"{context}: the engine didn't start within 30 s:\n{Output}");
                await Task.Delay(25, Ct);
            }
        }

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        public HttpClient Client()
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return client;
        }

        /// <summary>SIGKILL (<see cref="Process.Kill()"/> on Unix), then waits for the process to be gone.</summary>
        public void Kill()
        {
            _process.Kill();
            _process.WaitForExit();
        }

        private void Append(string? line)
        {
            lock (_output)
                _output.AppendLine(line);
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                Kill();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
