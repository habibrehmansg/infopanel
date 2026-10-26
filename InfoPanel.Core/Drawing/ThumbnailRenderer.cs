using InfoPanel.Editing;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace InfoPanel.Drawing;

/// <summary>
/// One <see cref="ProfileRenderState"/> per profile, shared by everything that renders it (thumbnails now,
/// M6's render loops), so per-item animation state and the tick are the profile's, not the caller's.
/// </summary>
public sealed class ProfileRenderStates
{
    private readonly ConcurrentDictionary<Guid, ProfileRenderState> _states = new();

    public ProfileRenderState Get(Guid profileId) => _states.GetOrAdd(profileId, _ => new ProfileRenderState());

    public void Remove(Guid profileId) => _states.TryRemove(profileId, out _);
}

/// <summary>
/// Renders a profile to a PNG of a requested width with <see cref="ProfileRenderer"/> (the panel path), at
/// <c>scale = width / profile.Width</c>.
/// Ticks: with no render loop running for the profile (M4, or M6 with nobody subscribed) each thumbnail advances
/// the profile's tick, so clocks and animations show the current time instead of the time frozen at the last
/// tick; when a loop is running (M6's <c>RenderService.RenderOnce</c>) the caller passes
/// <c>advanceTick: false</c> to render at the loop's current tick.
/// </summary>
public sealed class ThumbnailRenderer(ProfileRenderer renderer, ProfileRenderStates states)
{
    public const int MinWidth = 16;
    public const int MaxWidth = 4096;
    public const int MaxHeight = 4096;

    /// <summary>
    /// The pixel size and scale for <paramref name="requestedWidth"/>: the width clamped to
    /// <see cref="MinWidth"/>..<see cref="MaxWidth"/>, the height proportional; a height over
    /// <see cref="MaxHeight"/> is capped and the width shrunk to match. Both sides are bounded whatever the profile's
    /// size (at most <see cref="MaxWidth"/> × <see cref="MaxHeight"/>, which is <c>FrameScale.MaxPixels</c>): the height
    /// is compared in double before it becomes an int, so a huge profile can't overflow past the cap.
    /// </summary>
    public static (int Width, int Height, float Scale) Size(int profileWidth, int profileHeight, int requestedWidth)
    {
        var pw = Math.Max(1, profileWidth);
        var ph = Math.Max(1, profileHeight);
        var width = Math.Clamp(requestedWidth, MinWidth, MaxWidth);
        var scale = (float)width / pw;
        var exactHeight = Math.Round(ph * (double)scale);
        if (exactHeight > MaxHeight)
        {
            scale = (float)MaxHeight / ph;
            width = (int)Math.Clamp(Math.Round(pw * (double)scale), 1, MaxWidth);
            return (width, MaxHeight, scale);
        }

        return (width, Math.Max(1, (int)exactHeight), scale);
    }

    /// <summary>
    /// Thumbnails draw bar and donut values at their target: a smoothed render would start each thumbnail mid-way
    /// from the last one, and unsmoothed renders leave the smoothing state alone (see <c>GraphDraw</c>).
    /// </summary>
    public static readonly RenderOptions Options = new(Smoothing: false);

    public byte[] RenderPng(ProfileDocument document, int requestedWidth, bool advanceTick = true)
    {
        using var bitmap = Render(document, requestedWidth, advanceTick);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private SKBitmap Render(ProfileDocument document, int requestedWidth, bool advanceTick)
    {
        lock (document.SyncRoot)
        {
            var profile = document.Profile;
            var (width, height, scale) = Size(profile.Width, profile.Height, requestedWidth);
            var state = states.Get(document.Id);
            if (advanceTick)
                state.Tick++;

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            try
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);
                renderer.Draw(canvas, profile, document.Items, state, Options, scale);
                canvas.Flush();
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }
}
