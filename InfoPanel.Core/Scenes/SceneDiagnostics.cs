using System.Collections.Generic;

namespace InfoPanel.Scenes;

/// <summary>
/// Something the runtime reports about an item for a tick without making it a placeholder (spec/docs/unsupported.md,
/// "What is not unsupported"), or why it is one: <c>font.missing</c>, <c>media.missing</c>, <c>media.loading</c>, <c>media.budget</c>,
/// <c>media.provider</c>, <c>time.zone</c>, <c>time.locale</c>, <c>budget.*</c>.
/// </summary>
public sealed record SceneDiagnostic(string ItemId, string Code, string Message);

/// <summary>An item's diagnostics for one tick, each code and message once.</summary>
internal sealed class ItemDiagnostics(string itemId)
{
    private readonly HashSet<(string, string)> _seen = [];

    public List<SceneDiagnostic> Items { get; } = [];

    public void Add(string code, string message)
    {
        if (Items.Count < 64 && _seen.Add((code, message))) Items.Add(new SceneDiagnostic(itemId, code, message));
    }
}
