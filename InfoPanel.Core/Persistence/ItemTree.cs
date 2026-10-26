using System;
using System.Collections.Generic;
using InfoPanel.Models;

namespace InfoPanel.Persistence;

/// <summary>Walks a profile's item tree: top-level items, group children (any depth) and gauge frame images.</summary>
public static class ItemTree
{
    /// <summary>Every item in the tree, depth first, parents before their children.</summary>
    public static IEnumerable<DisplayItem> Walk(IEnumerable<DisplayItem> items)
    {
        foreach (var item in items)
        {
            yield return item;

            switch (item)
            {
                case GroupDisplayItem group:
                    foreach (var child in Walk(group.DisplayItems))
                        yield return child;
                    break;
                case GaugeDisplayItem gauge:
                    foreach (var image in Walk(gauge.Images))
                        yield return image;
                    break;
            }
        }
    }

    /// <summary>
    /// Gives every item in the tree a unique, non-empty id. The first item (in <see cref="Walk"/> order) keeps an id;
    /// later duplicates and empty ids get a new one. Returns how many ids were regenerated.
    /// </summary>
    public static int EnsureUniqueIds(IList<DisplayItem> items)
    {
        var seen = new HashSet<Guid>();
        var regenerated = 0;
        foreach (var item in Walk(items))
        {
            if (item.Guid != Guid.Empty && seen.Add(item.Guid))
                continue;

            do
                item.Guid = Guid.NewGuid();
            while (!seen.Add(item.Guid));
            regenerated++;
        }
        return regenerated;
    }
}
