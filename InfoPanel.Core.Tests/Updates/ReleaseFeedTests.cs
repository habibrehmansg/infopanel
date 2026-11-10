using System.Text;
using System.Text.Json;
using InfoPanel.Updates;
using Xunit;

namespace InfoPanel.Core.Tests.Updates;

/// <summary>
/// The engine's release list (<see cref="ReleaseFeed"/>): GitHub's releases read for their tag, draft and pre-release
/// flags only; the newest version above this build's that its channel takes, the channel told by this build's version (a
/// pre-release build takes pre-releases and releases, a release build releases only), never by GitHub's flag; drafts, 1.x
/// and tags that aren't SemVer 2 never offered; the release page built from the tag, never taken from the list.
/// </summary>
public sealed class ReleaseFeedTests
{
    private const string Page = "https://github.com/habibrehmansg/infopanel/releases/tag/";

    private static FeedRelease Release(string tag, bool draft = false, bool prerelease = false) => new(tag, draft, prerelease);

    private static string? Chosen(string current, params FeedRelease[] releases) => ReleaseFeed.Choose(current, releases)?.Version;

    /// <summary>The channel table: what a build is offered, by its version and the releases listed.</summary>
    [Theory]
    // A beta build takes the next beta, and the release.
    [InlineData("2.0.0-beta.1", "2.0.0-beta.2", "2.0.0-beta.2")]
    [InlineData("2.0.0-beta.1", "2.0.0", "2.0.0")]
    // A release build takes the next release, never a pre-release.
    [InlineData("2.0.0", "2.0.1", "2.0.1")]
    [InlineData("2.0.0", "2.0.1-beta.1", null)]
    // 1.x's newest release and pre-release are never offered to 2.x.
    [InlineData("2.0.0-beta.1", "1.3.1", null)]
    [InlineData("2.0.0-beta.1", "1.4.0-preview.9", null)]
    [InlineData("2.0.0", "1.3.1", null)]
    // SemVer's order: numbers as numbers.
    [InlineData("2.0.0-beta.9", "2.0.0-beta.10", "2.0.0-beta.10")]
    // The same version: nothing.
    [InlineData("2.0.0-beta.2", "2.0.0-beta.2", null)]
    [InlineData("2.0.0", "2.0.0", null)]
    // Older: nothing.
    [InlineData("2.0.0-beta.2", "2.0.0-beta.1", null)]
    [InlineData("2.0.1", "2.0.0", null)]
    public void TheChannel_FollowsTheBuildsVersion(string current, string tag, string? offered) =>
        Assert.Equal(offered, Chosen(current, Release(tag, prerelease: tag.Contains('-'))));

    [Fact]
    public void AReleaseBuild_TakesARelease_EvenWhileGitHubMarksItAPreRelease() =>
        Assert.Equal("2.0.1", Chosen("2.0.0", Release("2.0.1", prerelease: true)));

    [Fact]
    public void ADraft_IsNeverOffered() =>
        Assert.Null(Chosen("2.0.0-beta.1", Release("2.0.0-beta.9", draft: true)));

    [Fact]
    public void TheHighestWins_InAnyOrder()
    {
        FeedRelease[] releases =
        [
            Release("2.0.0-beta.2"), Release("2.0.0-beta.10"), Release("1.4.0-preview.4"), Release("2.0.0-beta.9"),
            Release("2.0.0-beta.11", draft: true), Release("1.3.1"), Release("2.0.0-beta.3"),
        ];

        Assert.Equal("2.0.0-beta.10", Chosen("2.0.0-beta.1", releases));
        Assert.Equal("2.0.0-beta.10", Chosen("2.0.0-beta.1", [.. releases.Reverse()]));
        Assert.Equal("2.0.0-beta.10", Chosen("2.0.0-beta.1", [.. releases.OrderBy(r => r.Tag, StringComparer.Ordinal)]));
    }

    [Theory]
    [InlineData("v2.0.1")]
    [InlineData("2.0")]
    [InlineData("02.0.1")]
    [InlineData("2.0.1-")]
    [InlineData("2.0.1-beta..1")]
    [InlineData("2.0.1-beta.01")]
    [InlineData("latest")]
    [InlineData("")]
    public void ATagThatIsntSemVer2_IsSkipped(string tag) =>
        Assert.Equal("2.0.0-beta.2", Chosen("2.0.0-beta.1", Release(tag), Release("2.0.0-beta.2")));

    [Fact]
    public void AnOfferNamesTheTag_AndItsPage_BuiltFromTheTag() =>
        Assert.Equal(new UpdateOffer("2.0.0-beta.2", Page + "2.0.0-beta.2"), ReleaseFeed.Choose("2.0.0-beta.1", [Release("2.0.0-beta.2")]));

    [Fact]
    public void TheReleasePage_IsBuiltFromTheTag_EvenWhenTheListPointsElsewhere()
    {
        var body = Encoding.UTF8.GetBytes("""
            [{"tag_name": "2.0.0-beta.2", "draft": false, "prerelease": true,
              "html_url": "https://example.test/not-ours", "url": "https://example.test/api",
              "assets": [{"browser_download_url": "https://example.test/setup.exe"}], "body": "notes"}]
            """);

        var releases = ReleaseFeed.Parse(body);

        Assert.NotNull(releases);
        var offer = ReleaseFeed.Choose("2.0.0-beta.1", releases);
        Assert.Equal(Page + "2.0.0-beta.2", offer?.Url);
    }

    [Fact]
    public void Parse_ReadsTheTag_AndTheTwoFlags_Only()
    {
        var body = Encoding.UTF8.GetBytes("""
            [{"tag_name": "2.0.0-beta.1", "draft": false, "prerelease": true, "name": "anything"},
             {"tag_name": "2.0.0-beta.9", "draft": true, "prerelease": true},
             {"tag_name": "1.3.1", "draft": false, "prerelease": false},
             {"name": "no tag"},
             {"tag_name": 7},
             "not a release"]
            """);

        Assert.Equal([Release("2.0.0-beta.1", prerelease: true), Release("2.0.0-beta.9", draft: true, prerelease: true), Release("1.3.1")],
            ReleaseFeed.Parse(body));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[")]
    [InlineData("not json")]
    [InlineData("[]garbage")]
    [InlineData("[][]")]
    public void Parse_RefusesAnythingButAList(string text) => Assert.Null(ReleaseFeed.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Parse_TakesWhitespaceAfterTheList() =>
        Assert.Equal([Release("2.0.1")], ReleaseFeed.Parse(Encoding.UTF8.GetBytes("[{\"tag_name\": \"2.0.1\"}] \r\n\t ")));

    /// <summary>A tag that can't be read as text (a lone surrogate) is skipped as a tag that isn't SemVer 2 is: the rest still count.</summary>
    [Fact]
    public void Parse_SkipsATagThatCantBeRead_AndKeepsTheRest()
    {
        var releases = ReleaseFeed.Parse(Encoding.UTF8.GetBytes("""[{"tag_name":"\uD800"},{"tag_name":"2.0.1"}]"""));

        Assert.Equal([Release("2.0.1")], releases);
        Assert.Equal("2.0.1", ReleaseFeed.Choose("2.0.0", releases!)?.Version);
    }

    [Fact]
    public void Parse_RefusesABodyOverOneMebibyte()
    {
        var entry = JsonSerializer.Serialize(new { tag_name = "2.0.0-beta.2", draft = false, prerelease = true, body = new string('x', 1000) });
        var under = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat(entry, 900)) + "]");
        var over = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat(entry, 1100)) + "]");
        Assert.True(under.Length <= ReleaseFeed.MaxBytes);
        Assert.True(over.Length > ReleaseFeed.MaxBytes);

        Assert.NotNull(ReleaseFeed.Parse(under));
        Assert.Null(ReleaseFeed.Parse(over));
    }

    [Theory]
    [InlineData("2.0.0-dev", true)]
    [InlineData("2.0.0-dev.ci.12", true)]
    [InlineData("2.1.0-dev", true)]
    [InlineData("2.0.0-beta.1", false)]
    [InlineData("2.0.0", false)]
    [InlineData("2.0.0-develop", false)]
    public void ASourceBuild_IsOneWhosePreReleaseStartsWithDev(string version, bool dev) =>
        Assert.Equal(dev, ReleaseFeed.IsDevelopmentBuild(version));

    /// <summary>SemVer 2 §11's own example, in order, each pair both ways; build metadata takes no part.</summary>
    [Fact]
    public void Versions_CompareAsSemVer2Says()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0-beta.1", "2.0.0", "10.0.0"];

        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = 0; j < ordered.Length; j++)
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(ReleaseFeed.Compare(ordered[i], ordered[j])));
        }

        Assert.Equal(0, ReleaseFeed.Compare("2.0.0-beta.1+abc", "2.0.0-beta.1"));
    }
}
