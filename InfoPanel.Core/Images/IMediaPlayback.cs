using System;

namespace InfoPanel.Images
{
    public enum MediaPlaybackStatus
    {
        Stopped,
        Opening,
        Playing,
        Paused,
        Ended,
        Failed,
    }

    /// <summary>
    /// Playback state of a video image, used by the video panel overlay. Implemented by video
    /// providers later; nothing implements it in step 1.
    /// </summary>
    public interface IMediaPlayback
    {
        TimeSpan CurrentTime { get; }

        TimeSpan Duration { get; }

        double FrameRate { get; }

        float Volume { get; }

        MediaPlaybackStatus Status { get; }
    }
}
