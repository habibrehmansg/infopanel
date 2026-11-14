using System.Text.Json;

namespace InfoPanel.AiUsage;

/// <summary>Tokens of one model at one speed, by kind.</summary>
internal sealed class TokenCounts
{
    public long Input;
    public long Write5m;
    public long Write1h;
    public long Read;
    public long Output;

    public long Total => Input + Write5m + Write1h + Read + Output;

    public void Add(TokenCounts other)
    {
        Input += other.Input;
        Write5m += other.Write5m;
        Write1h += other.Write1h;
        Read += other.Read;
        Output += other.Output;
    }

    /// <summary>Takes <paramref name="other"/> away, each kind at most down to 0.</summary>
    public void Subtract(TokenCounts other)
    {
        Input = Math.Max(0, Input - other.Input);
        Write5m = Math.Max(0, Write5m - other.Write5m);
        Write1h = Math.Max(0, Write1h - other.Write1h);
        Read = Math.Max(0, Read - other.Read);
        Output = Math.Max(0, Output - other.Output);
    }
}

/// <summary>One model's prices, in dollars per million tokens; <see cref="Fast"/> multiplies them all for fast mode (null: no fast price).</summary>
internal sealed record ModelPrice(string Model, double Input, double Output, double? CacheRead, double? Fast);

/// <summary>
/// The price table (<c>prices.json</c>, built into the plugin): what Claude Code's use would cost on Anthropic's API.
/// A model is matched by its id, or its id followed by a date (<c>-YYYYMMDD</c> or <c>@YYYYMMDD</c>) and at most a size
/// in brackets (<c>[1m]</c>), the longest id first; a model with no match (another version, <c>claude-opus-5-6</c>), or
/// fast use of one with no fast price, has no price and is left out of the cost.
/// </summary>
internal sealed class Prices
{
    private readonly List<ModelPrice> _models;

    public Prices(IEnumerable<ModelPrice> models, double cacheWrite5m, double cacheWrite1h, double cacheRead)
    {
        _models = models.OrderByDescending(m => m.Model.Length).ToList();
        CacheWrite5m = cacheWrite5m;
        CacheWrite1h = cacheWrite1h;
        CacheRead = cacheRead;
    }

    public double CacheWrite5m { get; }
    public double CacheWrite1h { get; }

    /// <summary>A cache read's price as a share of the input price, for a model whose table row gives none.</summary>
    public double CacheRead { get; }

    public IReadOnlyList<ModelPrice> Models => _models;

    /// <summary>The table built into the plugin.</summary>
    public static Prices Default { get; } = Load();

    /// <summary>The prices of <paramref name="model"/>; null when the table has none.</summary>
    public ModelPrice? Of(string model)
    {
        foreach (var price in _models)
        {
            if (model.StartsWith(price.Model, StringComparison.Ordinal) && Variant(model.AsSpan(price.Model.Length)))
                return price;
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="suffix"/>, what follows a listed id, names a variant of that model: nothing, a date
    /// (<c>-YYYYMMDD</c> or <c>@YYYYMMDD</c>), then at most a size in brackets (<c>[1m]</c>). Anything else (<c>-6</c>
    /// after <c>claude-opus-5</c>) is another model.
    /// </summary>
    private static bool Variant(ReadOnlySpan<char> suffix)
    {
        if (suffix.Length >= 9 && suffix[0] is '-' or '@' && IsDigits(suffix.Slice(1, 8)) && (suffix.Length == 9 || suffix[9] == '['))
            suffix = suffix[9..];
        if (suffix.IsEmpty)
            return true;
        return suffix.Length >= 3 && suffix[0] == '[' && suffix[^1] == ']' && suffix[1..^1].IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyz") < 0;
    }

    private static bool IsDigits(ReadOnlySpan<char> text) => text.IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>What <paramref name="counts"/> of <paramref name="model"/> cost, in dollars; null when it has no price (at that speed).</summary>
    public double? Cost(string model, bool fast, TokenCounts counts)
    {
        if (Of(model) is not { } price)
            return null;
        var factor = 1.0;
        if (fast)
        {
            if (price.Fast is not { } multiplier)
                return null;
            factor = multiplier;
        }
        var read = price.CacheRead ?? price.Input * CacheRead;
        var dollars = counts.Input * price.Input
            + counts.Write5m * price.Input * CacheWrite5m
            + counts.Write1h * price.Input * CacheWrite1h
            + counts.Read * read
            + counts.Output * price.Output;
        return dollars * factor / 1_000_000;
    }

    private static Prices Load()
    {
        using var stream = typeof(Prices).Assembly.GetManifestResourceStream("InfoPanel.AiUsage.prices.json")
            ?? throw new InvalidOperationException("The price table isn't built into the plugin.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var models = root.GetProperty("models").EnumerateArray().Select(m => new ModelPrice(
            m.GetProperty("model").GetString()!,
            m.GetProperty("input").GetDouble(),
            m.GetProperty("output").GetDouble(),
            m.TryGetProperty("cacheRead", out var read) ? read.GetDouble() : null,
            m.TryGetProperty("fast", out var fast) ? fast.GetDouble() : null));
        return new Prices(models, root.GetProperty("cacheWrite5m").GetDouble(), root.GetProperty("cacheWrite1h").GetDouble(), root.GetProperty("cacheRead").GetDouble());
    }
}
