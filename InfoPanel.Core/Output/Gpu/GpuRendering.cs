using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Output.Gpu;

/// <summary>Why the CPU draws though <c>rendering.gpu</c> is on: a code (<see cref="GpuRendering"/>'s) and what happened.</summary>
public sealed record GpuFallback(string Code, string Message);

/// <summary>What draws the frames now: <c>cpu</c>, <c>metal</c> or <c>direct3d12</c>; the GPU's name; why the CPU draws though the setting is on.</summary>
public sealed record GpuStatus(string Using, string? Device, GpuFallback? Fallback)
{
    public static GpuStatus Cpu { get; } = new(GpuRendering.CpuBackend, null, null);
}

/// <summary>No GPU can draw here, for a reason with its code (<c>gpu.unsupported</c>, <c>gpu.noDevice</c>).</summary>
public sealed class GpuUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A GPU the engine draws with: Skia's context over a platform device (<see cref="GrContextDevice"/>), or a test's
/// stand-in. Used by one thread at a time (under <see cref="GpuRendering"/>'s lock).
/// </summary>
public interface IGpuDevice : IDisposable
{
    /// <summary><c>metal</c> or <c>direct3d12</c>: the settings answer's <c>rendering.using</c>.</summary>
    string Backend { get; }

    /// <summary>The GPU's name, as the platform gives it.</summary>
    string Name { get; }

    /// <summary>A surface of <paramref name="info"/>'s size and type on the GPU, or null when it can't make one that size.</summary>
    SKSurface? CreateSurface(SKImageInfo info);

    /// <summary>Sends what was drawn to the GPU and waits until it is done.</summary>
    void Submit();

    /// <summary>Why the device is lost, or null while it works.</summary>
    string? CheckLost();

    /// <summary>Frees the GPU memory of resources unused for <paramref name="unused"/> (textures of media no longer shown).</summary>
    void PurgeUnused(TimeSpan unused);

    /// <summary>The platform's scope around one draw (an autorelease pool on macOS), or null.</summary>
    IDisposable? DrawScope();
}

/// <summary>
/// A device whose drawing is Skia's <see cref="GRContext"/>: its resource cache held to
/// <see cref="GpuRendering.CacheBytes"/>, surfaces budgeted in it, and on dispose the context abandoned (its resources
/// released) and disposed before the platform's device (<see cref="ReleaseDevice"/>).
/// </summary>
public abstract class GrContextDevice : IGpuDevice
{
    private int _disposed;

    protected GrContextDevice(GRContext context, string backend, string name)
    {
        Context = context;
        Backend = backend;
        Name = name;
        context.SetResourceCacheLimit(GpuRendering.CacheBytes);
    }

    public GRContext Context { get; }

    public string Backend { get; }

    public string Name { get; }

    public virtual SKSurface? CreateSurface(SKImageInfo info) => SKSurface.Create(Context, true, info);

    public virtual void Submit() => Context.Flush(submit: true, synchronous: true);

    public virtual string? CheckLost() => Context.IsAbandoned ? "Skia's GPU context was abandoned." : null;

    public void PurgeUnused(TimeSpan unused) => Context.PurgeUnusedResources((long)unused.TotalMilliseconds);

    public virtual IDisposable? DrawScope() => null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (!Context.IsAbandoned)
                Context.AbandonContext(releaseResources: true);
            Context.Dispose();
        }
        finally
        {
            ReleaseDevice();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the platform's device and queue, after Skia's context is gone.</summary>
    protected abstract void ReleaseDevice();
}

/// <summary>
/// Drawing with the GPU (the <c>rendering.gpu</c> setting; spec/docs/api.md "Settings"), one per
/// <see cref="RenderService"/>. Off (the default), or while the GPU can't draw, <see cref="Draw"/> draws on a canvas over
/// the target bitmap, as the engine always did. On, it draws on a GPU surface of the target's size and type, waits for
/// the GPU and reads the result back into the target, so every output after the draw (the hash, the frame every sink
/// shares, PNG, video, desktop windows, panels) takes the same CPU frame as before.
/// <list type="bullet">
/// <item>One device at a time, made by <see cref="Apply"/> (at start-up when the setting is on, and on each change of
/// it), so the settings answer already says what draws. A Skia GPU context is used by one thread at a time: every GPU
/// draw holds this object's lock, so loops and thumbnails draw on the GPU one after another. No GPU object outlives one
/// draw but the device itself.</item>
/// <item>A device that can't be made gives a fallback with its code (<c>gpu.unsupported</c>, <c>gpu.noDevice</c>,
/// <c>gpu.failed</c>, which one not made within <see cref="MakeTimeout"/> is too: released when it comes) and CPU frames; one lost while drawing (any device operation throwing: its scope, surface, submit,
/// read-back, <see cref="IGpuDevice.CheckLost"/> or freeing resources; or a failed read-back or a loss
/// <see cref="IGpuDevice.CheckLost"/> reports) is released, that frame is drawn again on the CPU (none is lost or blank),
/// and the fallback is <c>gpu.lost</c> until the setting is turned off and on again, or the engine restarts. Each is
/// logged once; the stored setting never changes.</item>
/// <item>A size the device can't make a surface for is drawn on the CPU, logged once per size: not a fallback.</item>
/// <item>Skia's GPU resource cache is held to <see cref="CacheBytes"/>, and resources unused for
/// <see cref="PurgeAfter"/> are freed, checked at most every <see cref="PurgeEvery"/>.</item>
/// </list>
/// </summary>
public sealed class GpuRendering : IDisposable
{
    public const string CpuBackend = "cpu", Metal = "metal", Direct3D12 = "direct3d12";

    public const string Unsupported = "gpu.unsupported", NoDevice = "gpu.noDevice", Failed = "gpu.failed", Lost = "gpu.lost";

    /// <summary>The most GPU memory Skia keeps for resources (textures of the media it draws): its own default, made explicit.</summary>
    public const long CacheBytes = 256L * 1024 * 1024;

    /// <summary>
    /// How long making the device may take: past it the CPU draws (<c>gpu.failed</c>, <see cref="TookTooLong"/>) and the
    /// settings answer, and a device made later is released unused.
    /// </summary>
    public static readonly TimeSpan MakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The fallback's message when making the device took longer than <see cref="MakeTimeout"/>.</summary>
    public const string TookTooLong = "The GPU didn't start within 5 seconds.";

    /// <summary>How long a GPU resource goes unused before it is freed, and how often that is checked.</summary>
    public static readonly TimeSpan PurgeAfter = TimeSpan.FromSeconds(30), PurgeEvery = TimeSpan.FromSeconds(10);

    private readonly Func<IGpuDevice> _devices;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    /// <summary>Sizes the device refused a surface for, each logged once; under the lock.</summary>
    private readonly HashSet<(int Width, int Height)> _refused = [];

    /// <summary>The device while the GPU draws; read without the lock to keep the CPU path off it, changed under it.</summary>
    private volatile IGpuDevice? _device;

    private volatile GpuStatus _status = GpuStatus.Cpu;
    private bool _on;
    private bool _disposed;
    private long _purged;

    /// <param name="devices">Makes the device when the GPU is turned on (throws when it can't); null: <see cref="ForThisComputer"/>.</param>
    public GpuRendering(Func<IGpuDevice>? devices = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _devices = devices ?? ForThisComputer;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Log.ForContext<GpuRendering>();
    }

    /// <summary>What draws now.</summary>
    public GpuStatus Status => _status;

    /// <summary>The stats overlay's first word: <c>CPU</c>, <c>Metal</c> or <c>Direct3D 12</c>.</summary>
    public string Drawing => Label(Status.Using);

    /// <summary>A backend's name in the stats overlay and the log.</summary>
    public static string Label(string backend) => backend switch
    {
        Metal => "Metal",
        Direct3D12 => "Direct3D 12",
        _ => "CPU",
    };

    /// <summary>This computer's GPU: Metal on macOS; elsewhere <see cref="GpuUnavailableException"/> <c>gpu.unsupported</c>.</summary>
    public static IGpuDevice ForThisComputer()
    {
        if (OperatingSystem.IsMacOS())
            return MetalDevice.Create();
        if (OperatingSystem.IsWindows())
            throw new GpuUnavailableException(Unsupported, "This build of the engine has no Direct3D 12 backend: frames are drawn on the CPU.");
        throw new GpuUnavailableException(Unsupported, "There is no GPU backend on this system: frames are drawn on the CPU.");
    }

    /// <summary>
    /// Turns drawing with the GPU on (making the device) or off (releasing it), and answers what draws then. Turning it on
    /// while it is on changes nothing, a fallback included: off and on again tries the device again.
    /// </summary>
    public GpuStatus Apply(bool gpu)
    {
        lock (_lock)
        {
            if (_disposed || gpu == _on)
                return _status;

            _on = gpu;
            if (!gpu)
            {
                var drew = _device != null;
                Release();
                _status = GpuStatus.Cpu;
                if (drew)
                    _logger.Information("Drawing on the CPU: rendering.gpu is off");
                return _status;
            }

            // Made on a thread of its own and waited for at most MakeTimeout: this runs under the settings' lock, which every
            // settings request takes. A device made later is released, never drawn with.
            var making = Task.Run(_devices);
            if (!Finishes(making, MakeTimeout))
            {
                _ = making.ContinueWith(ReleaseLate, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _status = new GpuStatus(CpuBackend, null, new GpuFallback(Failed, TookTooLong));
                _logger.Warning("The GPU didn't start within {Timeout}; drawing on the CPU", MakeTimeout);
                return _status;
            }

            try
            {
                var device = making.GetAwaiter().GetResult();
                _refused.Clear();
                _purged = _time.GetTimestamp();
                _status = new GpuStatus(device.Backend, device.Name, null);
                _device = device;
                _logger.Information("Drawing with the GPU: {Backend} on {Device}", Label(device.Backend), device.Name);
            }
            catch (Exception ex)
            {
                var (code, message) = ex is GpuUnavailableException unavailable ? (unavailable.Code, unavailable.Message) : (Failed, ex.Message);
                _status = new GpuStatus(CpuBackend, null, new GpuFallback(code, message));
                _logger.Warning("The GPU can't draw ({Code}: {Message}); drawing on the CPU", code, message);
            }

            return _status;
        }
    }

    /// <summary>Whether <paramref name="task"/> ends, done or failed, within <paramref name="bound"/>.</summary>
    private static bool Finishes(Task task, TimeSpan bound)
    {
        try
        {
            return task.Wait(bound);
        }
        catch (AggregateException)
        {
            // It failed: its own exception is thrown again where its result is taken.
            return true;
        }
    }

    /// <summary>A device made after <see cref="MakeTimeout"/>: released unused (a failure to make it, logged).</summary>
    private void ReleaseLate(Task<IGpuDevice> making)
    {
        if (!making.IsCompletedSuccessfully)
        {
            _logger.Information(making.Exception?.GetBaseException(), "The GPU that didn't start in time failed to start");
            return;
        }

        try
        {
            making.Result.Dispose();
            _logger.Information("The GPU that didn't start in time started late; released unused");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Releasing the GPU that started late failed");
        }
    }

    /// <summary>
    /// Raised after each draw with the backend that drew its frame (<c>cpu</c>, <c>metal</c>, <c>direct3d12</c>): per
    /// draw, where <see cref="Status"/> says only what draws now (a refused size is drawn on the CPU while it says the
    /// GPU). For tests.
    /// </summary>
    internal event Action<string>? Drew;

    /// <summary>
    /// Draws <paramref name="draw"/> into <paramref name="target"/> (Rgba8888 premultiplied): on the GPU, read back into
    /// it, while the GPU draws; else on a canvas over it. <paramref name="draw"/> gets a canvas that starts with what the
    /// target holds on the CPU and with nothing on the GPU, so it clears the canvas first. An exception it throws is its
    /// own (the scene's), never a loss of the GPU, and is thrown on.
    /// </summary>
    /// <returns>The backend that drew the frame the target holds: <c>cpu</c>, or the GPU's.</returns>
    public string Draw(SKBitmap target, Action<SKCanvas> draw) => Draw(target, (canvas, _) => draw(canvas));

    /// <summary>
    /// <see cref="Draw(SKBitmap, Action{SKCanvas})"/>, with <paramref name="draw"/> told which backend runs it
    /// (<c>cpu</c>, <c>metal</c>, <c>direct3d12</c>): the CPU's redraw after a loss is told <c>cpu</c>.
    /// </summary>
    public string Draw(SKBitmap target, Action<SKCanvas, string> draw)
    {
        if (_device != null)
        {
            lock (_lock)
            {
                // The backend is taken before the draw: a device lost after its frame was read back whole (freeing its
                // resources failed) is released by then, and a released device's answers can't be relied on.
                if (_device is { } device)
                {
                    var backend = device.Backend;
                    if (TryDraw(device, backend, target, draw))
                    {
                        Drew?.Invoke(backend);
                        return backend;
                    }
                }
            }
        }

        DrawOnCpu(target, canvas => draw(canvas, CpuBackend));
        Drew?.Invoke(CpuBackend);
        return CpuBackend;
    }

    /// <summary>Draws <paramref name="draw"/> on a canvas over <paramref name="target"/>: the CPU path.</summary>
    public static void DrawOnCpu(SKBitmap target, Action<SKCanvas> draw)
    {
        using var canvas = new SKCanvas(target);
        draw(canvas);
        canvas.Flush();
    }

    /// <summary>
    /// The fallback's message when a device operation threw while drawing: fixed, so what the exception says (which can
    /// name files or the system's own text) stays in the log.
    /// </summary>
    public const string FailedWhileDrawing = "The GPU failed while drawing.";

    /// <summary>
    /// Under the lock: one draw on <paramref name="device"/>, read back into the target; false when the CPU draws it. Every
    /// device operation (its scope, the surface, submitting, reading back, <see cref="IGpuDevice.CheckLost"/>, freeing
    /// resources) is guarded: one that throws or reports a loss releases the device (<c>gpu.lost</c>), and the frame is
    /// drawn on the CPU. Only <paramref name="draw"/>'s own exception is thrown on, the device kept.
    /// </summary>
    private bool TryDraw(IGpuDevice device, string backend, SKBitmap target, Action<SKCanvas, string> draw)
    {
        var info = target.Info;
        string? lost = null;
        Exception? failure = null;
        IDisposable? scope = null;
        SKSurface? surface = null;
        var drawn = false;
        try
        {
            try
            {
                scope = device.DrawScope();
                surface = device.CreateSurface(info);
                if (surface == null)
                    lost = device.CheckLost();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure == null && lost == null && surface == null)
            {
                if (_refused.Add((info.Width, info.Height)))
                    _logger.Warning("The GPU ({Backend}) can't make a {Width}×{Height} surface; frames of that size are drawn on the CPU", Label(backend), info.Width, info.Height);
            }
            else if (surface != null && failure == null && lost == null)
            {
                draw(surface.Canvas, backend);
                try
                {
                    device.Submit();
                    if (!surface.ReadPixels(info, target.GetPixels(), target.RowBytes, 0, 0))
                        lost = "Reading the frame back from the GPU failed.";
                    lost ??= device.CheckLost();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                drawn = failure == null && lost == null;
            }
        }
        finally
        {
            // Run whether the draw threw or not: a scope or surface that can't be let go is a loss too.
            try
            {
                surface?.Dispose();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            try
            {
                scope?.Dispose();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            if (failure != null || lost != null)
                Lose(device, lost, failure);
        }

        if (!drawn || failure != null)
            return false;

        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_purged, now) >= PurgeEvery)
        {
            _purged = now;
            try
            {
                device.PurgeUnused(PurgeAfter);
            }
            catch (Exception ex)
            {
                // The frame was read back whole: it stands, and later ones are drawn on the CPU.
                Lose(device, null, ex);
            }
        }

        return true;
    }

    /// <summary>
    /// Under the lock: <paramref name="device"/> is lost (<paramref name="reason"/>, its own words, or
    /// <paramref name="failure"/>, which the fallback names only as <see cref="FailedWhileDrawing"/>): logged once,
    /// released, and <c>gpu.lost</c> until the setting is turned off and on again.
    /// </summary>
    private void Lose(IGpuDevice device, string? reason, Exception? failure)
    {
        if (!ReferenceEquals(_device, device))
            return;

        var message = reason ?? FailedWhileDrawing;
        _logger.Warning(failure, "The GPU ({Backend} on {Device}) was lost while drawing: {Reason}; drawing on the CPU until rendering.gpu is turned off and on again, or the engine restarts",
            Label(device.Backend), device.Name, message);
        Release();
        _status = new GpuStatus(CpuBackend, null, new GpuFallback(Lost, message));
    }

    /// <summary>Under the lock: releases the device, if any.</summary>
    private void Release()
    {
        var device = _device;
        _device = null;
        if (device == null)
            return;

        try
        {
            device.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Releasing the GPU failed");
        }
    }

    /// <summary>Releases the device; later draws are on the CPU.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _on = false;
            Release();
            _status = GpuStatus.Cpu;
        }
    }
}
