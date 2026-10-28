using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// Two tabs of a web client (the session token, each with its own <c>/ws</c> subscription and its own coalescing-key
/// prefix) and a script (an <c>edit</c> integration token) editing one document at once over REST on real Kestrel
/// (spec/docs/commit.md). Every answer is recorded and checked afterwards against <see cref="EditOracle"/>, which
/// applies the accepted changes in <c>rev</c> order by hand: revisions, the history requests' revision guard, conflicts,
/// what each change stamped (items, removed ids, the profile), the shared undo history with redo, revert and coalescing,
/// the answers that changed nothing, the final document, and both sockets' last state and frame.
/// </summary>
public sealed class ConcurrentEditingTests
{
    private const int Seed = 20261027;
    private const int TabRounds = 40;
    private const int ScriptRounds = 20;
    private const string SensorKey = "plugin:/infopanel.mock/cpu/cpu-load";

    /// <summary>The group the tabs move items into and out of.</summary>
    private const string Folder = "g";

    /// <summary>The items both tabs edit: <c>a</c>–<c>d</c> at the top level, <c>e</c> in <see cref="Folder"/>.</summary>
    private static readonly string[] Shared = ["a", "b", "c", "d", "e"];

    /// <summary>A tab's actions: each tab takes every one in its first rounds, in a seeded order, then picks them by these weights.</summary>
    private static readonly string[] TabActions = ["drag", "restyle", "stale", "move", "profile", "noop", "undo", "redo", "revert"];

    private static readonly string[] Weighted = ["drag", "drag", "drag", "drag", "restyle", "restyle", "stale", "move", "move", "profile", "noop", "undo", "undo", "redo", "revert"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One request and its answer, as a client saw it; <paramref name="SentAt"/> is when it was sent (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    private sealed record Sent(string Actor, string Kind, JsonArray? Ops, string? Key, long BaseRev, HttpStatusCode Status, JsonElement Body, string Purpose, long SentAt);

    [Fact]
    public async Task TwoTabsAndAScript_EditOneDocumentAtOnce_AndEveryAnswerAgreesWithTheRevisionOrder()
    {
        using var engine = new EngineFactory();
        using var live = LiveEngine.Start(engine);
        using var session = live.Client();
        var start = Shared[..4].Select((s, i) => Scene(s, 20 + 100 * i, 20)).Append(Group(Folder, Scene("e", 10, 120))).ToArray();
        var id = await CreateAsync(session, Doc("Concurrent", 480, 320, start), Ct);
        var instance = await LiveEngine.Instance(session, Ct);
        using var script = live.Client(await LiveEngine.CreateToken(session, "concurrent-script", Ct, "edit"));
        var first = await session.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var oracle = new EditOracle(await Export(session, id), first);
        var initial = oracle.Rev;
        TestContext.Current.TestOutputHelper?.WriteLine($"Seed {Seed}");

        await using var socketA = await live.Subscribe(id, Ct);
        await using var socketB = await live.Subscribe(id, Ct);
        using var tabA = live.Client();
        using var tabB = live.Client();
        var sent = new ConcurrentQueue<Sent>();
        var staleRestyles = new ConcurrentQueue<Sent>();

        // Before anyone edits: an undo, a redo and a revert of a clean document with no history pass the guard and take no step.
        foreach (var kind in new[] { "undo", "redo", "revert" })
            Assert.Equal(HttpStatusCode.OK, (await History("tab-a", tabA, kind, initial, "nothing")).Status);

        await Task.WhenAll(
            Task.Run(() => Tab("tab-a", tabA, socketA, new Random(Seed + 1), certainRevert: true), Ct),
            Task.Run(() => Tab("tab-b", tabB, socketB, new Random(Seed + 2)), Ct),
            Task.Run(() => Script(new Random(Seed + 3)), Ct));

        // ---- the answers, replayed in revision order -------------------------------------------------------------------
        var changes = sent.Where(IsChange).OrderBy(s => s.Body.GetProperty("rev").GetInt64()).ToList();
        var revs = changes.Select(s => s.Body.GetProperty("rev").GetInt64()).ToList();
        Assert.Equal(Enumerable.Range(1, changes.Count).Select(i => initial + i), revs);
        var others = sent.Where(s => !IsChange(s)).ToLookup(s => s.Body.GetProperty("rev").GetInt64());
        Assert.All(others, g => Assert.InRange(g.Key, initial, initial + changes.Count));

        var count = new Dictionary<string, int>();
        void Count(string what) => count[what] = count.GetValueOrDefault(what) + 1;
        for (var rev = initial; rev <= initial + changes.Count; rev++)
        {
            if (rev > initial)
            {
                var change = changes[(int)(rev - initial - 1)];
                EditOracle.Change stamped;
                if (change.Kind == "commit")
                {
                    // What the ops do to the tree, read before they apply: the parents they move from, the groups they remove.
                    var moves = change.Ops!.Where(o => o!["op"]!.GetValue<string>() == "move")
                        .Select(o => (Id: o!["id"]!.GetValue<string>(), From: oracle.TryParent(o["id"]!.GetValue<string>(), out var p) ? p : null)).ToList();
                    var groupRemovals = change.Ops!.Count(o => o!["op"]!.GetValue<string>() == "remove" && oracle.Subtree(o["id"]!.GetValue<string>()).Count > 1);

                    var (expected, after) = oracle.Evaluate(change.Ops!);
                    Assert.True(expected.Count == 0, $"rev {rev}: the engine accepted a commit that conflicts: {string.Join("; ", expected)}");
                    Assert.False(EditOracle.Equal(after, oracle.Document), $"rev {rev}: a commit that changed nothing made a revision");
                    stamped = oracle.Commit(after, change.Key, rev, change.Actor);
                    if (stamped.Merged) Count("merged drag commit");
                    if (stamped.Profile) Count("profile change");
                    foreach (var (moved, from) in moves)
                    {
                        Count("move");
                        if (oracle.TryParent(moved, out var to) && to != from)
                            Count("reparenting");
                    }
                    for (var i = 0; i < groupRemovals; i++)
                        Count("group removal");
                }
                else
                {
                    // An undo, redo or revert acts only on the revision it was based on: the one just before its own.
                    Assert.True(change.BaseRev == rev - 1, $"rev {rev}: {change.Actor}'s {change.Kind} based on {change.BaseRev} took a step");
                    stamped = change.Kind switch
                    {
                        "undo" => oracle.Undo(rev),
                        "redo" => oracle.Redo(rev),
                        "revert" => oracle.Revert(rev),
                        _ => throw new NotSupportedException(change.Kind),
                    } ?? throw new Xunit.Sdk.XunitException($"rev {rev}: {change.Actor}'s {change.Kind} took a step with none to take");
                    Count(change.Kind);
                    if (change.Kind == "undo" && stamped.UndoneActor != change.Actor)
                        Count("undo of another writer's step");
                }

                AssertResult(change, stamped, oracle);
            }

            foreach (var other in others[rev])
            {
                if (other.Status == HttpStatusCode.Conflict)
                {
                    Count("conflict");
                    Assert.Equal("conflict", other.Body.GetProperty("code").GetString());
                    List<string> expected;
                    if (other.Kind == "commit")
                    {
                        expected = oracle.Evaluate(other.Ops!).Conflicts;
                        Assert.NotEmpty(expected);
                    }
                    else
                    {
                        // A history request is refused only when the document had moved past its baseRev.
                        Assert.True(other.BaseRev != rev, $"{other.Actor}'s {other.Kind} on {other.BaseRev} was refused at that same revision");
                        expected = oracle.HistoryConflicts(other.BaseRev);
                    }

                    Assert.True(expected.SequenceEqual(EditOracle.Conflicts(other.Body)),
                        $"{other.Actor} {other.Kind} on {other.BaseRev} at rev {rev}: expected [{string.Join("; ", expected)}], got [{string.Join("; ", EditOracle.Conflicts(other.Body))}]");
                }
                else if (other.Kind == "commit")
                {
                    // A commit that changed nothing: no revision, nothing stamped, the state as it was.
                    var (expected, after) = oracle.Evaluate(other.Ops!);
                    Assert.Empty(expected);
                    Assert.True(EditOracle.Equal(after, oracle.Document), $"{other.Actor}'s commit at rev {rev} changed the document but made no revision");
                    AssertResult(other, EditOracle.Change.None, oracle);
                    Count(other.Purpose == "noop" ? "deliberate no-op commit" : "commit that changed nothing");
                }
                else
                {
                    // An undo, redo or revert at the current revision with no step to take: nothing changes, and the merge ends.
                    Assert.True(other.BaseRev == rev, $"{other.Actor}'s {other.Kind} on {other.BaseRev} answered 200 at rev {rev}");
                    var state = oracle.State;
                    Assert.False(other.Kind switch { "undo" => state.CanUndo, "redo" => state.CanRedo, _ => state.Dirty },
                        $"{other.Actor}'s {other.Kind} at rev {rev} took no step though there was one");
                    oracle.NoStep();
                    AssertResult(other, EditOracle.Change.None, oracle);
                    Count("history request with no step");
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{sent.Count} requests, {changes.Count} revisions; {string.Join(", ", count.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"))}");

        // Every kind of change and answer occurred.
        foreach (var what in new[] { "merged drag commit", "profile change", "move", "reparenting", "group removal", "undo", "redo", "revert",
                     "conflict", "deliberate no-op commit", "history request with no step" })
            Assert.True(count.GetValueOrDefault(what) > 0, $"No {what} in this run");

        // Each stale restyle met its conflict.
        Assert.NotEmpty(staleRestyles);
        Assert.All(staleRestyles, s => Assert.Equal(HttpStatusCode.Conflict, s.Status));

        // ---- the final document: the accepted changes applied in rev order ------------------------------------------------
        var final = await session.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        Assert.Equal(oracle.State, DocState.From(final));
        Assert.True(EditOracle.Equal(oracle.Document, await Export(session, id)), "The final document differs from the accepted changes applied in rev order");
        var finalItems = Items(final.GetProperty("document").GetProperty("items")).ToList();
        Assert.Equal(oracle.ItemIds, finalItems.Select(i => i.GetProperty("id").GetString()!));
        foreach (var item in finalItems)
            Assert.Equal(oracle.ItemRev(item.GetProperty("id").GetString()!), item.GetProperty("rev").GetInt64());
        Assert.Equal(oracle.ProfileRev, final.GetProperty("document").GetProperty("profile").GetProperty("rev").GetInt64());

        // ---- both sockets: on the latest state, with a frame of exactly the latest rev that came after the request that
        // made it; no revision ahead of the document's, and revisions never go back ------------------------------------------
        var lastSent = changes.Count > 0 ? changes[^1].SentAt : 0;
        foreach (var socket in new[] { socketA, socketB })
        {
            await socket.FirstFrameAt(oracle.Rev, lastSent, TimeSpan.FromSeconds(10), Ct);
            await WaitFor(socket, s => s.Docs.Count > 0 && s.Docs[^1] == oracle.State, "the latest doc state");
            Assert.Empty(socket.Errors);
            Assert.True(socket.FrameRevs.Max() == oracle.Rev, $"a frame labelled rev {socket.FrameRevs.Max()}, after the last change at {oracle.Rev}");
            Assert.True(socket.Docs.Max(d => d.Rev) == oracle.Rev, $"a doc state of rev {socket.Docs.Max(d => d.Rev)}, after the last change at {oracle.Rev}");
            Assert.True(IsSorted(socket.Docs.Select(d => d.Rev)), "doc revisions went back");
            Assert.True(IsSorted(socket.FrameRevs), "frame revisions went back");
        }

        return;

        async Task Tab(string name, HttpClient api, LiveSocket socket, Random rng, bool certainRevert = false)
        {
            var prefix = Guid.NewGuid().ToString();
            long known = initial;
            long Known() => Math.Max(known, socket.LastDocRev);
            void Read(long rev) => known = rev;
            var gesture = 0;
            var designedAt = new Dictionary<string, long>();
            var offset = rng.Next(TabActions.Length);

            async Task Restyle(int round)
            {
                var item = Shared[rng.Next(Shared.Length)];
                var result = await Commit(name, api, null, [Design(item, Rect($"#{(rng.Next(1 << 24) & 0xfffff0) | round % 16:x6}", 40, 30), Known())], Read, purpose: "restyle");
                if (IsChange(result))
                    designedAt[item] = result.Body.GetProperty("rev").GetInt64();
            }

            for (var round = 0; round < TabRounds; round++)
            {
                switch (round < TabActions.Length ? TabActions[(round + offset) % TabActions.Length] : Weighted[rng.Next(Weighted.Length)])
                {
                    case "drag":
                    {
                        // A drag: a few placements of one item under one gesture.
                        var item = Shared[rng.Next(Shared.Length)];
                        var key = $"gesture:{prefix}:{++gesture}";
                        for (int step = 0, steps = rng.Next(2, 5); step < steps; step++)
                            await Commit(name, api, key, [Place(item, rng.Next(0, 400), rng.Next(0, 300), Known())], Read, purpose: "drag");
                        break;
                    }
                    case "restyle":
                        // A restyle, based on what the tab last read.
                        await Restyle(round);
                        break;
                    case "stale":
                    {
                        // A restyle based on the revision before this tab's own last restyle of the item: always a conflict.
                        while (designedAt.Count == 0)
                            await Restyle(round);
                        var item = designedAt.Keys.Order(StringComparer.Ordinal).ElementAt(rng.Next(designedAt.Count));
                        staleRestyles.Enqueue(await Commit(name, api, null, [Design(item, Rect("#123456", 40, 30), designedAt[item] - 1)], Read, purpose: "stale"));
                        break;
                    }
                    case "move":
                    {
                        // A move within a list or into another one (the group, or the top level), sometimes placed anew;
                        // now and then the group itself, among the top-level items. Read again and retried when refused.
                        var item = rng.Next(6) == 0 ? Folder : Shared[rng.Next(Shared.Length)];
                        string? parent = item == Folder || rng.Next(2) == 0 ? null : Folder;
                        var index = rng.Next(0, 7);
                        var placed = rng.Next(3) == 0;
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            var op = new JsonObject { ["op"] = "move", ["id"] = item, ["index"] = index, ["baseRev"] = Known() };
                            if (parent != null)
                                op["parentId"] = parent;
                            if (placed)
                                op["placement"] = new JsonObject { ["x"] = rng.Next(0, 200), ["y"] = rng.Next(0, 200) };
                            if ((await Commit(name, api, null, [op], Read, purpose: "move")).Status == HttpStatusCode.OK)
                                break;
                        }
                        break;
                    }
                    case "profile":
                    {
                        // A profile change: the name or the guides, based on what the tab last read.
                        JsonObject set = rng.Next(2) == 0
                            ? new JsonObject { ["name"] = $"Concurrent {name} {round}" }
                            : new JsonObject { ["guides"] = new JsonArray(new JsonObject { ["axis"] = rng.Next(2) == 0 ? "x" : "y", ["at"] = rng.Next(0, 300) }) };
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            var op = Profile((JsonObject)set.DeepClone());
                            op["baseRev"] = Known();
                            if ((await Commit(name, api, null, [op], Read, purpose: "profile")).Status == HttpStatusCode.OK)
                                break;
                        }
                        break;
                    }
                    case "noop":
                    {
                        // A deliberate no-op: an item's placement, or the profile's name, written back as just read, based
                        // on that read. Either it changes nothing or the value changed after the read and it conflicts.
                        for (var attempt = 0; attempt < 5; attempt++)
                        {
                            var read = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
                            var rev = read.GetProperty("rev").GetInt64();
                            var document = read.GetProperty("document");
                            JsonObject op;
                            if (rng.Next(2) == 0)
                            {
                                var item = Shared[rng.Next(Shared.Length)];
                                var placement = Items(document.GetProperty("items")).Single(i => i.GetProperty("id").GetString() == item).GetProperty("placement");
                                op = new JsonObject { ["op"] = "setPlacement", ["id"] = item, ["baseRev"] = rev, ["placement"] = JsonNode.Parse(placement.GetRawText()) };
                            }
                            else
                            {
                                op = Profile(new JsonObject { ["name"] = document.GetProperty("profile").GetProperty("name").GetString() });
                                op["baseRev"] = rev;
                            }

                            if ((await Commit(name, api, null, [op], Read, () => rev, "noop")).Status == HttpStatusCode.OK)
                                break;
                        }
                        break;
                    }
                    case "undo":
                        await Undo();
                        break;
                    case "redo":
                    {
                        // Undo, then redo at the revision the undo answered; when another change came between them, the
                        // redo is refused or finds nothing to redo, and the tab tries the pair again.
                        for (var attempt = 0; attempt < 5; attempt++)
                        {
                            if (await Undo() is not { } undone)
                                continue;
                            var redo = await History(name, api, "redo", undone, "redo");
                            if (redo.Status == HttpStatusCode.OK)
                            {
                                known = redo.Body.GetProperty("rev").GetInt64();
                                if (known != undone)
                                    break;
                            }
                            else
                            {
                                known = await RevAsync(api, id, Ct);
                            }
                        }
                        break;
                    }
                    case "revert" when round < TabActions.Length && certainRevert:
                    {
                        // The first cycle's revert, certain to take a step: a profile name the saved document doesn't have,
                        // then a revert at the revision that answered (the document is dirty there, so an accepted revert
                        // is a step). Anything that came between them refuses the revert, and the pair is tried again until
                        // the revert lands. Every other writer does a finite amount of work, so the loop ends under any
                        // schedule; a refusal at the revert's own revision is an engine bug and fails at once, and a deadline
                        // fails the test (instead of hanging it) should the loop never land.
                        var deadline = Stopwatch.StartNew();
                        for (var attempt = 0; ; attempt++)
                        {
                            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60),
                                $"{name}'s certain revert did not land in 60 s, after {attempt} attempts");
                            var op = Profile(new JsonObject { ["name"] = $"Concurrent {name} revert {round}.{attempt}" });
                            op["baseRev"] = Known();
                            var dirtied = await Commit(name, api, null, [op], Read, purpose: "revert-base");
                            if (dirtied.Status != HttpStatusCode.OK)
                                continue;
                            var result = await History(name, api, "revert", dirtied.Body.GetProperty("rev").GetInt64(), "revert");
                            if (result.Status == HttpStatusCode.OK)
                            {
                                known = result.Body.GetProperty("rev").GetInt64();
                                break;
                            }

                            var current = result.Body.GetProperty("rev").GetInt64();
                            Assert.True(current != result.BaseRev,
                                $"{name}'s revert on {result.BaseRev} was refused at that same revision (409 reports rev {current}, attempt {attempt}): the engine must take a step on a dirty document at the current revision");
                            known = await RevAsync(api, id, Ct);
                        }
                        break;
                    }
                    case "revert":
                    {
                        // Revert to the saved document: refused when the tab's revision is stale; it reads and tries again.
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            var result = await History(name, api, "revert", Known(), "revert");
                            if (result.Status == HttpStatusCode.OK)
                            {
                                known = result.Body.GetProperty("rev").GetInt64();
                                break;
                            }

                            known = await RevAsync(api, id, Ct);
                        }
                        break;
                    }
                }
            }

            // Undo: refused when the tab's revision is stale; then it reads the state and decides again, up to twice.
            // Returns the revision the undo answered, or null when every attempt was refused.
            async Task<long?> Undo()
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var result = await History(name, api, "undo", Known(), "undo");
                    if (result.Status == HttpStatusCode.OK)
                    {
                        known = result.Body.GetProperty("rev").GetInt64();
                        return known;
                    }

                    known = await RevAsync(api, id, Ct);
                }

                return null;
            }
        }

        async Task Script(Random rng)
        {
            long known = initial;
            void Read(long rev) => known = rev;
            for (var round = 0; round < ScriptRounds; round++)
            {
                // A scene item, at the top level or at some index in the group: placed, bound, then removed.
                var item = $"s{round}";
                var insert = Insert(Scene(item, rng.Next(0, 400), rng.Next(0, 300), InputScene()));
                if (rng.Next(2) == 0)
                {
                    insert["parentId"] = Folder;
                    insert["index"] = rng.Next(0, 4);
                }

                await Commit("script", script, null, [insert], Read, () => known, "insert");
                await Commit("script", script, null,
                [
                    Place(item, rng.Next(0, 400), rng.Next(0, 300), known),
                    new JsonObject { ["op"] = "setBindings", ["id"] = item, ["baseRev"] = known, ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = SensorKey } } },
                ], Read, () => known, "bind");
                await Commit("script", script, null, [Remove(item, known)], Read, () => known, "remove");

                if (round % 3 != 0)
                    continue;

                // A group with two items: one moved to the front of the group, then the group removed with everything in
                // it; a refused remove reads again and retries while the group is there.
                var group = $"sg{round}";
                var add = Insert(Group(group, Scene($"{group}-0", 5, 5), Scene($"{group}-1", 30, 5)));
                add["index"] = rng.Next(0, 6);
                await Commit("script", script, null, [add], Read, () => known, "insert-group");
                await Commit("script", script, null,
                    [new JsonObject { ["op"] = "move", ["id"] = $"{group}-1", ["parentId"] = group, ["index"] = 0, ["baseRev"] = known }], Read, () => known, "move");
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    if ((await Commit("script", script, null, [Remove(group, known)], Read, () => known, "remove-group")).Status == HttpStatusCode.OK)
                        break;
                    var items = Items((await script.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct)).GetProperty("document").GetProperty("items"));
                    if (!items.Any(i => i.GetProperty("id").GetString() == group))
                        break;
                }
            }
        }

        // Commits ops based on baseRev (the commit's own; each op carries its own), records the answer, and on a 409
        // reads the document's revision as a client does before deciding again.
        async Task<Sent> Commit(string actor, HttpClient api, string? key, JsonObject[] ops, Action<long> read, Func<long>? baseRev = null, string purpose = "")
        {
            var commitBase = baseRev?.Invoke() ?? ops.Select(o => o["baseRev"]?.GetValue<long>() ?? 0).Max();
            var bytes = LiveEngine.Commit(instance, commitBase, key, ops);
            var at = Stopwatch.GetTimestamp();
            using var response = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct);
            var body = await LiveEngine.Body(response, Ct);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{actor}: {(int)response.StatusCode} {body}");
            var record = new Sent(actor, "commit", JsonNode.Parse(bytes)!["ops"]!.AsArray(), key, commitBase, response.StatusCode, body, purpose, at);
            sent.Enqueue(record);
            if (response.StatusCode == HttpStatusCode.OK)
                read(body.GetProperty("rev").GetInt64());
            else
                read(await RevAsync(api, id, Ct));
            return record;
        }

        // An undo, redo or revert based on baseRev; records the answer (200 or 409).
        async Task<Sent> History(string actor, HttpClient api, string kind, long baseRev, string purpose)
        {
            var at = Stopwatch.GetTimestamp();
            using var response = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/{kind}", LiveEngine.History(instance, baseRev), Ct);
            var body = await LiveEngine.Body(response, Ct);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{actor} {kind}: {(int)response.StatusCode} {body}");
            var record = new Sent(actor, kind, null, null, baseRev, response.StatusCode, body, purpose, at);
            sent.Enqueue(record);
            return record;
        }
    }

    /// <summary>
    /// Two undos (then two redos) sent at once from the same revision: the guard and the step are one, so exactly one
    /// takes a step and the other is refused at the revision the first made, however the two interleave.
    /// </summary>
    [Fact]
    public async Task TwoUndosFromOneRevision_OnlyOneTakesAStep()
    {
        const int pairs = 12;
        using var engine = new EngineFactory();
        using var live = LiveEngine.Start(engine);
        using var session = live.Client();
        var id = await CreateAsync(session, Doc("Pairs", 480, 320, Scene("a", 0, 0)), Ct);
        var instance = await LiveEngine.Instance(session, Ct);
        var rev = await RevAsync(session, id, Ct);
        for (var i = 1; i <= pairs; i++)
        {
            using var response = await LiveEngine.PostBytes(session, $"/api/profiles/{id}/commit", LiveEngine.Commit(instance, rev, null, Place("a", 10 * i, 0, rev)), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            rev = (await LiveEngine.Body(response, Ct)).GetProperty("rev").GetInt64();
        }

        using var tabA = live.Client();
        using var tabB = live.Client();
        // Open both connections first, so the two requests leave together.
        await LiveEngine.Instance(tabA, Ct);
        await LiveEngine.Instance(tabB, Ct);

        foreach (var (kind, round) in Enumerable.Range(1, pairs).Select(r => ("undo", r)).Concat(Enumerable.Range(1, pairs).Select(r => ("redo", r))))
        {
            var bytes = LiveEngine.History(instance, rev);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient tab)
            {
                await go.Task;
                using var response = await LiveEngine.PostBytes(tab, $"/api/profiles/{id}/{kind}", bytes, Ct);
                return (response.StatusCode, await LiveEngine.Body(response, Ct));
            }

            var both = new[] { Task.Run(() => Send(tabA), Ct), Task.Run(() => Send(tabB), Ct) };
            go.SetResult();
            var answers = await Task.WhenAll(both);
            var took = Assert.Single(answers, a => a.Status == HttpStatusCode.OK);
            var refused = Assert.Single(answers, a => a.Status == HttpStatusCode.Conflict);
            Assert.Equal(rev + 1, took.Body.GetProperty("rev").GetInt64());
            Assert.Equal(rev + 1, refused.Body.GetProperty("rev").GetInt64());
            Assert.Equal([$"op=- id=a changed rev={rev + 1} fields=placement"], EditOracle.Conflicts(refused.Body));
            rev++;

            var state = await session.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
            Assert.Equal(rev, state.GetProperty("rev").GetInt64());
            var x = Items(state.GetProperty("document").GetProperty("items")).Single().GetProperty("placement").GetProperty("x").GetDouble();
            Assert.Equal(kind == "undo" ? 10 * (pairs - round) : 10 * round, x);
        }
    }

    /// <summary>
    /// Undo is the document's: it undoes the last step whoever made it. A tab's gesture merges its own commits only, so
    /// another tab's commit with the same gesture number, or between two of its commits, starts a step of its own.
    /// </summary>
    [Fact]
    public async Task Undo_UndoesTheLastStepWhoeverMadeIt_AndTwoTabsGesturesNeverMerge()
    {
        using var engine = new EngineFactory();
        using var live = LiveEngine.Start(engine);
        using var session = live.Client();
        var id = await CreateAsync(session, Doc("Undo", 480, 320, Scene("a", 0, 0)), Ct);
        var instance = await LiveEngine.Instance(session, Ct);
        using var script = live.Client(await LiveEngine.CreateToken(session, "undo-script", Ct, "edit"));
        using var tabA = live.Client();
        using var tabB = live.Client();
        string prefixA = Guid.NewGuid().ToString(), prefixB = Guid.NewGuid().ToString();
        long rev = 1;

        async Task<JsonElement> Post(HttpClient api, string path, byte[] body)
        {
            using var response = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/{path}", body, Ct);
            var result = await LiveEngine.Body(response, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {result}");
            rev = result.GetProperty("rev").GetInt64();
            return result;
        }

        Task<JsonElement> Drag(HttpClient tab, string prefix, double x) =>
            Post(tab, "commit", LiveEngine.Commit(instance, rev, $"gesture:{prefix}:7", Place("a", x, 0, rev)));

        async Task<(double X, string[] Ids)> Read()
        {
            var items = (await Export(session, id))["items"]!.AsArray();
            return (items[0]!["placement"]!["x"]!.GetValue<double>(), items.Select(i => i!["id"]!.GetValue<string>()).ToArray());
        }

        await Drag(tabA, prefixA, 10);
        await Drag(tabA, prefixA, 20); // the same gesture of the same tab: one step
        await Drag(tabB, prefixB, 30); // another tab, the same gesture number: its own step
        await Drag(tabA, prefixA, 40); // tab A again, after tab B's commit: a new step
        await Post(script, "commit", LiveEngine.Commit(instance, rev, null, Insert(Scene("s", 5, 5))));

        // Tab B's undo undoes the script's insert, the document's last step.
        var undo = await Post(tabB, "undo", LiveEngine.History(instance, rev));
        Assert.Equal(["s"], Removed(undo));
        var (x, ids) = await Read();
        Assert.Equal(40, x);
        Assert.Equal(["a"], ids);

        // Then each tab's steps, newest first, whichever tab asks.
        await Post(tabA, "undo", LiveEngine.History(instance, rev));
        Assert.Equal(30, (await Read()).X);
        await Post(tabA, "undo", LiveEngine.History(instance, rev));
        Assert.Equal(20, (await Read()).X);
        var last = await Post(tabB, "undo", LiveEngine.History(instance, rev));
        Assert.Equal(0, (await Read()).X);
        Assert.False(last.GetProperty("canUndo").GetBoolean());
        Assert.False(last.GetProperty("dirty").GetBoolean());
    }

    private static bool IsChange(Sent s) => s.Status == HttpStatusCode.OK && (s.Kind == "commit"
        ? s.Body.GetProperty("items").EnumerateObject().Any() || Removed(s.Body).Count > 0 || s.Body.TryGetProperty("profileRev", out _)
        : s.Body.GetProperty("rev").GetInt64() != s.BaseRev);

    /// <summary>
    /// An answer against what the oracle stamped (nothing, for one that changed nothing): its revision, its items and
    /// removed ids, its <c>profileRev</c>, and the state after it.
    /// </summary>
    private static void AssertResult(Sent change, EditOracle.Change stamped, EditOracle oracle)
    {
        var body = change.Body;
        var rev = body.GetProperty("rev").GetInt64();
        var what = $"rev {rev} ({change.Actor} {change.Kind} on {change.BaseRev})";
        Assert.True(rev == oracle.Rev, $"{what}: expected rev {oracle.Rev}");
        var items = body.GetProperty("items").EnumerateObject().ToList();
        Assert.True(stamped.Items.SetEquals(items.Select(i => i.Name)),
            $"{what}: items [{string.Join(",", items.Select(i => i.Name))}], expected [{string.Join(",", stamped.Items)}]");
        Assert.All(items, i => Assert.Equal(rev, i.Value.GetInt64()));
        Assert.Equal(stamped.Removed, Removed(body).Order(StringComparer.Ordinal));
        Assert.True(stamped.Profile == body.TryGetProperty("profileRev", out var profileRev), $"{what}: profileRev {(stamped.Profile ? "missing" : "set")}");
        if (stamped.Profile)
            Assert.Equal(rev, profileRev.GetInt64());
        Assert.False(body.TryGetProperty("replayed", out var replayed) && replayed.GetBoolean(), $"{what}: replayed");
        Assert.True(oracle.State == DocState.From(body), $"{what}: state {DocState.From(body)}, expected {oracle.State}");
    }

    /// <summary>A result's <c>removed</c> ids (absent when it removed none).</summary>
    private static List<string> Removed(JsonElement result) =>
        result.TryGetProperty("removed", out var removed) ? removed.EnumerateArray().Select(r => r.GetString()!).ToList() : [];

    /// <summary>Every item of a response's <c>items</c>, groups' children included, in document order.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            yield return item;
            if (item.TryGetProperty("children", out var children))
                foreach (var child in Items(children))
                    yield return child;
        }
    }

    private static async Task<JsonObject> Export(HttpClient api, string id) =>
        JsonNode.Parse(await api.GetStringAsync($"/api/profiles/{id}/document", Ct))!.AsObject();

    private static Task WaitFor(LiveSocket socket, Func<LiveSocket, bool> condition, string what) =>
        socket.Until(() => condition(socket), TimeSpan.FromSeconds(10), what, Ct);

    private static bool IsSorted(IEnumerable<long> values)
    {
        long previous = long.MinValue;
        foreach (var value in values)
        {
            if (value < previous)
                return false;
            previous = value;
        }

        return true;
    }

    private static JsonObject Remove(string id, long baseRev) => new() { ["op"] = "remove", ["id"] = id, ["baseRev"] = baseRev };
}
