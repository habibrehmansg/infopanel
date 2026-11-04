using System;
using System.Globalization;
using System.Text.Json;

namespace InfoPanel.Sensors.Weather;

/// <summary>
/// The configured service's weather answer as the engine reads it (docs/2.0/weather.md): <c>format</c>
/// <see cref="Format"/>, the place the service used, when the answer <c>expires</c>, whether it is <c>stale</c> (the
/// service couldn't refresh it), the current hour's values (metric always), the next 24 hours' high and low, sunrise and
/// sunset, and the credit to show with the data. A value the service gives as <c>null</c>, or of another type, is null
/// here (that reading has no value); members the engine doesn't read are ignored.
/// </summary>
public sealed record WeatherAnswer
{
    /// <summary>The answer's format.</summary>
    public const string Format = "infopanel.weather/1";

    /// <summary>The largest answer read, in bytes; a longer one is refused unread past it.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The deepest nesting read.</summary>
    public const int MaxDepth = 16;

    public string? PlaceName { get; init; }
    public string? PlaceCountry { get; init; }

    /// <summary><c>network</c> (found from the request's network address) or <c>given</c> (the place the engine asked for).</summary>
    public string? PlaceFrom { get; init; }

    public DateTimeOffset Expires { get; init; }
    public bool Stale { get; init; }

    public double? Temperature { get; init; }
    public double? FeelsLike { get; init; }
    public double? Humidity { get; init; }
    public double? DewPoint { get; init; }
    public double? Pressure { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindGust { get; init; }
    public double? WindDirection { get; init; }
    public double? CloudCover { get; init; }
    public double? UvIndex { get; init; }
    public double? Precipitation { get; init; }
    public string? Symbol { get; init; }
    public string? Condition { get; init; }
    public double? High { get; init; }
    public double? Low { get; init; }
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }

    /// <summary>The credit to show with the data (the answer's <c>attribution.text</c>).</summary>
    public string? Credit { get; init; }

    /// <summary>
    /// The answer in <paramref name="json"/>, or null when it isn't one: not JSON, nested deeper than
    /// <see cref="MaxDepth"/>, another format, or without a readable <c>expires</c>, <c>stale</c>, <c>place</c> or
    /// <c>current</c>.
    /// </summary>
    public static WeatherAnswer? Parse(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "format") != Format
                || Time(root, "expires") is not { } expires
                || !root.TryGetProperty("stale", out var stale) || stale.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || Member(root, "place") is not { } place
                || Member(root, "current") is not { } current)
            {
                return null;
            }

            var next = Member(root, "next24h");
            var sun = Member(root, "sun");
            var attribution = Member(root, "attribution");
            return new WeatherAnswer
            {
                PlaceName = Text(place, "name"),
                PlaceCountry = Text(place, "country"),
                PlaceFrom = Text(place, "from"),
                Expires = expires,
                Stale = stale.GetBoolean(),
                Temperature = Number(current, "temperature"),
                FeelsLike = Number(current, "feelsLike"),
                Humidity = Number(current, "humidity"),
                DewPoint = Number(current, "dewPoint"),
                Pressure = Number(current, "pressure"),
                WindSpeed = Number(current, "windSpeed"),
                WindGust = Number(current, "windGust"),
                WindDirection = Number(current, "windDirection"),
                CloudCover = Number(current, "cloudCover"),
                UvIndex = Number(current, "uvIndex"),
                Precipitation = Number(current, "precipitation"),
                Symbol = Text(current, "symbol"),
                Condition = Text(current, "condition"),
                High = next is { } n ? Number(n, "high") : null,
                Low = next is { } m ? Number(m, "low") : null,
                Sunrise = sun is { } s ? Time(s, "sunrise") : null,
                Sunset = sun is { } t ? Time(t, "sunset") : null,
                Credit = attribution is { } a ? Text(a, "text") : null,
            };
        }
    }

    private static JsonElement? Member(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var x) && double.IsFinite(x) ? x : null;

    /// <summary>An ISO 8601 date and time with its offset (<c>2026-11-01T13:00:00Z</c>), or null.</summary>
    private static DateTimeOffset? Time(JsonElement parent, string name) =>
        Text(parent, name) is { } text
        && text.Contains('T')
        && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > text.IndexOf('T'))
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}
