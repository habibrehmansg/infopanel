using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Panels.BeadaPanel;

/// <summary>
/// A BeadaPanel model: the code its info reply carries, its id (<c>6C</c>), and its native size, which a write-through
/// stream uses. Names and physical sizes are the client's.
/// </summary>
public sealed record BeadaPanelModel(byte Code, string Id, int Width, int Height)
{
    /// <summary>The 21 models InfoPanel 1.4 knows. Code 1 has no entry: a panel reporting it isn't driven.</summary>
    public static IReadOnlyList<BeadaPanelModel> All { get; } =
    [
        new(17, "2", 480, 480),
        new(18, "2W", 480, 480),
        new(3, "3", 320, 480),
        new(4, "4", 480, 800),
        new(13, "3C", 480, 320),
        new(14, "4C", 800, 480),
        new(10, "5C", 800, 480),
        new(0, "5", 800, 480),
        new(11, "5T", 800, 480),
        new(20, "5S", 480, 854),
        new(2, "6", 480, 1280),
        new(15, "6C", 1280, 480),
        new(16, "6S", 1280, 480),
        new(12, "7C", 800, 480),
        new(19, "7S", 1280, 400),
        new(21, "8", 480, 1920),
        new(24, "Y", 480, 1920),
        new(22, "11", 440, 1920),
        new(25, "X", 440, 1920),
        new(23, "9", 462, 1920),
        new(26, "Z", 462, 1920),
    ];

    private static readonly Dictionary<byte, BeadaPanelModel> ByCodes = All.ToDictionary(m => m.Code);

    /// <summary>The model with <paramref name="code"/>, or null.</summary>
    public static BeadaPanelModel? ByCode(byte code) => ByCodes.GetValueOrDefault(code);
}
