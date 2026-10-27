using InfoPanel.Drawing;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace InfoPanel.Output;

/// <summary>
/// The engine's frame-rate overlay (a profile's <c>statsOverlay</c>, spec/docs/document.md): a dark band across the top
/// of the canvas with the profile's name on the left and the loop's ticks per second and the time a tick takes on the
/// right. Drawn by the render loop over the tick's frame, on live outputs only (not thumbnails); 1.x drew the same band.
/// </summary>
internal sealed class StatsOverlay
{
    /// <summary>The band's height in profile pixels.</summary>
    public const float Height = 15;

    private readonly Queue<long> _ticks = new();
    private double _lastMs;

    /// <summary>Counts a tick that took <paramref name="duration"/>, ending at <paramref name="timestamp"/>.</summary>
    public void Record(TimeSpan duration, long timestamp, TimeProvider time)
    {
        _lastMs = duration.TotalMilliseconds;
        _ticks.Enqueue(timestamp);
        while (_ticks.Count > 0 && time.GetElapsedTime(_ticks.Peek(), timestamp) > TimeSpan.FromSeconds(1))
            _ticks.Dequeue();
    }

    /// <summary>The left and right texts for the next frames.</summary>
    public (string Left, string Right) Text(string profileName) => (profileName, $"CPU | FPS {_ticks.Count} | {Math.Round(_lastMs)}ms");

    /// <summary>Draws the band at <paramref name="scale"/> over a canvas <paramref name="profileWidth"/> profile pixels wide.</summary>
    public static void Draw(SKCanvas canvas, float scale, int profileWidth, (string Left, string Right) text)
    {
        var count = canvas.Save();
        canvas.Scale(scale);
        using var band = new SKPaint { Color = new SKColor(0, 0, 0, 0x84) };
        canvas.DrawRect(new SKRect(0, 0, profileWidth, Height), band);

        var typeface = FontRegistry.TryGet("Inter", 400, SKFontStyleSlant.Upright, out var inter) ? inter : SKTypeface.Default;
        using var font = new SKFont(typeface, 10);
        using var ink = new SKPaint { Color = new SKColor(0, 0xFF, 0), IsAntialias = true };
        var baseline = (Height - font.Metrics.Ascent - font.Metrics.Descent) / 2;
        canvas.DrawText(text.Left, 5, baseline, SKTextAlign.Left, font, ink);
        canvas.DrawText(text.Right, profileWidth - 5, baseline, SKTextAlign.Right, font, ink);
        canvas.RestoreToCount(count);
    }
}
