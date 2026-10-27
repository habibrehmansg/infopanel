using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfoPanel.Spec;

/// <summary>
/// The limits of format version 1 (spec/docs/limits.md, which gives the reason for each number). Every engine that reads
/// infopanel.scene/1 and infopanel.document/1 accepts everything within them; the numbers never change within major
/// version 1. Each member's camelCase name is the limit's name in docs, error codes (<c>limit.{name}</c>) and the session's
/// <c>limits</c>. Tests may tighten a limit to keep an example small (<see cref="With"/>).
/// </summary>
public sealed record SpecLimits
{
    public static SpecLimits Default { get; } = new();

    // Per scene.
    public int SceneBytes { get; init; } = 1_048_576;
    public int Nodes { get; init; } = 2_000;
    public int Depth { get; init; } = 24;
    public int Inputs { get; init; } = 32;
    public int Values { get; init; } = 256;
    public int StateCells { get; init; } = 64;
    public int ExprDepth { get; init; } = 32;
    public int ExprWork { get; init; } = 20_000;
    public int PathCommands { get; init; } = 8_192;
    public int Geometry { get; init; } = 32_768;
    public int TextNodes { get; init; } = 256;
    public int SeriesNodes { get; init; } = 32;
    public int TableNodes { get; init; } = 8;
    public int MediaNodes { get; init; } = 64;
    public int Sequence { get; init; } = 256;
    public int EffectCost { get; init; } = 64;

    // Per JSON value: every document and request body read, and every document a commit would make.
    public int JsonDepth { get; init; } = 256;

    // Per document.
    public int DocumentBytes { get; init; } = 16_777_216;
    public int Items { get; init; } = 2_000;
    public int GroupDepth { get; init; } = 8;
    public int Bindings { get; init; } = 32;
    public int SourceBytes { get; init; } = 262_144;
    public int OriginBytes { get; init; } = 4_096;
    public int EditorBytes { get; init; } = 65_536;
    public int DocumentNodes { get; init; } = 20_000;
    public int DocumentWork { get; init; } = 200_000;
    public int DocumentTextNodes { get; init; } = 1_000;
    public int DocumentEffectCost { get; init; } = 512;

    // Per commit.
    public int CommitBytes { get; init; } = 16_777_216;
    public int Ops { get; init; } = 1_000;

    // Per media asset, from its header (MediaProbe), before decoding.
    public int MediaBytes { get; init; } = 33_554_432;
    public int SvgBytes { get; init; } = 1_048_576;
    public int MediaSide { get; init; } = 8_192;
    public long MediaArea { get; init; } = 16_777_216;
    public int MediaFrames { get; init; } = 1_000;
    public double MediaDuration { get; init; } = 600;
    public long MediaFrameArea { get; init; } = 268_435_456;
    public int SvgElements { get; init; } = 10_000;

    // At run time (not validation): every string-producing operation (concat, upper, lower, format, formatTime, and
    // input text fields) cuts its result to this many Unicode scalar values, at the operation, before the result is
    // used anywhere, comparisons included (docs/expressions.md, "Numbers and strings").
    public int TextLength { get; init; } = 4_096;

    /// <summary>These limits with some replaced, by name (<c>{"nodes": 8}</c>).</summary>
    public SpecLimits With(IReadOnlyDictionary<string, double> overrides)
    {
        var node = JsonSerializer.SerializeToNode(this, SpecJson.Options)!.AsObject();
        foreach (var (name, value) in overrides)
        {
            if (!node.ContainsKey(name)) throw new KeyNotFoundException($"No limit named '{name}'.");
            node[name] = JsonValue.Create(value);
        }
        return node.Deserialize<SpecLimits>(SpecJson.Options)!;
    }

    /// <summary>Every limit by name, as the session reports them.</summary>
    public IReadOnlyDictionary<string, double> ToDictionary()
    {
        var result = new SortedDictionary<string, double>(System.StringComparer.Ordinal);
        foreach (var (name, value) in JsonSerializer.SerializeToNode(this, SpecJson.Options)!.AsObject())
            result[name] = value!.GetValue<double>();
        return result;
    }
}
