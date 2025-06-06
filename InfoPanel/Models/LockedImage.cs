using InfoPanel.Extensions;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using unvell.D2DLib;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        public readonly string ImagePath;

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKImageMemoryCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKGLImageMemoryCache = new();

        public int Width { get; private set; } = 0;
        public int Height { get; private set; } = 0;

        public bool IsSvg { get; private set; } = false;
        private readonly SKSvg? SKSvg;

        public readonly int Frames;
        public readonly long TotalFrameTime;

        private readonly SKCodec? _codec;
        private readonly Stream? _stream;
        private SKBitmap? _compositeBitmap;
        private readonly long[]? _cumulativeFrameTimes;
        private int _lastRenderedFrame = -1;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new();

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;
            if (ImagePath.IsUrl())
            {
                using HttpClient client = new();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");

                try
                {
                    var data = client.GetByteArrayAsync(ImagePath).GetAwaiter().GetResult();
                    _stream = new MemoryStream(data);
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }
            else if (File.Exists(ImagePath))
            {
                try
                {
                    var fileStream = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    _stream = new MemoryStream();
                    fileStream.CopyTo(_stream);
                    fileStream.Dispose();
                    _stream.Position = 0;

                    Trace.WriteLine($"Image loaded from file: {ImagePath}");
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }

            if (_stream == null)
            {
                throw new ArgumentException("Image path is invalid or file does not exist.", nameof(imagePath));
            }

            lock (Lock)
            {
                try
                {
                    IsSvg = IsSvgContent(_stream);

                    if (IsSvg)
                    {
                        SKSvg = new SKSvg();
                        SKSvg.Load(_stream);

                        if (SKSvg.Picture is SKPicture picture)
                        {
                            Width = (int)picture.CullRect.Width;
                            Height = (int)picture.CullRect.Height;
                            Frames = 1;

                            DisposeD2DAssets();
                        }
                    }
                    else
                    {
                        _codec?.Dispose();
                        _codec = SKCodec.Create(_stream);

                        if (_codec != null)
                        {
                            Width = _codec.Info.Width;
                            Height = _codec.Info.Height;

                            Frames = _codec.FrameCount;

                            //ensure at least 1 frame
                            if (Frames == 0)
                            {
                                Frames = 1;
                            }

                            DisposeD2DAssets();

                            DisposeSKAssets();

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

                                //start the stopwatch
                                Stopwatch.Start();
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static bool IsSvgContent(Stream stream)
        {
            if (stream.Length < 512)
            {
                return false;
            }

            var buffer = new byte[512];
            stream.Read(buffer, 0, buffer.Length);
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
                                Trace.WriteLine(r + $" i={i}");
                                return null;
                            }
                        }
                        catch (Exception e)
                        {
                            Trace.WriteLine(e.Message);
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

        private int GetCurrentFrameCount()
        {
            if (_codec == null || _cumulativeFrameTimes == null || Frames <= 1 || TotalFrameTime == 0)
            {
                return 0;
            }

            var elapsedTime = Stopwatch.ElapsedMilliseconds;

            // Reset stopwatch every day (24 hours).
            if (elapsedTime >= 86400000)
            {
                Stopwatch.Restart();
                elapsedTime = 0;
            }

            var elapsedFrameTime = elapsedTime % TotalFrameTime;

            // Use binary search to find the current frame index
            int index = Array.BinarySearch(_cumulativeFrameTimes, (int)elapsedFrameTime);

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

        public void AccessSVG(Action<SKPicture> access)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (SKSvg?.Picture is SKPicture picture)
                {
                    access(picture);
                }
            }
        }

        private SKImageFrameSlot[] GetSKBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKImageMemoryCache.Set(cacheHint, cacheValue, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(5), PostEvictionCallbacks = {
                            new PostEvictionCallbackRegistration
                            {
                                EvictionCallback = (key, value, reason, state) =>
                                {
                                    Trace.WriteLine($"Cache entry '{key}' evicted due to {reason}.");
                                    if (value is SKImageFrameSlot[] slots)
                                    {
                                        foreach (var slot in slots)
                                        {
                                            slot.Dispose();
                                        }
                                    }
                                }
                            }
                        } });
                }

                return cacheValue;
            }
        }

        public SKImageFrameSlot[] GetD2DBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKGLImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKGLImageMemoryCache.Set(cacheHint, cacheValue);
                }

                return cacheValue;
            }
        }

        public void AccessSK(int targetWidth, int targetHeight, Action<SKImage> access, bool cache = true, string cacheHint = "default", GRContext? grContext = null)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("Target dimensions must be positive");

            lock (Lock)
            {
                var SKBitmapCache = grContext != null ? GetD2DBitmapFrameCache(cacheHint) : GetSKBitmapFrameCache(cacheHint);

                var frame = GetCurrentFrameCount();

                var bitmapFrame = SKBitmapCache[frame];

                if (cache && (bitmapFrame.Width != targetWidth || bitmapFrame.Height != targetHeight))
                {
                    bitmapFrame.Invalidate();
                }

                if(bitmapFrame.Image != null && grContext != null && !bitmapFrame.Image.IsValid(grContext))
                {
                    bitmapFrame.Invalidate();
                }

                var shouldDispose = false;

                if (bitmapFrame.Image == null)
                {
                    using var bitmap = GetSKBitmapFromSK(frame);
                    using var resizedBitmap = bitmap?.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);

                    if (grContext != null && resizedBitmap != null)
                    {
                        using var image = SKImage.FromBitmap(resizedBitmap);
                        bitmapFrame.Image = image.ToTextureImage(grContext);
                    }
                    else
                    {
                        bitmapFrame.Image = SKImage.FromBitmap(resizedBitmap);
                    }

                    if (grContext == null && !cache)
                    {
                        shouldDispose = true;
                    }
                }

                if (bitmapFrame.Image != null)
                {
                    access(bitmapFrame.Image);

                    if (shouldDispose)
                    {
                        bitmapFrame.Invalidate();
                    }
                }
            }
        }

        public void AccessD2D(D2DDevice device, IntPtr handle, int targetWidth, int targetHeight, Action<D2DBitmap> access, bool cache = true, string cacheHint = "default")
        {
            //ObjectDisposedException.ThrowIf(IsDisposed, this);
            //ArgumentNullException.ThrowIfNull(access);

            //if (targetWidth <= 0 || targetHeight <= 0)
            //    throw new ArgumentException("Target dimensions must be positive");

            //lock (Lock)
            //{

            //    if (D2DHandle == null)
            //    {
            //        D2DHandle = handle;
            //    }
            //    else if (D2DHandle != handle)
            //    {
            //        Trace.WriteLine("D2DDevice changed, disposing assets");
            //        DisposeD2DAssets();
            //        D2DHandle = handle;
            //    }

            //    var D2DBitmapCache = GetD2DBitmapFrameCache(cacheHint);

            //    var frame = GetCurrentFrameCount();
            //    var d2dbitmapFrame = D2DBitmapCache[frame];

            //    if (d2dbitmapFrame.Width != targetWidth || d2dbitmapFrame.Height != targetHeight)
            //    {
            //        d2dbitmapFrame.Invalidate();
            //    }

            //    if (d2dbitmapFrame.Bitmap == null)
            //    {
            //        SKBitmap? bitmap = null;

            //        if (IsSvg && SKSvg?.Picture is SKPicture picture)
            //        {
            //            var bounds = picture.CullRect;

            //            float scaleX = targetWidth / bounds.Width;
            //            float scaleY = targetHeight / bounds.Height;

            //            bitmap = picture.ToBitmap(
            //               background: SKColors.Transparent,
            //               scaleX,
            //               scaleY,
            //               skColorType: SKColorType.Bgra8888,
            //               skAlphaType: SKAlphaType.Premul,
            //               skColorSpace: SKColorSpace.CreateSrgb()
            //            );
            //        }
            //        else if (_codec != null)
            //        {
            //            var b = GetSKBitmapFromSK(frame);
            //            bitmap = b?.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
            //            b?.Dispose();
            //        }

            //        if (bitmap != null)
            //        {
            //            uint width = (uint)bitmap.Width;
            //            uint height = (uint)bitmap.Height;
            //            uint stride = (uint)bitmap.RowBytes;
            //            IntPtr pixelPtr = bitmap.GetPixels();
            //            uint length = stride * height;

            //            d2dbitmapFrame.Bitmap = device.CreateBitmapFromMemory(width, height, stride, pixelPtr, 0, length);
            //        }
            //    }

            //    if (d2dbitmapFrame.Bitmap != null)
            //    {
            //        access(d2dbitmapFrame.Bitmap);
            //    }
            //}
        }

        public void DisposeSKAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKImageMemoryCache.Keys)
                {
                    Trace.WriteLine($"Clearing SKImageMemoryCache[{key}]");
                }
                SKImageMemoryCache.Clear();
            }
        }

        public void DisposeD2DAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKGLImageMemoryCache.Keys)
                {
                    Trace.WriteLine($"Clearing SKGLImageMemoryCache[{key}]");
                }
                SKGLImageMemoryCache.Clear();
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
                    SKGLImageMemoryCache.Dispose();

                    SKSvg?.Dispose();

                    _codec?.Dispose();
                    _stream?.Dispose();
                    _compositeBitmap?.Dispose();

                    Stopwatch.Stop();
                    IsDisposed = true;
                    Trace.WriteLine($"LockedImage {ImagePath} disposed.");
                }
            }

            GC.SuppressFinalize(this);
        }

        public class SKImageFrameSlot : IDisposable
        {
            private SKImage? _bitmap;
            public SKImage? Image
            {
                get => _bitmap;
                set
                {
                    Invalidate();
                    _bitmap = value;
                }
            }

            public int Width => _bitmap?.Width ?? 0;
            public int Height => _bitmap?.Height ?? 0;

            public void Invalidate()
            {
                if (_bitmap == null)
                    return;

                try
                {
                    _bitmap?.Dispose();
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Error invalidating SKImageFrameSlot: {e.Message}");
                }

                _bitmap = null;
            }

            public void Dispose()
            {
                Invalidate();
                GC.SuppressFinalize(this);
            }
        }
    }
}
