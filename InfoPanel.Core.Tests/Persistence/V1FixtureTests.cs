using InfoPanel.Models;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

public class V1FixtureTests
{
    private static string FixturePath(string relative) => Path.Combine(AppContext.BaseDirectory, relative);

    [Fact]
    public void Fixtures_V1_LoadWithCurrentModels()
    {
        var items = V1Format.ReadItems(FixturePath(V1Format.ItemsFixture));

        var all = Walk(items).ToList();
        Assert.Equal(14, all.Select(i => i.GetType()).Distinct().Count());

        var nested = items.OfType<GroupDisplayItem>()
            .SelectMany(g => g.DisplayItems.OfType<GroupDisplayItem>())
            .Single();
        Assert.Equal(2, nested.DisplayItems.Count);

        var gauge = Assert.Single(all.OfType<GaugeDisplayItem>());
        Assert.Equal(["g0.png", "g1.png", "g2.png"], gauge.Images.Select(i => i.FilePath));

        var profiles = V1Format.ReadProfiles(FixturePath(V1Format.ProfilesFixture));
        Assert.Equal(2, profiles.Count);
        Assert.Equal(FixtureGenerator.DeskProfileGuid, profiles[0].Guid);
        Assert.Equal(2560, profiles[0].TargetWindow?.Width);
    }

    private static IEnumerable<DisplayItem> Walk(IEnumerable<DisplayItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item is GroupDisplayItem group)
            {
                foreach (var child in Walk(group.DisplayItems))
                    yield return child;
            }
        }
    }
}
