namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The ffmpeg tests decode and parse with: Homebrew's, where Homebrew puts it. Tests that need it skip without it, with
/// the reason; on the Mac the agents gate on, it is installed, so they run.
/// </summary>
internal static class TestFfmpeg
{
    public static string Path { get; } = new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" }.FirstOrDefault(File.Exists) ?? "/opt/homebrew/bin/ffmpeg";

    public static bool Exists => File.Exists(Path);

    public static void SkipWithout() => Xunit.Assert.SkipUnless(Exists, $"No ffmpeg at {Path} (Homebrew's).");
}
