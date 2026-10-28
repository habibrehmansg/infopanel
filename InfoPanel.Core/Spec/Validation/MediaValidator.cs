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
/// structure (<see cref="MediaProbe"/>; an SVG's byte limit first, and its element limit while it is read), then the
/// size, dimension, frame and duration limits. An accepted SVG comes back rebuilt (<see cref="MediaCheck.Sanitized"/>).
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

        var errors = new List<SpecError>();
        var area = (long)info.Width * info.Height;
        if (info.Width > limits.MediaSide || info.Height > limits.MediaSide)
            errors.Add(Over("mediaSide", Math.Max(info.Width, info.Height), limits.MediaSide));
        else if (area > limits.MediaArea)
            errors.Add(Over("mediaArea", area, limits.MediaArea));
        if (info.Frames > limits.MediaFrames) errors.Add(Over("mediaFrames", info.Frames, limits.MediaFrames));
        else if (area * info.Frames > limits.MediaFrameArea) errors.Add(Over("mediaFrameArea", area * info.Frames, limits.MediaFrameArea));
        if (info.DurationSeconds > limits.MediaDuration)
            errors.Add(new SpecError("", "limit.mediaDuration", $"{info.DurationSeconds} s is over the limit of {limits.MediaDuration} s."));
        return new(info, errors, errors.Count == 0 && info.Format == MediaFormat.Svg ? rebuilt : null);
    }

    private static SpecError Over(string name, long actual, long limit) =>
        new("", "limit." + name, $"{actual} is over the limit of {limit} ({name}).");
}
