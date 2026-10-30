using InfoPanel.Images.Video;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// FFmpeg's libraries for the video tests: <c>INFOPANEL_FFMPEG_DIR</c>'s, else Homebrew's on a Mac. A test that needs them
/// skips, saying why, when neither has them (the gate runs them on the Mac, where a skip fails it).
/// </summary>
internal static class FFmpegForTests
{
    private static readonly Lazy<(FFmpeg? Library, string Why)> Loaded = new(() =>
    {
        var library = FFmpeg.Load([Environment.GetEnvironmentVariable(FFmpeg.FolderVariable), "/opt/homebrew/lib", "/usr/local/lib"], out var why);
        return (library, why);
    });

    /// <summary>The libraries, or a skip with the reason.</summary>
    public static FFmpeg Library
    {
        get
        {
            var (library, why) = Loaded.Value;
            if (library is null) Assert.Skip($"FFmpeg's libraries weren't found ({why}): set {FFmpeg.FolderVariable}.");
            return library!;
        }
    }

    /// <summary>The folder they were loaded from, for an engine's setting; null without them.</summary>
    public static string? Folder => Loaded.Value.Library?.Folder;
}
