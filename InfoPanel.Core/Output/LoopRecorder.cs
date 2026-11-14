using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using InfoPanel.Scenes;
using SkiaSharp;

namespace InfoPanel.Output;

/// <summary>
/// A reading a state gives one key: <see cref="Type"/> is the input type it reads for (<c>number</c>, <c>text</c> or
/// <c>table</c>); an input of another type reads it as unavailable, as a sensor of another type is.
/// </summary>
public sealed record LoopReading(string Type, SceneReading Reading);

/// <summary>
/// One state of a recorded loop: how many frames it lasts and the reading each input key gives throughout it. A key it
/// doesn't list has no reading (unavailable), so a state with no readings is "no reading".
/// </summary>
public sealed record LoopState(int Frames, IReadOnlyDictionary<string, LoopReading> Readings);

/// <summary>A recorded loop: its frames, and the index of each state's first frame.</summary>
public sealed record RecordedLoop(IReadOnlyList<LoopFrame> Frames, IReadOnlyList<int> StateStarts) : IDisposable
{
    /// <summary>The frames state by state, each state's run in order.</summary>
    public IReadOnlyList<IReadOnlyList<LoopFrame>> Runs()
    {
        var runs = new List<IReadOnlyList<LoopFrame>>(StateStarts.Count);
        for (var s = 0; s < StateStarts.Count; s++)
        {
            var end = s + 1 < StateStarts.Count ? StateStarts[s + 1] : Frames.Count;
            var run = new List<LoopFrame>(end - StateStarts[s]);
            for (var i = StateStarts[s]; i < end; i++)
                run.Add(Frames[i]);
            runs.Add(run);
        }
        return runs;
    }

    public void Dispose()
    {
        foreach (var frame in Frames)
            frame.Bitmap.Dispose();
    }
}

/// <summary>
/// The preview recorder: draws a document's frames at fixed times with made-up readings, state by state. Stateless: each
/// call loads the document into a runtime of its own, ticks it frame by frame (frame <c>i</c> at <c>i / fps</c> seconds),
/// and keeps nothing.
/// </summary>
public static class LoopRecorder
{
    public const int Fps = 10;

    /// <summary>
    /// The recorder's runtime options around <paramref name="inputs"/>, from an output's (<paramref name="live"/>): the
    /// same limits, support, regional format and budgets, but the recording's own <paramref name="media"/> (none: every
    /// picture draws as missing; never the outputs' shared cache), no media provider (nothing is fetched: a reference to
    /// one draws as a missing picture), no video played (its media has no decoder), a process node budget of its own (a recording
    /// never pushes a running output's items over theirs) and no log (nothing a recording loads or draws is logged).
    /// </summary>
    public static SceneRuntimeOptions Options(SceneRuntimeOptions live, ISceneInputSource inputs, LoopMedia? media) => new()
    {
        Inputs = inputs,
        Media = media?.Cache,
        VideoOwner = new object(),
        Limits = live.Limits,
        Support = live.Support,
        LocalZone = live.LocalZone,
        Locale = live.Locale,
        Logger = Serilog.Core.Logger.None,
        LayoutsPerTick = live.LayoutsPerTick,
        ItemBudgetMs = live.ItemBudgetMs,
        DrawTime = live.DrawTime,
        SuspendFor = live.SuspendFor,
        ProcessBudget = new SceneProcessBudget(SceneProcessBudget.DefaultMaxNodes),
    };

    /// <summary>
    /// Records <paramref name="states"/> in order. <paramref name="options"/> makes the runtime's options around the
    /// recorder's own input source (pass it as <see cref="SceneRuntimeOptions.Inputs"/>). With
    /// <paramref name="background"/>, every frame is drawn over that colour, so it is opaque; without it, as an output
    /// draws it (the document's own background). <paramref name="cancel"/> is checked before every frame and after the
    /// last. Throws
    /// <see cref="SceneDocumentException"/> for a document the runtime can't load.
    /// </summary>
    public static RecordedLoop Record(JsonElement document, IReadOnlyList<LoopState> states,
        Func<ISceneInputSource, SceneRuntimeOptions> options, DateTimeOffset start, float scale = 1,
        SKColor? background = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(options);
        if (states.Count == 0)
            throw new ArgumentException("A loop needs at least one state.", nameof(states));

        var inputs = new LoopInputs();
        var frames = new List<LoopFrame>();
        var starts = new List<int>();
        var duration = TimeSpan.FromSeconds(1.0 / Fps);
        using var runtime = new SceneRuntime(options(inputs));
        runtime.Load(document);
        try
        {
            foreach (var state in states)
            {
                if (state.Frames <= 0)
                    throw new ArgumentException("Every state needs at least one frame.", nameof(states));
                inputs.Readings = state.Readings;
                starts.Add(frames.Count);
                for (var i = 0; i < state.Frames; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = frames.Count;
                    using var snapshot = runtime.Tick(n / (double)Fps, start + duration * n);
                    var bitmap = snapshot.Render(scale);
                    frames.Add(new LoopFrame(background is { } colour ? Over(bitmap, colour) : bitmap, duration));
                }
            }
            cancel.ThrowIfCancellationRequested();
        }
        catch
        {
            foreach (var frame in frames)
                frame.Bitmap.Dispose();
            throw;
        }
        return new RecordedLoop(frames, starts);
    }

    /// <summary>
    /// The picture and scale for a canvas drawn <paramref name="width"/> pixels wide: the height in proportion, rounded
    /// and at least 1.
    /// </summary>
    public static (int Width, int Height, float Scale) Size(int canvasWidth, int canvasHeight, int width)
    {
        var w = Math.Max(1, canvasWidth);
        var h = Math.Max(1, canvasHeight);
        var height = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(h * (double)width / w)));
        return (width, height, (float)width / w);
    }

    /// <summary>A frame as a PNG: the still of a loop, its pixels exactly.</summary>
    public static byte[] Png(SKBitmap frame)
    {
        using var image = SKImage.FromBitmap(frame);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary><paramref name="frame"/> drawn over <paramref name="colour"/> (opaque) into a new bitmap; the frame is disposed.</summary>
    private static SKBitmap Over(SKBitmap frame, SKColor colour)
    {
        using (frame)
        {
            var opaque = new SKBitmap(frame.Info);
            using var canvas = new SKCanvas(opaque);
            canvas.Clear(colour.WithAlpha(255));
            canvas.DrawBitmap(frame, 0, 0);
            canvas.Flush();
            return opaque;
        }
    }

    /// <summary>The current state's readings, each for an input of its type only; no history (every key's is empty).</summary>
    private sealed class LoopInputs : ISceneInputSource
    {
        public IReadOnlyDictionary<string, LoopReading> Readings { get; set; } = new Dictionary<string, LoopReading>();

        public SceneReading Read(string key, string inputType) =>
            Readings.TryGetValue(key, out var reading) && reading.Type == inputType ? reading.Reading : SceneReading.Unavailable;

        public IReadOnlyList<double> History(string key) => [];
    }
}
