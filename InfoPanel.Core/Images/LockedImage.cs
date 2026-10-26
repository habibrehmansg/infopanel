using InfoPanel.Images;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Svg.Skia;
using System;
using Serilog;
using System.IO;
using System.Text;
using System.Threading;

namespace InfoPanel.Models
{
    /// <summary>
    /// A decoded local image file: a raster (PNG, JPEG, animated GIF/WebP, ...) or an SVG.
    /// Video, HTTP and plugin images are not handled here (see PORTING.md).
    /// </summary>
    public partial class LockedImage : IRenderImage, IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<LockedImage>();
        
        public enum ImageType
        {
            SK, SVG
        }

        public readonly string ImagePath;

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKImageMemoryCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });

        public int Width { get; private set; }

        public int Height { get; private set; }

        public readonly ImageType Type;

        public bool IsVector => Type == ImageType.SVG;

        private readonly SKSvg? SKSvg;

        public readonly long Frames;
        public readonly long TotalFrameTime;

        private readonly SKCodec? _codec;
        private readonly Stream? _stream;
        private SKBitmap? _compositeBitmap;
        private readonly long[]? _cumulativeFrameTimes;
        private int _lastRenderedFrame = -1;

        private readonly object Lock = new();
        private const int AccessLockTimeoutMs = 50;
        internal bool IsDisposed { get; private set; }

        public bool Loaded { get; private set; } = false;

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;

            try
            {
                if (File.Exists(ImagePath))
                {
                    try
                    {
                        var fileStream = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        _stream = new MemoryStream();
                        fileStream.CopyTo(_stream);
                        fileStream.Dispose();
                        _stream.Position = 0;

                        Logger.Debug("Image loaded from file: {ImagePath}", ImagePath);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Error loading image from file");
                    }
                }


                if (_stream == null)
                {
                    throw new ArgumentException("Image path is invalid or file does not exist.", nameof(imagePath));
                }

                if (IsSvgContent(_stream))
                {
                    Type = ImageType.SVG;

                    SKSvg = new SKSvg();
                    SKSvg.Load(_stream);

                    if (SKSvg.Picture is SKPicture picture)
                    {
                        Width = (int)picture.CullRect.Width;
                        Height = (int)picture.CullRect.Height;
                        Frames = 1;
                    }
                }
                else
                {
                    _codec?.Dispose();
                    _codec = SKCodec.Create(_stream);

                    if (_codec == null)
                    {
                        Log.Error("Failed to create SKCodec for {ImagePath}", ImagePath);
                        throw new ArgumentException("Unsupported image format or codec creation failed.", nameof(imagePath));
                    }

                    Width = _codec.Info.Width;
                    Height = _codec.Info.Height;

                    Frames = _codec.FrameCount;

                    //ensure at least 1 frame
                    if (Frames == 0)
                    {
                        Frames = 1;
                    }

                    _cumulativeFrameTimes = new long[Frames];

                    if (Frames > 1)
                    {
                        for (int i = 0; i < Frames; i++)
                        {
                            var frameDelay = _codec.FrameInfo[i].Duration;

                            if (frameDelay == 0)
                            {
                                frameDelay = 100;
                            }

                            TotalFrameTime += frameDelay;
                            _cumulativeFrameTimes[i] = TotalFrameTime;
                        }
                    }
                }

                Loaded = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Error initializing LockedImage for {ImagePath}", ImagePath);
            }
        }

        private static bool IsSvgContent(Stream stream)
        {
            if (stream.Length < 512)
            {
                return false;
            }

            var buffer = new byte[512];
            stream.ReadExactly(buffer, 0, buffer.Length);
            stream.Position = 0;

            // Check for SVG markers in the first bytes
            var text = Encoding.UTF8.GetString(buffer);
            return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("svg", StringComparison.OrdinalIgnoreCase);
        }

        private SKBitmap? GetSKBitmapFromSK(int frame)
        {
            if (_stream != null && _codec != null)
            {
                var info = _codec.Info;
                _compositeBitmap ??= new SKBitmap(info);

                SKBitmap? keepCopy = null;

                if (frame != _lastRenderedFrame)
                {
                    if (_lastRenderedFrame >= frame)
                    {
                        ResetCompositeBitmap(_compositeBitmap);
                        _lastRenderedFrame = -1;
                    }

                    for (int i = _lastRenderedFrame + 1; i <= frame; i++)
                    {

                        SKCodecFrameInfo? frameInfo = null;
                        if (_codec.FrameCount > 0)
                        {
                            frameInfo = _codec.FrameInfo[i];
                            if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestoreBackgroundColor)
                            {
                                ResetCompositeBitmap(_compositeBitmap);
                            }
                            else if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious)
                            {
                                keepCopy?.Dispose();
                                keepCopy = _compositeBitmap.Copy();
                            }
                        }

                        //var options = new SKCodecOptions(i, i > 0 ? i - 1 : 0);
                        var requiredFrame = frameInfo?.RequiredFrame ?? (i > 0 ? i - 1 : 0);

                        var options = new SKCodecOptions(i, requiredFrame);
                        try
                        {
                            var r = _codec.GetPixels(info, _compositeBitmap.GetPixels(), options);

                            if (r != SKCodecResult.Success)
                            {
                                Log.Error("SKCodec error: {Result} at frame i={FrameIndex}", r, i);
                                return null;
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Error getting pixels from codec at frame {FrameIndex}", i);
                        }
                    }

                    _lastRenderedFrame = frame;
                }

                var result = _compositeBitmap.Copy(SKColorType.Bgra8888);

                if (keepCopy != null)
                {
                    _compositeBitmap?.Dispose();
                    _compositeBitmap = keepCopy;
                }

                return result;
            }

            return null;
        }


        private static void ResetCompositeBitmap(SKBitmap bitmap)
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
        }

        /// <summary>
        /// The animation frame shown at <paramref name="elapsed"/>; frames loop over <see cref="TotalFrameTime"/>.
        /// </summary>
        private int GetFrameAt(TimeSpan elapsed)
        {
            if (_codec == null || _cumulativeFrameTimes == null || Frames <= 1 || TotalFrameTime == 0)
            {
                return 0;
            }

            var elapsedTime = Math.Max(0, (long)elapsed.TotalMilliseconds);
            var elapsedFrameTime = elapsedTime % TotalFrameTime;

            // Use binary search to find the current frame index
            int index = Array.BinarySearch(_cumulativeFrameTimes, elapsedFrameTime);

            // BinarySearch returns a negative value if the exact value isn't found.
            if (index < 0)
            {
                index = ~index;
            }

            // Handle wrapping around if needed
            if (index >= _cumulativeFrameTimes.Length)
            {
                index = 0; // Wrap to the first frame
            }

            return index;
        }

        public void AccessVector(Action<SKPicture> draw)
        {
            if (IsDisposed || !Loaded)
            {
                return;
            }

            if (!Monitor.TryEnter(Lock, AccessLockTimeoutMs))
            {
                return;
            }

            try
            {
                if (!IsDisposed && SKSvg?.Picture is SKPicture picture)
                {
                    draw(picture);
                }
            }
            finally
            {
                Monitor.Exit(Lock);
            }
        }

        private SKImageFrameSlot[] GetSKBitmapFrameCache()
        {
            const string cacheKey = "default";

            SKImageMemoryCache.TryGetValue(cacheKey, out var cacheValue);
            if (cacheValue == null)
            {
                cacheValue = new SKImageFrameSlot[Frames];
                for (int i = 0; i < Frames; i++)
                {
                    cacheValue[i] = new SKImageFrameSlot();
                }

                SKImageMemoryCache.Set(cacheKey, cacheValue, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromSeconds(5),
                    PostEvictionCallbacks = {
                        new PostEvictionCallbackRegistration
                        {
                            EvictionCallback = (key, value, reason, state) =>
                            {
                                Log.Debug("Cache entry '{Key}' evicted due to {Reason}.", key, reason);
                                if (value is SKImageFrameSlot[] slots)
                                {
                                    foreach (var slot in slots)
                                    {
                                        slot.Dispose();
                                    }
                                }
                            }
                        }
                    }
                });
            }

            return cacheValue;
        }

        public void AccessRaster(int width, int height, TimeSpan elapsed, Action<SKImage> draw)
        {
            if (IsDisposed || !Loaded)
            {
                return;
            }

            if (width <= 0 || height <= 0)
                return;

            if (!Monitor.TryEnter(Lock, AccessLockTimeoutMs))
            {
                return;
            }

            try
            {
                if (IsDisposed)
                {
                    return;
                }

                var frameCache = GetSKBitmapFrameCache();

                var frame = GetFrameAt(elapsed);

                var bitmapFrame = frameCache[frame];

                if (bitmapFrame.Width != width || bitmapFrame.Height != height)
                {
                    bitmapFrame.Invalidate();
                }

                if (bitmapFrame.Image == null)
                {
                    using var bitmap = GetSKBitmapFromSK(frame);
                    using var resizedBitmap = bitmap?.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));

                    if (resizedBitmap != null)
                    {
                        bitmapFrame.Image = SKImage.FromBitmap(resizedBitmap);
                    }
                }

                if (bitmapFrame.Image != null)
                {
                    draw(bitmapFrame.Image);
                }
            }
            finally
            {
                Monitor.Exit(Lock);
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            lock (Lock)
            {
                if (!IsDisposed)
                {
                    SKImageMemoryCache.Dispose();

                    SKSvg?.Dispose();

                    _codec?.Dispose();
                    _stream?.Dispose();
                    _compositeBitmap?.Dispose();

                    IsDisposed = true;
                    Log.Debug("LockedImage {ImagePath} disposed.", ImagePath);
                }
            }

            GC.SuppressFinalize(this);
        }

        public class SKImageFrameSlot : IDisposable
        {
            private volatile SKImage? _bitmap;
            private int _disposed = 0;

            public SKImage? Image
            {
                get => _bitmap;
                set
                {
                    if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
                        return; // Already disposed

                    var oldBitmap = Interlocked.Exchange(ref _bitmap, value);
                    DisposeImage(oldBitmap);
                }
            }

            public int Width => _bitmap?.Width ?? 0;
            public int Height => _bitmap?.Height ?? 0;

            private static void DisposeImage(SKImage? image)
            {
                if (image == null) return;

                try
                {
                    image.Dispose();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error disposing SKImage");
                }
            }

            public void Invalidate()
            {
                Image = null; // Uses the setter logic
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                    return; // Already disposed

                var oldBitmap = Interlocked.Exchange(ref _bitmap, null);
                DisposeImage(oldBitmap);
                GC.SuppressFinalize(this);
            }
        }

    }
}
