namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The ffmpeg tests decode and parse with. <see cref="Path"/> is Homebrew's, where Homebrew puts it: the software encoder's
/// tests run it, and skip without it (on Windows always: Windows has no software H.264 encoder). <see cref="DecoderPath"/>
/// is the one the hardware encoders' tests decode with: Homebrew's off Windows, and on Windows the first
/// <c>ffmpeg.exe</c> on <c>PATH</c>, a test tool only (the engine never runs ffmpeg there). On the Mac the agents gate on
/// it is installed, so they run.
/// </summary>
internal static class TestFfmpeg
{
    public static string Path { get; } = new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" }.FirstOrDefault(File.Exists) ?? "/opt/homebrew/bin/ffmpeg";

    public static bool Exists => !OperatingSystem.IsWindows() && File.Exists(Path);

    public static void SkipWithout() => Xunit.Assert.SkipUnless(Exists,
        OperatingSystem.IsWindows() ? "Windows has no software H.264 encoder: its ffmpeg tests don't run here." : $"No ffmpeg at {Path} (Homebrew's).");

    /// <summary>The ffmpeg that decodes: on Windows the first <c>ffmpeg.exe</c> on <c>PATH</c> (null: none), elsewhere <see cref="Path"/>.</summary>
    public static string? DecoderPath { get; } = OperatingSystem.IsWindows() ? OnPath("ffmpeg.exe") : File.Exists(Path) ? Path : null;

    public static void SkipWithoutDecoder() => Xunit.Assert.SkipWhen(DecoderPath == null,
        OperatingSystem.IsWindows() ? "No ffmpeg.exe on PATH to decode with (winget install ffmpeg)." : $"No ffmpeg at {Path} (Homebrew's).");

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Where(System.IO.Path.IsPathFullyQualified)
            .Select(d => System.IO.Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
}
