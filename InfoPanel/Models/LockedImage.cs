using InfoPanel.Extensions;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using unvell.D2DLib;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        public readonly string ImagePath;
        private Bitmap?[]? BitmapCache;
        private SKBitmap?[]? SKBitmapCache;
        private IntPtr? D2DHandle;
        private D2DBitmap?[]? D2DBitmapCache;
        public int Width = 0;
        public int Height = 0;

        public int Frames = 0;
        public long TotalFrameTime = 0;

        private SKCodec? _codec;
        private Stream? _stream;
        private SKBitmap? _compositeBitmap;
        private int _lastRenderedFrame = -1;
        private long[]? _cumulativeFrameTimes;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new();

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;
            LoadImage();
        }

        private void LoadImage()
        {
            if (ImagePath != null)
            {
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
                        _stream = new FileStream(ImagePath, FileMode.Open, FileAccess.Read);
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine(e.Message);
                    }
                }

                if (_stream == null)
                {
                    return;
                }

                lock (Lock)
                {
                    try
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

                            DisposeAssets();
                            BitmapCache = new Bitmap[Frames];

                            DisposeD2DAssets();
                            D2DBitmapCache = new D2DBitmap[Frames];

                            DisposeSKAssets();
                            SKBitmapCache = new SKBitmap[Frames];

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
                    catch { }
                }
            }
        }

        static Bitmap SKBitmapToBitmap(SKBitmap skBitmap)
        {
            // Create a new Bitmap with the same dimensions
            Bitmap bitmap = new(skBitmap.Width, skBitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            // Lock the bitmap's bits
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            // Calculate the number of bytes in the bitmap
            int byteCount = skBitmap.Width * skBitmap.Height * 4; // 4 bytes per pixel for 32bpp

            // Allocate a managed array to hold the pixel data
            byte[] pixels = new byte[byteCount];

            // Copy the pixel data from the unmanaged memory (IntPtr) to the managed array
            Marshal.Copy(skBitmap.GetPixels(), pixels, 0, byteCount);

            // Copy the managed array to the Bitmap's locked bits
            Marshal.Copy(pixels, 0, data.Scan0, byteCount);

            // Unlock the bits
            bitmap.UnlockBits(data);

            return bitmap;
        }

        private SKBitmap? GetSKBitmapFromSK(int frame)
        {
            if (_stream != null && _codec != null)
            {
                _compositeBitmap ??= new SKBitmap(_codec.Info.Width, _codec.Info.Height);

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
                            var r = _codec.GetPixels(_codec.Info, _compositeBitmap.GetPixels(), options);

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

                var result = _compositeBitmap.Copy();

                if (keepCopy != null)
                {
                    _compositeBitmap?.Dispose();
                    _compositeBitmap = keepCopy;
                }

                return result;
            }

            return null;
        }

        private Bitmap? GetBitmapFromSK(int frame)
        {
            if (_stream != null && _codec != null)
            {
                _compositeBitmap ??= new SKBitmap(_codec.Info.Width, _codec.Info.Height);

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
                            }else if(frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious)
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
                            var r = _codec.GetPixels(_codec.Info, _compositeBitmap.GetPixels(), options);

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

                // Convert SKBitmap to System.Drawing.Bitmap
                var result = SKBitmapToBitmap(_compositeBitmap);

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

        public void AccessSK(Action<SKBitmap?> access, bool cache = true)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (_codec != null && SKBitmapCache != null)
                {
                    var frame = GetCurrentFrameCount();
                    var bitmap = SKBitmapCache[frame];
                    var dispose = false;

                    try
                    {
                        if (bitmap == null && _codec != null)
                        {
                            bitmap = GetSKBitmapFromSK(frame);

                            if (cache)
                            {
                                SKBitmapCache[frame] = bitmap;
                            }
                            else
                            {
                                dispose = true;
                            }
                        }

                        access(bitmap);
                    }
                    finally
                    {
                        if (dispose)
                        {
                            bitmap?.Dispose();
                        }
                    }
                }
            }
        }

        public void AccessD2D(D2DDevice device, IntPtr handle, Action<D2DBitmap?> action, bool cache = true)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (D2DHandle == null)
                {
                    D2DHandle = handle;
                }
                else
                if (D2DHandle != handle)
                {
                    Trace.WriteLine("D2DDevice changed, disposing assets");
                    DisposeD2DAssets();
                    D2DHandle = handle;
                }

                D2DBitmap? d2dbitmap = null;

                if (_codec != null && D2DBitmapCache != null)
                {
                    var frame = GetCurrentFrameCount();
                    d2dbitmap = D2DBitmapCache[frame];
                    var dispose = false;
                    try
                    {
                        if (d2dbitmap == null)
                        {
                            if (Frames == 1 && !ImagePath.IsUrl())
                            {
                                d2dbitmap = device.CreateBitmapFromFile(ImagePath);
                            }
                            else
                            {

                                using var bitmap = GetBitmapFromSK(frame);

                                if (bitmap != null)
                                {
                                    d2dbitmap = device.CreateBitmapFromGDIBitmap(bitmap, true);
                                }
                            }

                            if (d2dbitmap != null && cache)
                            {
                                D2DBitmapCache[frame] = d2dbitmap;
                            }
                            else
                            {
                                dispose = true;
                            }
                        }
                        action(d2dbitmap);
                    }
                    finally
                    {
                        if (dispose)
                        {
                            d2dbitmap?.Dispose();
                        }
                    }
                }
            }
        }

        public void Access(Action<Bitmap?> access, bool cache = true)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (_codec != null && BitmapCache != null)
                {
                    var frame = GetCurrentFrameCount();
                    var bitmap = BitmapCache[frame];
                    var dispose = false;

                    try
                    {
                        if (bitmap == null && _codec != null)
                        {
                            bitmap = GetBitmapFromSK(frame);

                            if (cache)
                            {
                                BitmapCache[frame] = bitmap;
                            }
                            else
                            {
                                dispose = true;
                            }
                        }

                        access(bitmap);
                    }
                    finally
                    {
                        if (dispose)
                        {
                            bitmap?.Dispose();
                        }
                    }
                }
            }
        }

        public Bitmap? GetBitmapCopy(int width, int height)
        {
            Bitmap? result = null;

            Access((bitmap) =>
            {
                if (bitmap != null)
                {
                    if (bitmap.Width == width && bitmap.Height == height)
                    {
                        result = new Bitmap(bitmap);
                    }
                    else
                    {
                        result = BitmapExtensions.EnsureBitmapSize(bitmap, width, height);
                    }
                }
            }, false);

            return result;
        }

        public void DisposeSKAssets()
        {
            lock(Lock)
            {
                if(SKBitmapCache != null)
                {
                    for(int i = 0; i< SKBitmapCache.Length; i++)
                    {
                        SKBitmapCache[i]?.Dispose();
                        SKBitmapCache[i] = null;
                    }
                }
            }
        }

        public void DisposeD2DAssets()
        {
            lock (Lock)
            {
                if (D2DBitmapCache != null)
                {
                    for (int i = 0; i < D2DBitmapCache.Length; i++)
                    {
                        D2DBitmapCache[i]?.Dispose();
                        D2DBitmapCache[i] = null;
                    }
                }
            }
        }

        public void DisposeAssets()
        {
            lock (Lock)
            {
                if (BitmapCache != null)
                {
                    for (int i = 0; i < BitmapCache.Length; i++)
                    {
                        BitmapCache[i]?.Dispose();
                        BitmapCache[i] = null;
                    }
                }
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
                    DisposeAssets();
                    DisposeD2DAssets();
                    _codec?.Dispose();
                    _stream?.Dispose();
                    _compositeBitmap?.Dispose();
                    IsDisposed = true;
                }
            }

            GC.SuppressFinalize(this);
        }
    }


}
