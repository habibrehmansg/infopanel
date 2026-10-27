using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>An item of a loaded document, with its placement as a matrix (spec/docs/geometry.md).</summary>
internal abstract class DocItem
{
    public required string Id;
    public required string Path;
    public bool Visible = true;

    /// <summary>T(x, y) · T(pivot) · R(rotation) · T(−pivot): local coordinates to the parent's.</summary>
    public SKMatrix Local = SKMatrix.Identity;

    public double Rotation;
}

internal sealed class DocGroup : DocItem
{
    public required DocItem[] Children;
}

internal sealed class DocScene : DocItem
{
    public CompiledScene? Scene;

    /// <summary>unsupported, invalid or budget: drawn as a placeholder.</summary>
    public string? Problem;

    public IReadOnlyList<SpecError> Errors = [];
    public double? LayoutW, LayoutH;

    /// <summary>Per scene input (in declaration order): the bound sensor key and statistic, or null when unbound.</summary>
    public (string Key, string Stat)?[] Bindings = [];

    /// <summary>The layout box, 100 × 100 where a dimension is automatic: a placeholder's frame.</summary>
    public SKRect PlaceholderBox => new(0, 0, (float)(LayoutW ?? 100), (float)(LayoutH ?? 100));
}

/// <summary>
/// A document loaded for rendering: one generation (<see cref="Generation"/>), already checked. Each scene was checked on
/// its own (a bad scene is a placeholder) and compiled once when the generation got it; aggregate budgets were admitted
/// in document order. Loading only builds the item tree around them, so a generation that changed one item costs one
/// item's work.
/// </summary>
internal sealed class SceneDocumentModel
{
    public required string Id;
    public required int Width, Height;
    public required uint Background;
    public required DocItem[] Items;
    public required long Generation;
    public required long Rev;
    public required int NodeCount;

    /// <summary>The item tree of <paramref name="document"/>, which was checked with the runtime's own limits and support.</summary>
    public static SceneDocumentModel Load(Generation document, long generation)
    {
        var content = document.Content;
        var nodes = 0;
        DocItem Build(Item item, string path)
        {
            var placement = item.Placement;
            switch (item)
            {
                case GroupItem g:
                {
                    var children = new DocItem[g.Children.Count];
                    for (var i = 0; i < children.Length; i++) children[i] = Build(g.Children[i], $"{path}/children/{i}");
                    return new DocGroup
                    {
                        Id = g.Id, Path = path, Visible = g.Visible != false, Children = children,
                        Local = PlacementMatrix(placement, 0, 0), Rotation = placement.Rotation ?? 0,
                    };
                }
                case SceneItem s:
                {
                    var d = new DocScene
                    {
                        Id = s.Id, Path = path, Visible = s.Visible != false, LayoutW = s.Layout?.W, LayoutH = s.Layout?.H,
                        Rotation = placement.Rotation ?? 0,
                    };
                    d.Local = PlacementMatrix(placement, d.LayoutW is { } lw ? lw / 2 : 0, d.LayoutH is { } lh ? lh / 2 : 0);
                    var facts = content.Entries[s.Id].Design.Scene!;
                    if (facts.Problem is { } problem)
                    {
                        d.Problem = problem;
                        d.Errors = facts.ErrorsAt(path + "/scene");
                        return d;
                    }
                    if (content.OverBudget.TryGetValue(s.Id, out var over))
                    {
                        d.Problem = "budget";
                        d.Errors = [over];
                        return d;
                    }
                    var scene = facts.Compiled!;
                    d.Scene = scene;
                    nodes += scene.NodeCount;
                    d.Bindings = scene.InputNames.Select(name =>
                        s.Bindings is { } b && b.TryGetValue(name, out var binding) ? ((string, string)?)(binding.Key, binding.Stat ?? "now") : null).ToArray();
                    return d;
                }
                default:
                    return new DocGroup { Id = item.Id, Path = path, Children = [] };
            }
        }

        var doc = document.Document;
        var items = new DocItem[doc.Items.Count];
        for (var i = 0; i < items.Length; i++) items[i] = Build(doc.Items[i], $"/items/{i}");

        return new SceneDocumentModel
        {
            Id = doc.Id,
            Width = doc.Profile.Width,
            Height = doc.Profile.Height,
            Background = SceneColor.TryParse(doc.Profile.Background, out var bg) ? bg : 0xFFFFFFFF,
            Items = items,
            Generation = generation,
            Rev = document.Rev,
            NodeCount = nodes,
        };
    }

    /// <summary>T(x, y) · R(rotation, pivot), with the pivot given or defaulting to (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public static SKMatrix PlacementMatrix(Placement p, double px, double py)
    {
        if (p.Pivot is { Count: 2 } pivot)
        {
            px = pivot[0];
            py = pivot[1];
        }
        var m = SKMatrix.CreateTranslation(SceneMath.F(p.X), SceneMath.F(p.Y));
        var rotation = p.Rotation ?? 0;
        if (rotation != 0) m = m.PreConcat(SKMatrix.CreateRotationDegrees(SceneMath.F(rotation), SceneMath.F(px), SceneMath.F(py)));
        return m;
    }
}

/// <summary>A document that can't be rendered: unsupported or invalid as a whole (<see cref="Check"/> says why).</summary>
public sealed class SceneDocumentException(DocumentCheck check)
    : Exception($"The document can't be rendered ({check.Status}): {string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}"))}")
{
    public DocumentCheck Check { get; } = check;
}
