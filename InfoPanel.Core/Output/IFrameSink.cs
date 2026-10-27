using System;

namespace InfoPanel.Output;

/// <summary>
/// What a sink wants rendered: the <paramref name="Scale"/> (1 = profile pixels; normalised by
/// <see cref="FrameScale.Normalize"/>), at most <paramref name="MaxFps"/> frames a second (1–60; 0 or less means
/// <see cref="RenderLoop.DefaultFps"/>), and whether it uses <see cref="RenderedFrame.Bounds"/>.
/// </summary>
public sealed record FrameRequest(float Scale, int MaxFps, bool IncludeBounds = true); // later: ColorType, PanelRotation

/// <summary>Why <see cref="IFrameSink.OnClosed"/> was called.</summary>
public enum FrameSinkClosedReason
{
    /// <summary>The profile was deleted; the sink is detached and gets no more frames.</summary>
    ProfileDeleted,

    /// <summary>The render service is shutting down.</summary>
    Shutdown,

    /// <summary>
    /// The profile can't be drawn within <see cref="FrameScale.MaxPixels"/> at any scale (<see cref="FrameScale.Normalize"/>
    /// is null; no canvas within the document format's limits); the sink is detached.
    /// </summary>
    Unrenderable,
}

/// <summary>
/// Receives a profile's rendered frames from its <see cref="RenderLoop"/> (preview sockets now; panels and overlays later).
/// <list type="bullet">
/// <item><see cref="OnFrame"/> runs on the render thread and must not block: hand the bytes to your own queue. It
/// must not take the workspace's lock either.</item>
/// <item><see cref="Request"/> is read on every tick, so a sink may change it at any time; call
/// <see cref="RenderService.RequestChanged"/> afterwards so the loop picks it up without waiting for its timer. A sink
/// whose request changed gets the next frame even if nothing in it changed.</item>
/// <item>After <see cref="RenderService.Detach"/> returns no <see cref="OnFrame"/> starts; one already under way may
/// still be running.</item>
/// <item>Closing: once <see cref="OnClosed"/> begins, the sink never receives <see cref="OnFrame"/> again, and
/// <see cref="OnClosed"/> is called only after any <see cref="OnFrame"/> under way for that sink has returned (then
/// on the render thread, right after it).</item>
/// <item>An exception thrown by a sink is logged and never stops the loop or the other sinks.</item>
/// </list>
/// </summary>
public interface IFrameSink
{
    string ProfileId { get; }

    FrameRequest Request { get; }

    void OnFrame(RenderedFrame frame);

    /// <summary>
    /// The sink was detached by the service, not by its owner: its profile was deleted, can't be rendered, or the
    /// service is shutting down. Called once, never while this sink's <see cref="OnFrame"/> runs and never followed by
    /// one; on the closing thread, or on the render thread right after an <see cref="OnFrame"/> that was under way.
    /// Must not block. Not called after <see cref="RenderService.Detach"/>.
    /// </summary>
    void OnClosed(FrameSinkClosedReason reason) { }
}
