using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Topten.RichTextKit;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The scene runtime's answers to whether the engine has a font family are kept in a bounded cache: family names a
/// client sends that the engine doesn't have can't grow it for good, and a name that left it resolves as before. The
/// cache is process-wide, so these run in the non-parallel collection.
/// </summary>
[Collection(GoldenCollection.Name)]
public class SceneFontMapperTests
{
    /// <summary>A family no computer has (as in <c>SkiaTextDefaultsTests</c>).</summary>
    private const string Missing = "Qqzxw";

    /// <summary><paramref name="count"/> family names no computer has, new to this process.</summary>
    private static string[] MissingNames(int count)
    {
        var run = Guid.NewGuid().ToString("N");
        return Enumerable.Range(0, count).Select(i => $"{Missing} {run} {i}").ToArray();
    }

    /// <summary>What a family resolves to: whether the engine has it, and the face its bold italic is drawn in.</summary>
    private static (bool Has, string Family, int Weight, SKFontStyleSlant Slant, int Glyphs) Resolve(string family)
    {
        var has = SceneFontMapper.Has(family);
        var typeface = SceneFontMapper.Instance.TypefaceFromStyle(new Style { FontFamily = family, FontWeight = 700, FontItalic = true }, false);
        return (has, typeface.FamilyName, typeface.FontWeight, typeface.FontSlant, typeface.GlyphCount);
    }

    /// <summary>The bundled default, the system's own font and a family no computer has.</summary>
    private static string[] Families => [SceneFontMapper.DefaultFamily, SKTypeface.Default.FamilyName, Missing];

    [Fact]
    public void MoreMissingFamiliesThanTheBound_LeaveTheCacheAtOrUnderIt()
    {
        foreach (var name in MissingNames(SceneFontMapper.MaxKnown + 500))
        {
            Assert.False(SceneFontMapper.Has(name));
            var count = SceneFontMapper.KnownCount;
            Assert.True(count <= SceneFontMapper.MaxKnown, $"the cache holds {count} names, over {SceneFontMapper.MaxKnown}");
        }
    }

    [Fact]
    public void AFamilyThatLeftTheCache_ResolvesToTheSameFaceAgain()
    {
        var before = Families.Select(Resolve).ToArray();
        Assert.True(before[0].Has);
        Assert.False(before[2].Has);

        foreach (var name in MissingNames(SceneFontMapper.MaxKnown + 1)) SceneFontMapper.Has(name);
        Assert.All(Families, family => Assert.False(SceneFontMapper.IsKnownCached(family), $"'{family}' is still cached"));

        Assert.Equal(before, Families.Select(Resolve).ToArray());
    }

    [Fact]
    public async Task LookupsFromSeveralThreads_WhileNamesAreAdded_StayCorrect()
    {
        const int threads = 8;
        var expected = Families.Select(Resolve).ToArray();
        var names = MissingNames(SceneFontMapper.MaxKnown * 2);
        using var start = new Barrier(threads);
        var token = TestContext.Current.CancellationToken;

        var workers = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait(token);
            for (var i = t; i < names.Length; i += threads)
            {
                Assert.False(SceneFontMapper.Has(names[i]));
                Assert.Equal(expected, Families.Select(Resolve).ToArray());
                var count = SceneFontMapper.KnownCount;
                Assert.True(count <= SceneFontMapper.MaxKnown, $"the cache holds {count} names, over {SceneFontMapper.MaxKnown}");
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers);
        Assert.Equal(expected, Families.Select(Resolve).ToArray());
    }
}
