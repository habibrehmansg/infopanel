using System.Collections.Generic;

namespace InfoPanel.Scenes;

/// <summary>
/// Something the runtime reports about an item for a tick without making it a placeholder (spec/docs/unsupported.md,
/// "What is not unsupported"), or why it is one: <c>font.missing</c>, <c>media.missing</c>, <c>media.loading</c>, <c>media.budget</c>,
/// <c>media.provider</c>, <c>time.zone</c>, <c>time.locale</c>, <c>budget.*</c>.
/// </summary>
public sealed record SceneDiagnostic(string ItemId, string Code, string Message);

/// <summary>
/// An item's diagnostics for one tick, each code and message once. Every one is held to the bounds of the frame header's
/// <c>itemBounds[].media</c> (a code of at most 64 characters, a message of at most 1,024), cut here, where they are made,
/// whatever made them: a message can carry text from outside (a provider's answer, a validator naming what it found).
/// </summary>
internal sealed class ItemDiagnostics(string itemId)
{
    /// <summary>The longest code, in Unicode scalar values.</summary>
    public const int CodeLength = 64;

    /// <summary>The longest message, in Unicode scalar values: a longer one is cut, ending in "…".</summary>
    public const int MessageLength = 1024;

    private readonly HashSet<(string, string)> _seen = [];

    public List<SceneDiagnostic> Items { get; } = [];

    /// <summary>The first problem the item's media had (the frame header's <c>itemBounds[].media</c>), or null.</summary>
    public SceneDiagnostic? Media { get; private set; }

    public void Add(string code, string message)
    {
        (code, message) = Bounded(code, message);
        if (Items.Count < 64 && _seen.Add((code, message))) Items.Add(new SceneDiagnostic(itemId, code, message));
    }

    /// <summary>A problem with media the item shows: reported, and the item's <see cref="Media"/> when it is the first.</summary>
    public void AddMedia(string code, string message)
    {
        (code, message) = Bounded(code, message);
        Media ??= new SceneDiagnostic(itemId, code, message);
        Add(code, message);
    }

    private static (string Code, string Message) Bounded(string code, string message) => (Cut(code, CodeLength), Cut(message, MessageLength));

    private static string Cut(string text, int max) => SceneText.Truncate(text, max) is var cut && cut.Length == text.Length ? text : SceneText.Truncate(text, max - 1) + "…";
}
