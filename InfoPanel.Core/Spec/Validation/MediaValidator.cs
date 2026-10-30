using System;
using System.Collections.Generic;
using System.IO;

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
        // A video is held to videoBytes, anything else to mediaBytes: the container is told by its first bytes.
        if (SizeError(bytes[..Math.Min(bytes.Length, 12)], bytes.Length, limits) is { } size)
            return new(null, [size]);

        // SVG is parsed, so its byte limit comes before anything reads it, and parsing stops past the element limit.
        if (MediaProbe.LooksLikeSvg(bytes) && bytes.Length > limits.SvgBytes)
            return new(null, [Over("svgBytes", bytes.Length, limits.SvgBytes)]);

        var info = MediaProbe.Inspect(bytes, out var problem, limits, out var rebuilt);
        return Limited(info, problem, limits, rebuilt);
    }

    /// <summary>
    /// As <see cref="Check(ReadOnlySpan{byte}, SpecLimits?)"/>, for a stored file (seekable, from its start): a video's
    /// container is read in place, never whole; anything else is read whole only when it is within <c>mediaBytes</c>.
    /// </summary>
    public static MediaCheck Check(Stream stream, SpecLimits? limits = null)
    {
        limits ??= SpecLimits.Default;
        var length = stream.Length;
        Span<byte> head = stackalloc byte[12];
        stream.Position = 0;
        var n = 0;
        while (n < head.Length && stream.Read(head[n..]) is var read and > 0) n += read;
        if (SizeError(head[..n], length, limits) is { } size)
            return new(null, [size]);
        if (VideoProbe.Detect(head[..n], out _) == VideoProbe.Container.None)
        {
            // Within mediaBytes: read whole, as an upload in memory is checked.
            var bytes = new byte[length];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            return Check(bytes, limits);
        }
        stream.Position = 0;
        var info = VideoProbe.Inspect(stream, out var problem);
        return Limited(info, problem, limits, null);
    }

    /// <summary>The bytes a file starting <paramref name="head"/> may hold, told by its first bytes (<see cref="HeadBytes"/>): <c>videoBytes</c> for a video container, <c>mediaBytes</c> for anything else.</summary>
    public static (long Bytes, string Name) ByteCap(ReadOnlySpan<byte> head, SpecLimits limits) =>
        VideoProbe.Detect(head[..Math.Min(head.Length, HeadBytes)], out _) != VideoProbe.Container.None
            ? (limits.VideoBytes, "videoBytes")
            : (limits.MediaBytes, "mediaBytes");

    /// <summary>The first bytes <see cref="ByteCap"/> reads.</summary>
    public const int HeadBytes = 12;

    /// <summary>The size error for <paramref name="length"/> bytes starting <paramref name="head"/>: over <c>videoBytes</c> for a video, <c>mediaBytes</c> for anything else.</summary>
    private static SpecError? SizeError(ReadOnlySpan<byte> head, long length, SpecLimits limits) =>
        ByteCap(head, limits) is var (cap, name) && length > cap ? Over(name, length, cap) : null;

    /// <summary>What a probe found, held to the media limits.</summary>
    private static MediaCheck Limited(MediaInfo? info, MediaProblem? problem, SpecLimits limits, byte[]? rebuilt)
    {
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
