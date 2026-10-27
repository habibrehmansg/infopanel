using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Documents;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Multi-op commits while the scene runtime loads each new generation and ticks on other threads: every snapshot shows
/// all of a commit or none of it, and the revision it reports is the one its bounds and pixels show.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class CommitWhileTickingTests
{
    private const int Items = 6, Commits = 150;

    /// <summary>The offset every item has at revision <paramref name="rev"/>: 2 as opened, then 0 and 5 by turns.</summary>
    private static int Offset(long rev) => rev == 1 ? 2 : (rev - 2) % 2 == 0 ? 0 : 5;

    [Fact]
    public void Every_snapshot_has_all_of_a_commit_or_none()
    {
        var items = Enumerable.Range(0, Items).Select(i => Scene($"i{i}", i * 30 + Offset(1), 10)).ToArray();
        using var doc = Open(Doc(items));
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(doc.Current);

        var done = 0;
        var failures = new List<string>();
        var revs = new HashSet<long>();
        var loader = new Thread(() =>
        {
            long loaded = 1;
            while (Volatile.Read(ref done) == 0 || loaded != doc.Rev)
            {
                var current = doc.Current;
                if (current.Rev == loaded)
                {
                    Thread.Yield();
                    continue;
                }
                runtime.Load(current);
                loaded = current.Rev;
            }
        });
        var ticker = new Thread(() =>
        {
            var tick = 0;
            long seen = 0;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            // After the last commit, tick on until a snapshot shows it (the loader may load it after any number of ticks).
            while (Volatile.Read(ref done) == 0 || tick < 20 || (seen != doc.Rev && deadline.Elapsed < TimeSpan.FromSeconds(30)))
            {
                using var snapshot = runtime.Tick(tick / 60.0, SceneKit.Now.AddSeconds(tick / 60.0));
                tick++;
                seen = snapshot.Rev;
                var offset = Offset(snapshot.Rev);
                lock (revs) revs.Add(snapshot.Rev);
                var xs = snapshot.Bounds.OrderBy(b => b.Id).Select(b => (int)Math.Round(b.Frame.X) - int.Parse(b.Id[1..]) * 30).ToList();
                if (xs.Any(x => x != offset))
                {
                    lock (failures) failures.Add($"rev {snapshot.Rev}: bounds offsets {string.Join(",", xs)}");
                    continue;
                }
                if (tick % 3 != 0) continue;
                using var bitmap = snapshot.Render();
                for (var i = 0; i < Items; i++)
                {
                    // The rect covers [x, x + 20): inside it red; where the other offset would have drawn it, background.
                    var inside = bitmap.GetPixel(i * 30 + offset + 1, 15);
                    var outside = bitmap.GetPixel(offset == 5 ? i * 30 + 2 : i * 30 + 23, 15);
                    if (inside != new SKColor(255, 0, 0) || outside != new SKColor(0, 0, 0))
                        lock (failures) failures.Add($"rev {snapshot.Rev}: item {i} pixels {inside} {outside}");
                }
            }
        });
        loader.Start();
        ticker.Start();

        for (var c = 0; c < Commits; c++)
        {
            var offset = Offset(doc.Rev + 1);
            doc.Ok(Commit(Enumerable.Range(0, Items).Select(i => Place($"i{i}", i * 30 + offset, 10, baseRev: doc.Rev)).ToArray()));
            // Commits are far faster than ticks: now and then let the ticker catch up, so that it sees many revisions.
            if (c % 10 == 0) SpinWait.SpinUntil(() => { lock (revs) return revs.Contains(doc.Rev); }, TimeSpan.FromSeconds(5));
        }
        Volatile.Write(ref done, 1);
        loader.Join();
        ticker.Join();

        Assert.Empty(failures);
        Assert.Equal(Commits + 1, doc.Rev);
        Assert.Contains(Commits + 1L, revs);
        Assert.True(revs.Count > 3, $"the ticker saw {revs.Count} revisions");
    }
}
