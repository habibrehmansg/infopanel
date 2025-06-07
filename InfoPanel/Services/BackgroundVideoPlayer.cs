using FFmpeg.AutoGen;
using SkiaSharp;
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Utils;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace InfoPanel;

public unsafe class BackgroundVideoPlayer : IDisposable
{
    private readonly string _filePath;
    private readonly Thread _decodingThread;
    private readonly CancellationTokenSource _cancellationToken = new();
    private volatile bool _disposed = false;
    // Immutable frame data with reference counting for thread-safe sharing
    private class ImmutableFrame
    {
        public readonly AVFrame* Frame; // Store a deep-copied AVFrame
        public readonly TimeSpan Time;
        public readonly long FrameIndex;
        public readonly int Width;
        public readonly int Height;
        private volatile int _refCount = 1;

        public ImmutableFrame(AVFrame* frame, TimeSpan time, long frameIndex, int width, int height)
        {
            Frame = frame;
            Time = time;
            FrameIndex = frameIndex;
            Width = width;
            Height = height;
        }

        public void AddRef() => Interlocked.Increment(ref _refCount);
        public bool Release(BackgroundVideoPlayer parent)
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                parent.FreeFrame(Frame);
                return true;
            }
            return false;
        }
    }

    private void FreeFrame(AVFrame* frame)
    {
        if (frame != null && !_disposed)
        {
            // Return to pool instead of freeing directly
            ReturnFrameToPool(frame);
        }
        else if (frame != null && _disposed)
        {
            // If disposed, free directly to avoid pool usage
            ffmpeg.av_frame_free(&frame);
        }
    }

    private volatile ImmutableFrame? _currentFrame;
    private readonly object _frameLock = new();

    #region Video Properties
    /// <summary>Video frame width in pixels</summary>
    public int Width { get; private set; }

    /// <summary>Video frame height in pixels</summary>
    public int Height { get; private set; }

    /// <summary>Total video duration</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Video frame rate in frames per second</summary>
    public double FrameRate { get; private set; }

    /// <summary>Total number of frames in the video</summary>
    public long TotalFrames { get; private set; }

    /// <summary>Whether the video player is successfully opened and ready</summary>
    public bool IsOpen { get; private set; }
    #endregion

    #region Performance Tracking
    /// <summary>Total number of frames decoded since start</summary>
    public long DecodedFrameCount { get; private set; }

    /// <summary>Number of times the video has looped</summary>
    public int LoopCount { get; private set; }
    #endregion

    #region Audio Properties
    /// <summary>Audio sample rate in Hz</summary>
    public int AudioSampleRate { get; private set; }

    /// <summary>Number of audio channels</summary>
    public int AudioChannels { get; private set; }

    /// <summary>Whether the video contains audio</summary>
    public bool HasAudio { get; private set; }
    #endregion

    // Audio control
    private float _previousVolume = 0.0f;
    private float _volume = 0.0f; // Backing field for per-instance volume

    // Audio playback
    private IWavePlayer? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private readonly object _audioLock = new();

    // Timing control and synchronization
    private readonly Stopwatch _playbackTimer = new();
    private TimeSpan _lastAudioPts = TimeSpan.Zero;
    private TimeSpan _lastVideoPts = TimeSpan.Zero;
    private readonly object _syncLock = new();

    // Connection health monitoring
    private DateTime _lastFrameTime = DateTime.Now;
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(5);

    // Performance optimizations - SWS context caching
    private SwsContext* _cachedSwsContext = null;
    private int _lastTargetWidth = -1;
    private int _lastTargetHeight = -1;
    private AVPixelFormat _lastSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private readonly object _swsContextLock = new();

    // Performance optimizations - Audio buffer pre-allocation
    private const int MAX_AUDIO_BUFFER_SIZE = 1024 * 1024; // 1MB should handle most audio frames
    private byte* _preAllocatedAudioBuffer = null;
    private readonly byte*[] _reusableInputData = new byte*[8]; // Reusable array

    // Performance optimizations - Frame pool for zero-copy operations
    private readonly ConcurrentQueue<IntPtr> _framePool = new();
    private const int MAX_POOL_SIZE = 10; // Limit pool size to control memory
    private int _poolAllocations = 0; // Track total allocations for diagnostics

    // Performance optimizations - Pre-computed timing values
    private double _videoTimeBaseCache = 0.0;
    private double _audioTimeBaseCache = 0.0;

    // Sync tolerance (40ms is typical for A/V sync)
    private readonly TimeSpan _syncTolerance = TimeSpan.FromMilliseconds(40);

    static BackgroundVideoPlayer()
    {
        InitializeFFmpeg();
    }

    private static void InitializeFFmpeg()
    {
        try
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffmpegPath = currentDir;

            if (!File.Exists(Path.Combine(ffmpegPath, "avformat-61.dll")))
            {
                var projectRoot = Directory.GetParent(currentDir)?.Parent?.Parent?.Parent?.FullName;
                ffmpegPath = Path.Combine(projectRoot ?? currentDir, "bin", "x64");

                if (!Directory.Exists(ffmpegPath))
                {
                    ffmpegPath = Path.Combine(Directory.GetCurrentDirectory(), "bin", "x64");
                }
            }

            if (Directory.Exists(ffmpegPath) && File.Exists(Path.Combine(ffmpegPath, "avformat-61.dll")))
            {
                ffmpeg.RootPath = ffmpegPath;
            }

            ffmpeg.avformat_network_init();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize FFmpeg: {ex.Message}", ex);
        }
    }
    public BackgroundVideoPlayer(string filePath)
    {
        _filePath = filePath;

        InitializeVideoProperties();

        // Initialize audio if available
        if (HasAudio)
        {
            InitializeAudio();
        }

        _decodingThread = new Thread(() =>
        {
            try
            {
                DecodingLoop();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Decoding thread error: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "VideoDecoder"
        };

        // Start background decoding thread
        _decodingThread.Start();
        IsOpen = true;
    }

    private void InitializeVideoProperties()
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;

        try
        {
            // Allocate format context
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
                throw new Exception("Failed to allocate format context");

            // Setup optimized FFmpeg options for better performance and reliability
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "fflags", "discardcorrupt", 0);  // Discard corrupted frames
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);        // Minimize latency
            ffmpeg.av_dict_set(&options, "threads", "auto", 0);           // Auto-detect thread count

            // Open input file with optimized options
            if (ffmpeg.avformat_open_input(&formatContext, _filePath, null, &options) < 0)
            {
                ffmpeg.av_dict_free(&options);
                throw new Exception($"Failed to open input file: {_filePath}");
            }

            ffmpeg.av_dict_free(&options); // Clean up options dictionary

            // Find stream info
            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                throw new Exception("Failed to find stream info");

            // Find video stream
            int videoStreamIndex = -1;
            int audioStreamIndex = -1;

            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                }
                else if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamIndex = i;
                }
            }

            if (videoStreamIndex == -1)
                throw new Exception("No video stream found");

            var videoStream = formatContext->streams[videoStreamIndex];
            var codecParameters = videoStream->codecpar;

            // Find decoder
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
                throw new Exception("Decoder not found");

            // Allocate codec context
            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
                throw new Exception("Failed to allocate codec context");

            // Copy codec parameters to context
            if (ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters) < 0)
                throw new Exception("Failed to copy codec parameters");

            // Set video properties
            Width = codecContext->width;
            Height = codecContext->height;

            // Set audio properties
            if (audioStreamIndex != -1)
            {
                var audioStream = formatContext->streams[audioStreamIndex];
                var audioCodecParameters = audioStream->codecpar;

                AudioSampleRate = audioCodecParameters->sample_rate;
                AudioChannels = audioCodecParameters->ch_layout.nb_channels;
                HasAudio = true;
            }
            else
            {
                HasAudio = false;
                AudioSampleRate = 0;
                AudioChannels = 0;
            }

            // Enhanced frame rate detection using av_guess_frame_rate (from sample project)
            var guessedFrameRate = ffmpeg.av_guess_frame_rate(formatContext, videoStream, null);
            var frameRate = ffmpeg.av_q2d(guessedFrameRate);

            // Fallback to stream frame rates if guess fails
            if (frameRate <= 0)
            {
                frameRate = ffmpeg.av_q2d(videoStream->avg_frame_rate);
                if (frameRate <= 0)
                {
                    frameRate = ffmpeg.av_q2d(videoStream->r_frame_rate);
                }
            }

            FrameRate = frameRate > 0 ? frameRate : 30.0;
            Trace.WriteLine($"Detected frame rate: {FrameRate} fps (using av_guess_frame_rate)");

            if (formatContext->duration == ffmpeg.AV_NOPTS_VALUE)
            {
                Duration = TimeSpan.MaxValue;
                TotalFrames = long.MaxValue;
            }
            else
            {
                Duration = TimeSpan.FromSeconds(formatContext->duration / (double)ffmpeg.AV_TIME_BASE);
                TotalFrames = (long)(Duration.TotalSeconds * FrameRate);
            }

        }
        finally
        {
            // Clean up temporary resources
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }
    private void InitializeAudio()
    {
        if (!HasAudio) return;

        try
        {
            lock (_audioLock)
            {
                if (AudioSampleRate <= 0 || AudioChannels <= 0)
                {
                    Trace.WriteLine("Invalid audio parameters detected");
                    HasAudio = false;
                    return;
                }

                // Always use stereo output for simplicity and better compatibility
                var waveFormat = new WaveFormat(AudioSampleRate, 16, 2); // Force stereo

                // Create buffered wave provider to buffer audio data
                _waveProvider = new BufferedWaveProvider(waveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = false
                };

                // Create wave output device
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_waveProvider);
                _waveOut.Play();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to initialize audio: {ex.Message}");
            HasAudio = false;
            // Clean up partially initialized audio components
            try
            {
                _waveOut?.Dispose();
                _waveOut = null;
                _waveProvider = null;
            }
            catch (Exception cleanupEx)
            {
                Trace.WriteLine($"Error during audio cleanup: {cleanupEx.Message}");
            }
        }
    }


    private void DecodingLoop()
    {
        while (!_cancellationToken.Token.IsCancellationRequested)
        {
            try
            {
                // Check for connection timeout before starting new loop
                if (DateTime.Now - _lastFrameTime > _connectionTimeout && DecodedFrameCount > 0)
                {
                    Trace.WriteLine($"Connection timeout detected ({_connectionTimeout.TotalSeconds}s), restarting decode loop");
                    _lastFrameTime = DateTime.Now; // Reset timeout counter
                }

                // On restart, keep current frame to prevent flickering
                // No need to clear anything - current frame will be naturally updated
                lock (_audioLock)
                {
                    _waveProvider?.ClearBuffer();
                }

                DecodeVideoLoop();

                // Video finished, increment loop count and restart
                LoopCount++;
                Thread.Sleep(10); // Brief pause before restarting
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Decoding error: {ex.Message}");
                if (_cancellationToken.Token.IsCancellationRequested)
                    break;
                Thread.Sleep(1000); // Wait before retrying
            }
        }

        _cancellationToken.Dispose();
    }
    private void DecodeVideoLoop()
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* videoCodecContext = null;
        AVCodecContext* audioCodecContext = null;
        AVFrame* videoFrame = null;
        AVFrame* audioFrame = null;
        AVPacket* packet = null;
        SwrContext* swrContext = null;

        lock (_syncLock)
        {
            _lastAudioPts = TimeSpan.Zero;
            _lastVideoPts = TimeSpan.Zero;
        }

        // Start playback timer
        _playbackTimer.Restart();
        Trace.WriteLine($"=== Starting new loop, timer restarted ===");

        try
        {
            // Initialize FFmpeg components
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
                throw new Exception("Failed to allocate format context");

            // Setup optimized FFmpeg options for decoding loop
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "fflags", "discardcorrupt", 0);  // Discard corrupted frames
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);        // Minimize latency

            var formatContextPtr = formatContext;
            if (ffmpeg.avformat_open_input(&formatContextPtr, _filePath, null, &options) < 0)
            {
                ffmpeg.av_dict_free(&options);
                throw new Exception($"Failed to open input file: {_filePath}");
            }

            ffmpeg.av_dict_free(&options); // Clean up options dictionary

            if (formatContextPtr == null)
                throw new Exception("Format context became null after opening input");
            formatContext = formatContextPtr;

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                throw new Exception("Failed to find stream info");

            // Find video and audio streams
            int videoStreamIndex = -1;
            int audioStreamIndex = -1;

            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                }
                else if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && HasAudio)
                {
                    audioStreamIndex = i;
                }
            }

            // Setup video decoder
            var videoStream = formatContext->streams[videoStreamIndex];
            var videoCodecParameters = videoStream->codecpar;
            _videoTimeBaseCache = ffmpeg.av_q2d(videoStream->time_base); // Pre-compute for performance
            Trace.WriteLine($"Video time base: {_videoTimeBaseCache} seconds per tick");

            var videoCodec = ffmpeg.avcodec_find_decoder(videoCodecParameters->codec_id);
            if (videoCodec == null)
                throw new Exception("Video decoder not found");

            videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
            if (videoCodecContext == null)
                throw new Exception("Failed to allocate video codec context");

            if (ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoCodecParameters) < 0)
                throw new Exception("Failed to copy video codec parameters");

            // Disable threading to avoid conflicts
            videoCodecContext->thread_count = 1;
            videoCodecContext->thread_type = 0;

            if (ffmpeg.avcodec_open2(videoCodecContext, videoCodec, null) < 0)
                throw new Exception("Failed to open video codec");

            // Setup audio decoder if audio is present
            if (audioStreamIndex >= 0 && HasAudio)
            {
                var audioStream = formatContext->streams[audioStreamIndex];
                var audioCodecParameters = audioStream->codecpar;
                _audioTimeBaseCache = ffmpeg.av_q2d(audioStream->time_base); // Pre-compute for performance

                var audioCodec = ffmpeg.avcodec_find_decoder(audioCodecParameters->codec_id);
                if (audioCodec == null)
                    throw new Exception("Audio decoder not found");

                audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                if (audioCodecContext == null)
                    throw new Exception("Failed to allocate audio codec context");

                if (ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioCodecParameters) < 0)
                    throw new Exception("Failed to copy audio codec parameters");

                audioCodecContext->thread_count = 1;
                audioCodecContext->thread_type = 0;

                if (ffmpeg.avcodec_open2(audioCodecContext, audioCodec, null) < 0)
                    throw new Exception("Failed to open audio codec");                // Setup audio resampler to convert to 16-bit PCM
                swrContext = ffmpeg.swr_alloc();
                if (swrContext == null)
                    throw new Exception("Failed to allocate resampler");

                // Use new AVChannelLayout API for FFmpeg 6+
                AVChannelLayout inputLayout = audioCodecParameters->ch_layout;
                AVChannelLayout outputLayout = new AVChannelLayout();
                ffmpeg.av_channel_layout_default(&outputLayout, 2); // Stereo output

                // Set input channel layout using new API
                if (ffmpeg.swr_alloc_set_opts2(&swrContext,
                    &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioSampleRate,
                    &inputLayout, (AVSampleFormat)audioCodecParameters->format, audioCodecParameters->sample_rate,
                    0, null) < 0)
                {
                    throw new Exception("Failed to set resampler options");
                }

                if (ffmpeg.swr_init(swrContext) < 0)
                    throw new Exception("Failed to initialize resampler");

                audioFrame = ffmpeg.av_frame_alloc();
                if (audioFrame == null)
                    throw new Exception("Failed to allocate audio frame");
            }

            // Allocate frames and packet
            videoFrame = ffmpeg.av_frame_alloc();
            if (videoFrame == null)
                throw new Exception("Failed to allocate video frame");

            packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                throw new Exception("Failed to allocate packet");
            long frameIndex = 0;

            // Audio buffer for resampled data
            byte* audioBuffer = null;
            int audioBufferSize = 0;
            byte** outDataPtr = stackalloc byte*[1]; // Move outside the loop

            while (!_cancellationToken.Token.IsCancellationRequested)
            {
                ffmpeg.av_packet_unref(packet);
                int error = ffmpeg.av_read_frame(formatContext, packet);

                if (error == ffmpeg.AVERROR_EOF)
                    return; // End of file, will trigger loop restart
                if (error < 0)
                    return;

                // Check for packet corruption (from sample project)
                if (packet->flags == ffmpeg.AV_PKT_FLAG_CORRUPT)
                {
                    Trace.WriteLine("Skipping corrupted packet");
                    continue;
                }

                // Handle packets - video first (most common case for better branch prediction)
                if (packet->stream_index == videoStreamIndex)
                {
                    // Note: videoFrame should be empty from move_ref, but unref to be safe
                    ffmpeg.av_frame_unref(videoFrame);
                    error = ffmpeg.avcodec_send_packet(videoCodecContext, packet);
                    if (error < 0) continue;

                    // Process all available video frames for this packet
                    while (ffmpeg.avcodec_receive_frame(videoCodecContext, videoFrame) == 0)
                    {
                        // Extract timing information BEFORE moving frame data
                        var currentTime = TimeSpan.Zero;
                        if (videoFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                        {
                            currentTime = TimeSpan.FromSeconds(videoFrame->pts * _videoTimeBaseCache);
                            //Console.WriteLine($"Frame {frameIndex}: PTS={videoFrame->pts}, TimeBase={_videoTimeBaseCache:F6}, CurrentTime={currentTime.TotalMilliseconds:F1}ms");

                            // Update video PTS for synchronization (optimized lock scope)
                            var videoPts = currentTime;
                            lock (_syncLock)
                            {
                                _lastVideoPts = videoPts;
                            }
                        }

                        // Now do the zero-copy frame move (after we've extracted timing)
                        AVFrame* frameRef = RentFrameFromPool();
                        if (frameRef == null)
                        {
                            Trace.WriteLine("Warning: Failed to get frame from pool");
                            continue;
                        }

                        // Move frame data without copying (transfers ownership)
                        ffmpeg.av_frame_move_ref(frameRef, videoFrame);
                        // CRITICAL: videoFrame is now empty - must not be used again in this iteration

                        var newFrame = new ImmutableFrame(frameRef, currentTime, frameIndex, Width, Height);
                        var oldFrame = Interlocked.Exchange(ref _currentFrame, newFrame);
                        if (oldFrame != null) oldFrame.Release(this);
                        DecodedFrameCount++;
                        frameIndex++;

                        // Update last frame time for connection monitoring
                        _lastFrameTime = DateTime.Now;

                        // Synchronization-aware timing control
                        if (currentTime != TimeSpan.Zero)
                        {
                            var masterTime = GetMasterClockTime();
                            var elapsed = _playbackTimer.Elapsed;
                            var drift = GetSyncDrift();

                            // Use video PTS as timing reference to prevent audio rush
                            var targetTime = currentTime;
                            var delay = targetTime - elapsed;

                            //Console.WriteLine($"  Elapsed: {elapsed.TotalMilliseconds:F1}ms, Target: {targetTime.TotalMilliseconds:F1}ms, Delay: {delay.TotalMilliseconds:F1}ms, Drift: {drift.TotalMilliseconds:F1}ms");

                            if (delay > TimeSpan.Zero)
                            {
                                //Console.WriteLine($"  SLEEPING for {delay.TotalMilliseconds:F1}ms");
                                Thread.Sleep(delay);
                            }
                            else if (delay <= TimeSpan.Zero)
                            {
                                //Console.WriteLine($"  NO SLEEP - Already behind by {Math.Abs(delay.TotalMilliseconds):F1}ms");
                            }
                        }
                    }
                }
                // Handle audio packets
                else if (packet->stream_index == audioStreamIndex && HasAudio && _waveProvider != null)
                {
                    ffmpeg.av_frame_unref(audioFrame);
                    error = ffmpeg.avcodec_send_packet(audioCodecContext, packet);
                    if (error < 0) continue;

                    // Process all available audio frames for this packet
                    while (ffmpeg.avcodec_receive_frame(audioCodecContext, audioFrame) == 0)
                    {
                        // Track audio PTS for synchronization (optimized lock scope)
                        if (audioFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                        {
                            var audioPts = TimeSpan.FromSeconds(audioFrame->pts * _audioTimeBaseCache); // Use cached value
                            lock (_syncLock)
                            {
                                _lastAudioPts = audioPts;
                            }
                        }
                        // Resample audio to 16-bit PCM
                        int outSamples = (int)ffmpeg.swr_get_out_samples(swrContext, audioFrame->nb_samples);
                        int bytesPerSample = 2; // 16-bit = 2 bytes
                        int outputChannels = 2; // Always stereo output
                        int requiredBufferSize = outSamples * outputChannels * bytesPerSample;

                        // Use pre-allocated buffer if size fits, fallback to dynamic allocation
                        byte* audioBufferToUse;
                        if (requiredBufferSize <= MAX_AUDIO_BUFFER_SIZE)
                        {
                            if (_preAllocatedAudioBuffer == null)
                            {
                                _preAllocatedAudioBuffer = (byte*)ffmpeg.av_malloc(MAX_AUDIO_BUFFER_SIZE);
                                if (_preAllocatedAudioBuffer == null)
                                {
                                    Trace.WriteLine("Critical: Failed to allocate pre-allocated audio buffer");
                                    continue;
                                }
                            }
                            audioBufferToUse = _preAllocatedAudioBuffer;
                        }
                        else
                        {
                            // Fallback for unusually large frames
                            if (audioBufferSize < requiredBufferSize)
                            {
                                if (audioBuffer != null)
                                    ffmpeg.av_free(audioBuffer);
                                audioBuffer = (byte*)ffmpeg.av_malloc((ulong)requiredBufferSize);
                                if (audioBuffer == null)
                                {
                                    Trace.WriteLine($"Critical: Failed to allocate audio buffer of size {requiredBufferSize}");
                                    continue;
                                }
                                audioBufferSize = requiredBufferSize;
                            }
                            audioBufferToUse = audioBuffer;
                        }
                        outDataPtr[0] = audioBufferToUse;

                        // Reuse input data array (performance optimization)
                        // Initialize all elements to prevent garbage pointers
                        for (uint i = 0; i < 8; i++)
                        {
                            _reusableInputData[i] = (i < ffmpeg.AV_NUM_DATA_POINTERS) ? audioFrame->data[i] : null;
                        }

                        fixed (byte** inputDataPtr = _reusableInputData)
                        {
                            int convertedSamples = ffmpeg.swr_convert(swrContext, outDataPtr, audioFrame->nb_samples, inputDataPtr, audioFrame->nb_samples);
                            if (convertedSamples > 0)
                            {
                                int actualSize = convertedSamples * outputChannels * bytesPerSample;
                                byte[] managedBuffer = new byte[actualSize];
                                fixed (byte* managedPtr = managedBuffer)
                                {
                                    Buffer.MemoryCopy(audioBufferToUse, managedPtr, actualSize, actualSize);
                                }
                                // Add audio data to wave provider (no timing delays for audio)
                                lock (_audioLock)
                                {
                                    try
                                    {
                                        if (_waveProvider != null && !_disposed)
                                        {
                                            ApplyVolume(managedBuffer, actualSize, _volume); // Apply per-instance volume
                                            _waveProvider.AddSamples(managedBuffer, 0, actualSize);
                                        }
                                    }
                                    catch (InvalidOperationException ex)
                                    {
                                        Trace.WriteLine($"Audio buffer operation failed: {ex.Message}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.WriteLine($"Audio playback error: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Clean up audio buffer
            if (audioBuffer != null)
            {
                ffmpeg.av_free(audioBuffer);
            }
        }
        finally
        {
            // Clean up resources
            if (swrContext != null)
            {
                ffmpeg.swr_free(&swrContext);
            }

            if (videoFrame != null)
            {
                var framePtr = videoFrame;
                ffmpeg.av_frame_free(&framePtr);
            }

            if (audioFrame != null)
            {
                var framePtr = audioFrame;
                ffmpeg.av_frame_free(&framePtr);
            }

            if (packet != null)
            {
                var packetPtr = packet;
                ffmpeg.av_packet_free(&packetPtr);
            }

            if (videoCodecContext != null)
            {
                var codecPtr = videoCodecContext;
                ffmpeg.avcodec_free_context(&codecPtr);
            }

            if (audioCodecContext != null)
            {
                var codecPtr = audioCodecContext;
                ffmpeg.avcodec_free_context(&codecPtr);
            }

            if (formatContext != null)
            {
                var formatPtr = formatContext;
                ffmpeg.avformat_close_input(&formatPtr);
            }
        }
    }

    /// <summary>
    /// Gets the latest decoded frame, optionally scaled to the specified width and height. Returns null if no frame is available yet.
    /// </summary>
    public SKImage? GetLatestFrame(int? width = null, int? height = null)
    {
        if (_disposed || !IsOpen)
            return null;

        // Atomic operation to prevent race condition
        ImmutableFrame? frame;
        lock (_frameLock)
        {
            frame = _currentFrame;
            if (frame == null)
                return null;
            frame.AddRef();
        }
        byte[]? rentedBuffer = null;
        try
        {
            int targetWidth = width ?? frame.Width;
            int targetHeight = height ?? frame.Height;
            var imageInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            int bgraStride = targetWidth * 4;
            int bgraSize = targetWidth * targetHeight * 4;
            rentedBuffer = ArrayPool<byte>.Shared.Rent(bgraSize);
            fixed (byte* dstPtr = rentedBuffer)
            {
                byte*[] dstData = new byte*[4];
                int[] dstLinesize = new int[4];
                dstData[0] = dstPtr;
                dstLinesize[0] = bgraStride;
                SwsContext* swsContext = GetCachedSwsContext(
                    frame.Width, frame.Height, (AVPixelFormat)frame.Frame->format,
                    targetWidth, targetHeight);

                if (swsContext == null)
                {
                    Trace.WriteLine("Warning: Failed to create SWS context");
                    return null;
                }

                int scaleResult = ffmpeg.sws_scale(swsContext, frame.Frame->data, frame.Frame->linesize, 0, frame.Height, dstData, dstLinesize);

                if (scaleResult < 0)
                {
                    Trace.WriteLine("Warning: SWS scaling failed");
                    return null;
                }
            }
            var image = SKImage.FromPixelCopy(imageInfo, rentedBuffer, bgraStride);
            return image;
        }
        finally
        {
            if (rentedBuffer != null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            frame.Release(this);
        }
    }

    /// <summary>
    /// Gets the current playback time of the latest frame
    /// </summary>
    public TimeSpan GetCurrentTime()
    {
        var frame = _currentFrame;
        return frame?.Time ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Gets the master clock time for synchronization
    /// Audio clock takes priority if available, otherwise uses video clock
    /// </summary>
    private TimeSpan GetMasterClockTime()
    {
        lock (_syncLock)
        {
            // Use audio as master clock if available and recent
            if (HasAudio && _lastAudioPts > TimeSpan.Zero)
            {
                return _lastAudioPts;
            }

            // Fallback to video clock
            return _lastVideoPts;
        }
    }

    /// <summary>
    /// Calculates sync drift between audio and video
    /// </summary>
    private TimeSpan GetSyncDrift()
    {
        lock (_syncLock)
        {
            if (!HasAudio || _lastAudioPts == TimeSpan.Zero || _lastVideoPts == TimeSpan.Zero)
                return TimeSpan.Zero;

            return _lastAudioPts - _lastVideoPts;
        }
    }

    /// <summary>
    /// Gets the current frame index
    /// </summary>
    public long GetCurrentFrameIndex()
    {
        var frame = _currentFrame;
        return frame?.FrameIndex ?? 0;
    }

    /// <summary>
    /// Gets playback progress as a percentage (0.0 to 1.0)
    /// </summary>
    public double GetProgress()
    {
        if (TotalFrames == 0)
            return 0.0;

        var frame = _currentFrame;
        var currentIndex = frame?.FrameIndex ?? 0;
        return Math.Min(1.0, currentIndex / (double)TotalFrames);
    }

    /// <summary>
    /// Gets or sets the audio volume (0.0 to 1.0)
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            if (value >= 0.0f && value <= 1.0f)
            {
                lock (_audioLock)
                {
                    _volume = value;
                    if (_waveOut != null)
                        _waveOut.Volume = 1.0f; // Always set output device to 1.0
                    if (value > 0.0f)
                        _previousVolume = value;
                }
            }
        }
    }

    /// <summary>
    /// Gets a frame from the pool or allocates a new one if pool is empty
    /// </summary>
    private AVFrame* RentFrameFromPool()
    {
        if (_disposed)
        {
            Trace.WriteLine("Warning: Attempting to rent frame after disposal");
            return null;
        }

        if (_framePool.TryDequeue(out var framePtr))
        {
            var frame = (AVFrame*)framePtr;
            if (frame == null)
            {
                Trace.WriteLine("Critical: Null frame in pool");
                return null;
            }
            // Clear the frame data but keep the structure
            ffmpeg.av_frame_unref(frame);
            return frame;
        }

        // Pool empty, allocate new frame
        Interlocked.Increment(ref _poolAllocations);
        var newFrame = ffmpeg.av_frame_alloc();
        if (newFrame == null)
        {
            Trace.WriteLine("Critical: Failed to allocate frame from pool");
        }
        return newFrame;
    }

    /// <summary>
    /// Returns a frame to the pool for reuse
    /// </summary>
    private void ReturnFrameToPool(AVFrame* frame)
    {
        if (frame == null) return;

        if (_disposed)
        {
            // If disposed, free immediately
            ffmpeg.av_frame_free(&frame);
            return;
        }

        // Only keep frames in pool up to limit
        if (_framePool.Count < MAX_POOL_SIZE)
        {
            ffmpeg.av_frame_unref(frame); // Clear data
            _framePool.Enqueue((IntPtr)frame);
        }
        else
        {
            // Pool full, free the frame
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>
    /// Gets a cached SWS context for the specified parameters, creating/recreating only when needed
    /// </summary>
    private SwsContext* GetCachedSwsContext(int srcWidth, int srcHeight, AVPixelFormat srcFormat, int dstWidth, int dstHeight)
    {
        lock (_swsContextLock)
        {
            // Check if we can reuse the existing context
            if (_cachedSwsContext != null &&
                _lastTargetWidth == dstWidth &&
                _lastTargetHeight == dstHeight &&
                _lastSourceFormat == srcFormat)
            {
                return _cachedSwsContext;
            }

            // Free old context if it exists
            if (_cachedSwsContext != null)
            {
                ffmpeg.sws_freeContext(_cachedSwsContext);
                _cachedSwsContext = null;
            }

            // Create new context
            _cachedSwsContext = ffmpeg.sws_getContext(
                srcWidth, srcHeight, srcFormat,
                dstWidth, dstHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            if (_cachedSwsContext != null)
            {
                _lastTargetWidth = dstWidth;
                _lastTargetHeight = dstHeight;
                _lastSourceFormat = srcFormat;
            }

            return _cachedSwsContext;
        }
    }

    /// <summary>
    /// Software volume scaling for 16-bit PCM stereo audio
    /// </summary>
    /// <param name="buffer">Audio buffer to process</param>
    /// <param name="bytes">Number of bytes to process</param>
    /// <param name="volume">Volume scale factor (0.0 to 1.0)</param>
    private void ApplyVolume(byte[] buffer, int bytes, float volume)
    {
        if (buffer == null || bytes <= 0)
            return;

        if (Math.Abs(volume - 1.0f) < 0.0001f)
            return; // No scaling needed

        // Ensure we don't exceed buffer bounds and process in 2-byte chunks (16-bit samples)
        int maxBytes = Math.Min(bytes, buffer.Length);
        maxBytes = (maxBytes / 2) * 2; // Ensure even number of bytes

        for (int i = 0; i < maxBytes; i += 2)
        {
            // Convert little-endian bytes to 16-bit sample
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));

            // Apply volume scaling with overflow protection
            int scaled = (int)(sample * volume);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);

            // Convert back to little-endian bytes
            buffer[i] = (byte)(scaled & 0xFF);
            buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel operations first
        try
        {
            _cancellationToken?.Cancel();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error cancelling operations: {ex.Message}");
        }

        // Wait for decoding thread to finish
        try
        {
            _decodingThread?.Join(1000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error joining decoding thread: {ex.Message}");
        }

        // Clean up audio resources
        lock (_audioLock)
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveProvider = null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error disposing audio resources: {ex.Message}");
            }
        }

        // Clean up current frame
        try
        {
            var frame = Interlocked.Exchange(ref _currentFrame, null);
            frame?.Release(this);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error releasing current frame: {ex.Message}");
        }

        // Clean up performance optimization resources
        try
        {
            lock (_swsContextLock)
            {
                if (_cachedSwsContext != null)
                {
                    ffmpeg.sws_freeContext(_cachedSwsContext);
                    _cachedSwsContext = null;
                }
            }

            if (_preAllocatedAudioBuffer != null)
            {
                ffmpeg.av_free(_preAllocatedAudioBuffer);
                _preAllocatedAudioBuffer = null;
            }

            // Clean up frame pool
            while (_framePool.TryDequeue(out var pooledFramePtr))
            {
                var pooledFrame = (AVFrame*)pooledFramePtr;
                ffmpeg.av_frame_free(&pooledFrame);
            }

            Trace.WriteLine($"Frame pool statistics: {_poolAllocations} total allocations, pool size was {MAX_POOL_SIZE}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error disposing performance resources: {ex.Message}");
        }

        // Dispose cancellation token
        try
        {
            _cancellationToken?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error disposing cancellation token: {ex.Message}");
        }
    }
}