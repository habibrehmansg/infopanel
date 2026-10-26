using InfoPanel.Models;
using System.Collections.Generic;

namespace InfoPanel.Editing;

/// <summary>
/// New items, as 1.x's editor made them (<c>CommonActions</c> and <c>SensorActions</c>): text-like kinds take the
/// profile's font, size and colour; text is named "Custom Text"; clock and calendar are upper-case; a group is
/// "New Group". Sensor-bound kinds start unbound (1.x always bound them to the picked sensor; the API binds them
/// with a property set), named after their kind.
/// </summary>
public static class ItemFactory
{
    /// <summary>The kinds <c>POST /items</c> creates: the eight editable kinds and groups (plan §6.5).</summary>
    public static IReadOnlySet<string> CreatableKinds { get; } =
        new HashSet<string> { "text", "sensor", "clock", "calendar", "bar", "graph", "donut", "shape", "group" };

    /// <exception cref="PatchException">Unknown kind, or one that can't be created (key <c>kind</c>).</exception>
    public static DisplayItem Create(string kind, Profile profile)
    {
        DisplayItem item = kind switch
        {
            "text" => new TextDisplayItem("Custom Text", profile) { Font = profile.Font, FontSize = profile.FontSize, Color = profile.Color },
            "sensor" => new SensorDisplayItem("Sensor", profile) { Font = profile.Font, FontSize = profile.FontSize, Color = profile.Color },
            "clock" => new ClockDisplayItem("Clock", profile) { Font = profile.Font, FontSize = profile.FontSize, Color = profile.Color, Uppercase = true },
            "calendar" => new CalendarDisplayItem("Calendar", profile) { Font = profile.Font, FontSize = profile.FontSize, Color = profile.Color, Uppercase = true },
            "bar" => new BarDisplayItem("Bar", profile),
            "graph" => new GraphDisplayItem("Graph", profile, GraphDisplayItem.GraphType.LINE),
            "donut" => new DonutDisplayItem("Donut", profile),
            "shape" => new ShapeDisplayItem("Shape", profile),
            "group" => new GroupDisplayItem { Name = "New Group" },
            _ when ItemKinds.TryGet(kind, out _) =>
                throw new PatchException("kind", $"'{kind}' items can't be created here; creatable kinds are {string.Join(", ", CreatableKinds)}."),
            _ => throw new PatchException("kind", $"Unknown kind '{kind}'."),
        };

        item.SetProfile(profile);
        return item;
    }
}
