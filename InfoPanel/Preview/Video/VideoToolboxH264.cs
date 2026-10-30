using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace InfoPanel.Preview.Video;

/// <summary>
/// Faults VideoToolbox doesn't produce on demand, injected beneath the backend interface (tests only; each backend has
/// its own, so no test reaches another's sessions).
/// </summary>
internal sealed class VideoToolboxFaults
{
    /// <summary>Reading the next encoded buffers throws inside the output callback.</summary>
    public int ThrowOnRead;

    /// <summary>Settings to report as refused, by name (<c>ProfileLevel</c>, <c>AllowFrameReordering</c>, <c>YCbCrMatrix</c>, <c>RealTime</c>, …).</summary>
    public HashSet<string> Refuse { get; init; } = [];

    /// <summary>
    /// Properties that read back other than set: <c>UsingHardwareAcceleratedVideoEncoder</c> (false), <c>AllowFrameReordering</c>
    /// (true), <c>YCbCrMatrix</c> (not BT.709).
    /// </summary>
    public HashSet<string> Misreport { get; init; } = [];

    /// <summary>The next session creations (each pixel format tried counts) fail with <see cref="FailCreateStatus"/>.</summary>
    public int FailCreate;

    /// <summary>The OSStatus <see cref="FailCreate"/> fails a creation with (-12915: no encoder free now).</summary>
    public int FailCreateStatus { get; init; } = -12915;

    /// <summary>The next preparations to encode fail (OSStatus -12911).</summary>
    public int FailPrepare;

    /// <summary>A session in RGBA makes no buffers, so the start goes on in BGRA (the retry).</summary>
    public bool RefuseRgba { get; init; }

    /// <summary>Every invalidation waits for this first (a wedged teardown while it isn't set).</summary>
    public ManualResetEventSlim? HoldInvalidate { get; init; }

    /// <summary>How many invalidations, the first ones, wait for <see cref="HoldInvalidate"/> (all by default).</summary>
    public int HeldInvalidations = int.MaxValue;

    /// <summary>Invalidations begun so far (each counted once whether it is held is decided, before it waits).</summary>
    public int Invalidating;

    /// <summary>Takes one of <paramref name="count"/> if any is left.</summary>
    public static bool Take(ref int count)
    {
        while (true)
        {
            var left = Volatile.Read(ref count);
            if (left <= 0)
                return false;
            if (Interlocked.CompareExchange(ref count, left - 1, left) == left)
                return true;
        }
    }
}

/// <summary>
/// The Mac's hardware H.264 encoder, VideoToolbox called directly (no ffmpeg). A session is created with
/// <c>RequireHardwareAcceleratedVideoEncoder</c>; its mandatory settings (Baseline, no frame reordering, the BT.709
/// matrix) are set and read back, and one that doesn't hold refuses the start as a fault, so the chain falls back; its
/// hints (real time, no frame delay, the expected frame rate, the keyframe interval, the bit rate, speed over quality,
/// BT.709 primaries and transfer) are logged when refused and the session runs.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class VideoToolboxH264(ILogger logger) : IH264Backend
{
    /// <summary>How long the probe's 64 × 64 session may take.</summary>
    public static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(1);

    public string Name => "VideoToolbox";

    public H264Kind Kind => H264Kind.Hardware;

    /// <summary>Tests: faults beneath the interface.</summary>
    internal VideoToolboxFaults? Faults { get; init; }

    public H264Probe Probe()
    {
        // On a thread of its own, so a busy thread pool can't hold the probe past its bound; the session only has to
        // start (its teardown, off this thread too, isn't waited for).
        var probe = Quarantine.RunOnThread("VideoToolbox probe", () =>
        {
            var session = VideoToolboxSession.Create(this, new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { }, logger);
            _ = session.Abort();
        });
        try
        {
            if (probe.Wait(ProbeBound))
                return H264Probe.Available;
            logger.LogWarning("VideoToolbox's probe took over {Ms} ms; not used", (int)ProbeBound.TotalMilliseconds);
            return H264Probe.Unavailable;
        }
        catch (AggregateException ex) when (ex.InnerException is H264StartException { Problem: H264StartProblem.Busy } busy)
        {
            // An encoder exists but none is free now: transient (the stream backs off and asks again).
            logger.LogInformation("VideoToolbox's hardware H.264 encoder is busy: {Message}", busy.Message);
            return H264Probe.Busy;
        }
        catch (AggregateException ex)
        {
            logger.LogInformation("VideoToolbox has no hardware H.264 encoder here: {Message}", ex.InnerException?.Message);
            return H264Probe.Unavailable;
        }
    }

    public IH264Session Start(H264Config config, Action<H264Outcome> output) => VideoToolboxSession.Create(this, config, output, logger);
}

/// <summary>One VideoToolbox compression session (see <see cref="VideoToolboxH264"/>).</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class VideoToolboxSession : IH264Session
{
    // OSStatus values a start is refused with (VTErrors.h).
    private const int ParameterErr = -12902;
    private const int AllocationFailedErr = -12904;
    private const int CouldNotFindVideoEncoderErr = -12908;
    private const int VideoEncoderNotAvailableNowErr = -12915;

    /// <summary>Sessions created and not yet torn down in this process (tests: 200 restarts leak none).</summary>
    private static long _live;

    public static long Live => Interlocked.Read(ref _live);

    private readonly Action<H264Outcome> _output;
    private readonly VideoToolboxFaults? _faults;
    private readonly ConcurrentDictionary<long, byte> _outstanding = new();
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool _swizzle;
    private IntPtr _session;
    private GCHandle _self;
    private IntPtr _forceKeyOptions;
    private long _lastPts = -1;
    private long _progress;
    private int _aborted;
    private Task? _teardown;

    // Sessions a start released before this one (the RGBA attempt), still tearing down.
    private readonly List<Task> _retired = [];

    public int Width { get; }

    public int Height { get; }

    /// <summary>The settings as set, for the log ("RealTime=true", "AverageBitRate=2000000 (not set: -12900)").</summary>
    public IReadOnlyList<string> Properties { get; private set; }

    private VideoToolboxSession(int width, int height, Action<H264Outcome> output, VideoToolboxFaults? faults)
    {
        Width = (width + 1) & ~1;
        Height = (height + 1) & ~1;
        _output = output;
        _faults = faults;
        Properties = [];
    }

    /// <summary>
    /// Creates, configures and verifies a session; throws <see cref="H264StartException"/> with the refusal's class. A
    /// start that fails after creating its session retires it as an abort does, off this thread (invalidation can block),
    /// and hands that teardown back with those of the sessions it let go first (<see cref="H264StartException.Teardown"/>)
    /// for the caller to bound. A start that succeeds hands those back as <see cref="Released"/>.
    /// </summary>
    public static VideoToolboxSession Create(VideoToolboxH264 backend, H264Config config, Action<H264Outcome> output, ILogger logger)
    {
        if (config.Width < 1 || config.Height < 1)
            throw new H264StartException(H264StartProblem.Unsupported, $"a {config.Width}x{config.Height} frame");
        var session = new VideoToolboxSession(config.Width, config.Height, output, backend.Faults);
        try
        {
            session.Open(config, logger);
            return session;
        }
        catch (Exception ex)
        {
            var teardown = Task.WhenAll([session.Abort(), .. session.Released]);
            if (ex is H264StartException refusal)
            {
                refusal.Teardown = teardown;
                throw;
            }

            throw new H264StartException(H264StartProblem.Fault, "VideoToolbox's session couldn't be set up: " + ex.Message) { Teardown = teardown };
        }
    }

    private void Open(H264Config config, ILogger logger)
    {
        _self = GCHandle.Alloc(this);

        // RGBA (the frames' own byte order) first; BGRA, swizzled while copying, when VideoToolbox won't take it: a session
        // accepts RGBA source attributes, but its pool may then fail to make a buffer (kCVReturnInvalidPixelFormat).
        var status = 0;
        foreach (var format in (uint[])[VTNative.PixelFormatRgba, VTNative.PixelFormatBgra])
        {
            status = CreateSession(format);
            if (status == 0)
                status = MakesBuffers(format);
            if (status == 0)
            {
                _swizzle = format == VTNative.PixelFormatBgra;
                break;
            }

            ReleaseSession();
        }

        if (_session == IntPtr.Zero)
            throw new H264StartException(Classify(status), $"VTCompressionSessionCreate failed (OSStatus {status})");
        Interlocked.Increment(ref _live);

        var set = new List<string>();
        var refused = new List<string>();
        void Set(string name, IntPtr key, IntPtr value, bool mandatory, bool release = false)
        {
            var result = _faults?.Refuse.Contains(name) == true ? -12900 : key == IntPtr.Zero ? -1 : VTNative.VTSessionSetProperty(_session, key, value);
            if (release && value != IntPtr.Zero)
                VTNative.CFRelease(value);
            set.Add(result == 0 ? name : $"{name} (not set: {result})");
            if (result != 0 && mandatory)
                refused.Add($"{name} (OSStatus {result})");
        }

        // Mandatory: what the stream's guarantees rest on.
        Set("AllowFrameReordering", VTNative.AllowFrameReordering, VTNative.False, mandatory: true);
        Set("ProfileLevel", VTNative.ProfileLevel, VTNative.H264BaselineAutoLevel, mandatory: true);
        Set("YCbCrMatrix", VTNative.YCbCrMatrix, VTNative.Matrix709, mandatory: true);
        // Hints.
        Set("RealTime", VTNative.RealTime, VTNative.True, mandatory: false);
        Set("MaxFrameDelayCount", VTNative.MaxFrameDelayCount, VTNative.Int32(0), mandatory: false, release: true);
        Set("AverageBitRate", VTNative.AverageBitRate, VTNative.Int64(config.Bitrate), mandatory: false, release: true);
        Set("ExpectedFrameRate", VTNative.ExpectedFrameRate, VTNative.Int32(config.Fps), mandatory: false, release: true);
        Set("MaxKeyFrameInterval", VTNative.MaxKeyFrameInterval, VTNative.Int32(600), mandatory: false, release: true);
        Set("PrioritizeEncodingSpeedOverQuality", VTNative.PrioritizeSpeed, VTNative.True, mandatory: false);
        Set("ColorPrimaries", VTNative.ColorPrimaries, VTNative.Primaries709, mandatory: false);
        Set("TransferFunction", VTNative.TransferFunction, VTNative.Transfer709, mandatory: false);
        Properties = set;

        if (refused.Count > 0)
            throw new H264StartException(H264StartProblem.Fault, "VideoToolbox refused a mandatory setting: " + string.Join(", ", refused));

        // The effective configuration, read back where VideoToolbox allows.
        var problems = new List<string>();
        if (_faults?.Misreport.Contains("UsingHardwareAcceleratedVideoEncoder") == true || VTNative.CopyBoolean(_session, VTNative.UsingHardwareEncoder) != true)
            problems.Add("it isn't using the hardware encoder");
        if (_faults?.Misreport.Contains("AllowFrameReordering") == true || VTNative.CopyBoolean(_session, VTNative.AllowFrameReordering) == true)
            problems.Add("it allows frame reordering");
        // The colour the stream's first keyframe must also say (its SPS, checked by the stream).
        if (_faults?.Misreport.Contains("YCbCrMatrix") == true || VTNative.CopyEquals(_session, VTNative.YCbCrMatrix, VTNative.Matrix709) == false)
            problems.Add("its matrix isn't BT.709");
        if (problems.Count > 0)
            throw new H264StartException(H264StartProblem.Fault, "VideoToolbox's session doesn't hold: " + string.Join(", ", problems));

        var hints = set.Where(s => s.Contains("not set", StringComparison.Ordinal)).ToList();
        if (hints.Count > 0)
            logger.LogInformation("VideoToolbox refused hints, running without them: {Hints}", string.Join(", ", hints));

        status = _faults != null && VideoToolboxFaults.Take(ref _faults.FailPrepare) ? -12911 : VTNative.VTCompressionSessionPrepareToEncodeFrames(_session);
        if (status != 0 || VTNative.VTCompressionSessionGetPixelBufferPool(_session) == IntPtr.Zero)
            throw new H264StartException(Classify(status), $"VideoToolbox couldn't prepare the session (OSStatus {status})");
        _forceKeyOptions = VTNative.Dictionary((VTNative.ForceKeyFrame, VTNative.True));
    }

    /// <summary>A refused start's class: no encoder for this configuration is unsupported, none free now is busy, the rest are faults.</summary>
    internal static H264StartProblem Classify(int status) => status switch
    {
        CouldNotFindVideoEncoderErr or ParameterErr => H264StartProblem.Unsupported,
        VideoEncoderNotAvailableNowErr or AllocationFailedErr => H264StartProblem.Busy,
        _ => H264StartProblem.Fault,
    };

    private int CreateSession(uint format)
    {
        if (_faults != null && VideoToolboxFaults.Take(ref _faults.FailCreate))
            return _faults.FailCreateStatus;
        var width = VTNative.Int32(Width);
        var height = VTNative.Int32(Height);
        var pixelFormat = VTNative.Int32(unchecked((int)format));
        var surface = VTNative.Dictionary();
        var source = VTNative.Dictionary(
            (VTNative.PixelBufferPixelFormatType, pixelFormat),
            (VTNative.PixelBufferWidth, width),
            (VTNative.PixelBufferHeight, height),
            (VTNative.PixelBufferIOSurfaceProperties, surface));
        var specification = VTNative.Dictionary((VTNative.RequireHardwareEncoder, VTNative.True));
        foreach (var cf in new[] { width, height, pixelFormat, surface })
            VTNative.CFRelease(cf);

        IntPtr session;
        var callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, uint, IntPtr, void>)&OnOutput;
        var status = VTNative.VTCompressionSessionCreate(IntPtr.Zero, Width, Height, VTNative.CodecH264, specification, source, IntPtr.Zero,
            callback, GCHandle.ToIntPtr(_self), &session);
        VTNative.CFRelease(source);
        VTNative.CFRelease(specification);
        if (status == 0)
            _session = session;
        return status;
    }

    private int MakesBuffers(uint format)
    {
        if (_faults?.RefuseRgba == true && format == VTNative.PixelFormatRgba)
            return -6680; // kCVReturnInvalidPixelFormat
        var pool = VTNative.VTCompressionSessionGetPixelBufferPool(_session);
        if (pool == IntPtr.Zero)
            return -1;
        IntPtr buffer;
        var status = VTNative.CVPixelBufferPoolCreatePixelBuffer(IntPtr.Zero, pool, &buffer);
        if (status == 0)
            VTNative.CFRelease(buffer);
        return status;
    }

    private void ReleaseSession()
    {
        if (_session == IntPtr.Zero)
            return;
        // Off this thread, as every retirement: the start goes on in the next pixel format. The callback handle stays
        // allocated until this invalidation has returned too (Teardown frees it only then).
        var session = _session;
        _session = IntPtr.Zero;
        lock (_retired)
            _retired.Add(Quarantine.RunOnThread("VideoToolbox teardown", () =>
            {
                Invalidate(session);
                VTNative.CFRelease(session);
            }));
    }

    /// <summary>VTCompressionSessionInvalidate, which may block in VideoToolbox (and waits for <see cref="VideoToolboxFaults.HoldInvalidate"/>).</summary>
    private void Invalidate(IntPtr session)
    {
        if (_faults != null)
        {
            var held = _faults.HoldInvalidate != null && VideoToolboxFaults.Take(ref _faults.HeldInvalidations);
            Interlocked.Increment(ref _faults.Invalidating);
            if (held)
                _faults.HoldInvalidate!.Wait();
        }

        VTNative.VTCompressionSessionInvalidate(session);
    }

    /// <summary>Tests: whether the callback handle is still allocated (it must be while any invalidation hasn't returned).</summary>
    internal bool CallbackHandleAlive => _self.IsAllocated;

    /// <summary>Tests: the callback's reference to this session, as VideoToolbox passes it back (zero once freed).</summary>
    internal IntPtr CallbackRefCon => _self.IsAllocated ? GCHandle.ToIntPtr(_self) : IntPtr.Zero;

    /// <summary>Tests: an output callback as VideoToolbox makes it, through the same entry point (no sample buffer).</summary>
    internal static void CallBack(IntPtr refCon, long frameId) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, uint, IntPtr, void>)&OnOutput)(refCon, (IntPtr)frameId, 0, 0, IntPtr.Zero);

    public IReadOnlyList<Task> Released
    {
        get
        {
            lock (_retired)
                return [.. _retired];
        }
    }

    public int InFlight => _outstanding.Count;

    public long Progress => Interlocked.Read(ref _progress);

    public bool TrySetFrameRate(int fps)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return false;
        var value = VTNative.Int32(fps);
        var status = VTNative.VTSessionSetProperty(_session, VTNative.ExpectedFrameRate, value);
        VTNative.CFRelease(value);
        return status == 0;
    }

    public H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem)
    {
        problem = null;
        if (Volatile.Read(ref _aborted) != 0)
        {
            problem = "the session was aborted";
            return H264Submit.Refused;
        }

        var pool = VTNative.VTCompressionSessionGetPixelBufferPool(_session);
        if (pool == IntPtr.Zero)
        {
            problem = "the session has no pixel buffer pool";
            return H264Submit.Refused;
        }

        IntPtr buffer;
        var status = VTNative.CVPixelBufferPoolCreatePixelBuffer(IntPtr.Zero, pool, &buffer);
        if (status != 0)
        {
            problem = $"no pixel buffer (OSStatus {status})";
            return H264Submit.Refused;
        }

        try
        {
            // The CPU mapping of the buffer's IOSurface can fail: then the frame is skipped, the session is fine.
            if (VTNative.CVPixelBufferLockBaseAddress(buffer, 0) != 0)
                return H264Submit.Skipped;
            try
            {
                Copy(pixels, width, height, buffer);
            }
            finally
            {
                VTNative.CVPixelBufferUnlockBaseAddress(buffer, 0);
            }

            // Microseconds since the session started, strictly increasing.
            var pts = (long)System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMicroseconds;
            if (pts <= _lastPts)
                pts = _lastPts + 1;
            _lastPts = pts;

            _outstanding[frameId] = 0;
            uint flags;
            status = VTNative.VTCompressionSessionEncodeFrame(_session, buffer, CMTime.Make(pts, 1_000_000), CMTime.Invalid,
                forceKeyframe ? _forceKeyOptions : IntPtr.Zero, (IntPtr)frameId, &flags);
            if (status == 0)
                return H264Submit.Accepted;

            // Not taken: no callback comes for it (and if one does, it finds no outstanding entry).
            _outstanding.TryRemove(frameId, out _);
            problem = $"VTCompressionSessionEncodeFrame failed (OSStatus {status})";
            return H264Submit.Refused;
        }
        finally
        {
            VTNative.CFRelease(buffer);
        }
    }

    private void Copy(byte[] pixels, int width, int height, IntPtr buffer)
    {
        var dst = VTNative.CVPixelBufferGetBaseAddress(buffer);
        var rowBytes = (int)VTNative.CVPixelBufferGetBytesPerRow(buffer);
        var bufferWidth = Math.Min((int)VTNative.CVPixelBufferGetWidth(buffer), rowBytes / 4);
        var bufferHeight = (int)VTNative.CVPixelBufferGetHeight(buffer);
        var w = Math.Min(width, bufferWidth);
        var h = Math.Min(height, bufferHeight);
        fixed (byte* src = pixels)
        {
            for (var y = 0; y < h; y++)
            {
                var from = src + (long)y * width * 4;
                var to = dst + (long)y * rowBytes;
                if (_swizzle)
                {
                    var s = (uint*)from;
                    var d = (uint*)to;
                    for (var x = 0; x < w; x++)
                    {
                        // RGBA in memory (R lowest) to BGRA: swap the first and third bytes.
                        var p = s[x];
                        d[x] = (p & 0xFF00FF00) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF);
                    }
                }
                else
                {
                    Buffer.MemoryCopy(from, to, rowBytes, (long)w * 4);
                }

                // The padding column (an odd width): black.
                if (bufferWidth > w)
                    new Span<byte>(to + (long)w * 4, (bufferWidth - w) * 4).Clear();
            }

            // The padding row (an odd height): black.
            for (var y = h; y < bufferHeight; y++)
                new Span<byte>(dst + (long)y * rowBytes, bufferWidth * 4).Clear();
        }
    }

    /// <summary>
    /// VideoToolbox's thread. The exception barrier: whatever happens reading the encoded buffer, the frame gets exactly
    /// one outcome (a failure when reading it throws), and nothing unwinds into VideoToolbox. A frame already reported
    /// (aborted) gets nothing more.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOutput(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr sampleBuffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(refCon).Target is not VideoToolboxSession self)
                return;
            var frameId = (long)sourceFrameRefCon;
            if (!self._outstanding.TryRemove(frameId, out _))
                return;

            H264Outcome outcome;
            try
            {
                outcome = self.Read(frameId, status, infoFlags, sampleBuffer);
            }
            catch (Exception ex)
            {
                outcome = new H264Outcome(frameId, H264OutcomeKind.Failed, Problem: "reading the encoded frame failed: " + ex.Message);
            }

            self.Report(outcome);
        }
        catch (Exception)
        {
            // Never let an exception unwind into VideoToolbox.
        }
    }

    private void Report(H264Outcome outcome)
    {
        try
        {
            _output(outcome);
        }
        catch (Exception)
        {
            // The stream's own failure; it isn't VideoToolbox's to see.
        }
        finally
        {
            Interlocked.Increment(ref _progress);
        }
    }

    // kVTEncodeInfo_FrameDropped
    private const uint FrameDropped = 1 << 1;

    /// <summary>The sample buffer's access unit (AVCC: 4-byte NAL lengths), whether it is a sync frame, and on one the avcC as VideoToolbox gave it.</summary>
    private H264Outcome Read(long frameId, int status, uint infoFlags, IntPtr sampleBuffer)
    {
        if (_faults != null && VideoToolboxFaults.Take(ref _faults.ThrowOnRead))
            throw new InvalidOperationException("an injected fault reading the encoded buffer");
        if (status != 0)
            return new H264Outcome(frameId, H264OutcomeKind.Failed, Problem: $"VideoToolbox failed the frame (OSStatus {status})");
        if (sampleBuffer == IntPtr.Zero || (infoFlags & FrameDropped) != 0)
            return new H264Outcome(frameId, H264OutcomeKind.Dropped);

        var block = VTNative.CMSampleBufferGetDataBuffer(sampleBuffer);
        if (block == IntPtr.Zero)
            return new H264Outcome(frameId, H264OutcomeKind.Failed, Problem: "the encoded frame has no data");
        var length = VTNative.CMBlockBufferGetDataLength(block);
        var data = new byte[checked((int)length)];
        fixed (byte* p = data)
        {
            var copied = VTNative.CMBlockBufferCopyDataBytes(block, 0, length, p);
            if (copied != 0)
                return new H264Outcome(frameId, H264OutcomeKind.Failed, Problem: $"the encoded frame couldn't be copied (OSStatus {copied})");
        }

        var key = true;
        var attachments = VTNative.CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
        if (attachments != IntPtr.Zero && VTNative.CFArrayGetCount(attachments) > 0)
        {
            var notSync = VTNative.CFDictionaryGetValue(VTNative.CFArrayGetValueAtIndex(attachments, 0), VTNative.SampleAttachmentNotSync);
            key = notSync == IntPtr.Zero || VTNative.CFBooleanGetValue(notSync) == 0;
        }

        if (!key)
            return new H264Outcome(frameId, H264OutcomeKind.Encoded, data, false);

        byte[]? avcc = null;
        var format = VTNative.CMSampleBufferGetFormatDescription(sampleBuffer);
        var atoms = format == IntPtr.Zero ? IntPtr.Zero : VTNative.CMFormatDescriptionGetExtension(format, VTNative.ExtensionAtoms);
        if (atoms != IntPtr.Zero)
        {
            var avccKey = VTNative.Str("avcC");
            try
            {
                var record = VTNative.CFDictionaryGetValue(atoms, avccKey);
                if (record != IntPtr.Zero)
                    avcc = new ReadOnlySpan<byte>(VTNative.CFDataGetBytePtr(record), checked((int)VTNative.CFDataGetLength(record))).ToArray();
            }
            finally
            {
                VTNative.CFRelease(avccKey);
            }
        }

        return new H264Outcome(frameId, H264OutcomeKind.Encoded, data, true, avcc);
    }

    /// <summary>
    /// Ends the session without draining it: every outstanding frame reported aborted now; the session invalidated and
    /// released on a quarantine thread, which the returned task follows (not the sessions the start let go, which the
    /// caller watches on their own: <see cref="Released"/>). The callback handle is freed only once every invalidation,
    /// theirs too, has returned, so a late callback finds a dead session; while one never returns the handle stays
    /// (deliberately leaked).
    /// </summary>
    public Task Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return _teardown ?? Task.CompletedTask;

        foreach (var id in _outstanding.Keys)
        {
            if (_outstanding.TryRemove(id, out _))
                Report(new H264Outcome(id, H264OutcomeKind.Aborted));
        }

        _teardown = Quarantine.RunOnThread("VideoToolbox teardown", Teardown);
        return _teardown;
    }

    private void Teardown()
    {
        if (_session != IntPtr.Zero)
        {
            Invalidate(_session);
            VTNative.CFRelease(_session);
            _session = IntPtr.Zero;
            Interlocked.Decrement(ref _live);
        }

        if (_forceKeyOptions != IntPtr.Zero)
        {
            VTNative.CFRelease(_forceKeyOptions);
            _forceKeyOptions = IntPtr.Zero;
        }

        // The callback handle outlives every session that could still call back, the released RGBA one too; this
        // teardown doesn't wait for that one (it is watched, and counted when wedged, on its own).
        var released = Released;
        if (released.All(t => t.IsCompleted))
            FreeCallbackHandle();
        else
            _ = Task.WhenAll(released).ContinueWith(_ => FreeCallbackHandle(), TaskScheduler.Default);
    }

    private void FreeCallbackHandle()
    {
        if (_self.IsAllocated)
            _self.Free();
    }
}
