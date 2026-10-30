using System;
using System.Collections.Generic;

namespace InfoPanel.Spec;

/// <summary>
/// The result of <see cref="MediaValidator.Check"/>. <see cref="Info"/> is what the probe found (null when it refused
/// the bytes); <see cref="Errors"/> is empty when the asset is accepted. <see cref="Format"/> is the probed format: a
/// consumer picks its decoder from it, never by sniffing the bytes again. For an accepted SVG, <see cref="Sanitized"/>
/// is the canonical document rebuilt from it (spec/docs/svg.md), and it is the only form of the asset an engine
/// decodes; it is null for raster formats and for anything refused. Deconstructs as <c>(info, errors)</c>.
/// </summary>
public sealed record MediaCheck(MediaInfo? Info, IReadOnlyList<SpecError> Errors, byte[]? Sanitized = null)
{
    public MediaFormat? Format => Info?.Format;

    public void Deconstruct(out MediaInfo? info, out IReadOnlyList<SpecError> errors)
    {
        info = Info;
        errors = Errors;
    }
}

/// <summary>
/// Checks a media asset's bytes before anything decodes them (spec/docs/limits.md, "Media"): the header and container
/// structure (<see cref="MediaProbe"/>; an SVG's byte limit first, and its element limit while it is read; a video's
/// container through <see cref="VideoProbe"/>), then the size, dimension (of <see cref="MediaInfo.Extent"/>), frame and
/// duration limits; a video, decoded a frame at a time as it plays, is held to the dimension limits only. An accepted
/// SVG comes back rebuilt (<see cref="MediaCheck.Sanitized"/>).
/// </summary>
public static class MediaValidator
{
    public static MediaCheck Check(ReadOnlySpan<byte> bytes, SpecLimits? limits = null)
    {
        limits ??= SpecLimits.Default;
        if (bytes.Length > limits.MediaBytes)
            return new(null, [Over("mediaBytes", bytes.Length, limits.MediaBytes)]);

        // SVG is parsed, so its byte limit comes before anything reads it, and parsing stops past the element limit.
        if (MediaProbe.LooksLikeSvg(bytes) && bytes.Length > limits.SvgBytes)
            return new(null, [Over("svgBytes", bytes.Length, limits.SvgBytes)]);

        var info = MediaProbe.Inspect(bytes, out var problem, limits, out var rebuilt);
        if (info is null) return new(null, [new SpecError("", problem?.Code ?? "media.malformed", problem?.Message ?? "Unreadable media.")]);
        if (info.Format == MediaFormat.Svg && info.Elements > limits.SvgElements)
            return new(null, [new SpecError("", "limit.svgElements", $"Over {limits.SvgElements} elements; the limit is {limits.SvgElements} (svgElements).")]);

        // The size limits hold everything a decoder may draw (an SVG's ratio-derived side too); a GIF's size is its canvas.
        var errors = new List<SpecError>();
        var (width, height) = info.Extent;
        var area = (long)width * height;
        if (width > limits.MediaSide || height > limits.MediaSide)
            errors.Add(Over("mediaSide", Math.Max(width, height), limits.MediaSide));
        else if (area > limits.MediaArea)
            errors.Add(Over("mediaArea", area, limits.MediaArea));
        // A video is decoded as it plays, a frame at a time: only its size is held to the limits.
        if (info.IsVideo) return new(info, errors);
        var sizeFailed = errors.Count > 0;
        if (info.Frames > limits.MediaFrames) errors.Add(Over("mediaFrames", info.Frames, limits.MediaFrames));
        // One cause, one error: a size already over a limit isn't counted against the frame area too.
        else if (!sizeFailed && area * info.Frames > limits.MediaFrameArea) errors.Add(Over("mediaFrameArea", area * info.Frames, limits.MediaFrameArea));
        if (info.DurationSeconds > limits.MediaDuration)
            errors.Add(new SpecError("", "limit.mediaDuration", $"{info.DurationSeconds} s is over the limit of {limits.MediaDuration} s."));
        return new(info, errors, errors.Count == 0 && info.Format == MediaFormat.Svg ? rebuilt : null);
    }

    private static SpecError Over(string name, long actual, long limit) =>
        new("", "limit." + name, $"{actual} is over the limit of {limit} ({name}).");
}
