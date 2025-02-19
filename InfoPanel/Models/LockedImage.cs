using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms; // Assuming WinForms, adjust for other frameworks
using InfoPanel.Extensions;
using SkiaSharp;
using unvell.D2DLib;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        public readonly string ImagePath;
        private Bitmap?[]? BitmapCache;
        private IntPtr? D2DHandle;
        private D2DBitmap?[]? D2DBitmapCache;
        public int Width = 0;
        public int Height = 0;

        public int Frames = 0;
        public long TotalFrameTime = 0;

        private SKCodec? _codec;
        private FileStream? _fileStream;
        private SKBitmap? _compositeBitmap;
        private int _lastRenderedFrame = -1;
        private long[]? _cumulativeFrameTimes;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new();

        private FileSystemWatcher? _fileWatcher; // Make nullable for lazy initialization
        private DateTime _lastModified;

        public event EventHandler? ImageUpdated;

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;
            _lastModified = File.GetLastWriteTime(imagePath);
            SetupFileWatcher();
            LoadImage(); // Load image initially
        }

        private void SetupFileWatcher()
        {
            if (_fileWatcher == null) // Lazy initialization
            {
                _fileWatcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(ImagePath),
                    Filter = Path.GetFileName(ImagePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                };
                _fileWatcher.Changed += FileChanged;
                _fileWatcher.EnableRaisingEvents = true;
            }
        }

        private void FileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
                return;

            Thread.Sleep(250); // Small delay to ensure the file write is complete

            lock (Lock)
            {
                try
                {
                    var newLastModified = File.GetLastWriteTime(ImagePath);
                    if (newLastModified != _lastModified)
                    {
                        _lastModified = newLastModified;
                        // Clear all resources before reloading
                        DisposeAssets();
                        DisposeD2DAssets();
                        _codec?.Dispose();
                        _fileStream?.Dispose();
                        _compositeBitmap?.Dispose();
                        _compositeBitmap = null;
                        _lastRenderedFrame = -1;
                        _cumulativeFrameTimes = null;
                        LoadImage(); // Reload image to refresh all states
                        OnImageUpdated();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error updating image: {ex.Message}");
                }
            }
        }

        protected virtual void OnImageUpdated()
        {
            ImageUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void LoadImage()
        {
            if (ImagePath != null && File.Exists(ImagePath))
            {
                lock (Lock)
                {
                    try
                    {
                        // Dispose resources before reinitializing
                        DisposeAssets();
                        DisposeD2DAssets();
                        _codec?.Dispose();
                        _fileStream?.Dispose();

                        _fileStream = new FileStream(
                            ImagePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite
                        );

                        _codec = SKCodec.Create(_fileStream);

                        if (_codec != null)
                        {
                            Width = _codec.Info.Width;
                            Height = _codec.Info.Height;
                            Frames = _codec.FrameCount;

                            // Ensure at least 1 frame
                            if (Frames == 0)
                            {
                                Frames = 1;
                            }

                            // Initialize caches
                            BitmapCache = new Bitmap[Frames];
                            D2DBitmapCache = new D2DBitmap[Frames];
                            _cumulativeFrameTimes = new long[Frames];

                            if (Frames > 1)
                            {
                                TotalFrameTime = 0;
                                for (int i = 0; i < Frames; i++)
                                {
                                    var frameDelay = _codec.FrameInfo[i].Duration;
                                    frameDelay = frameDelay == 0 ? 100 : frameDelay;

                                    TotalFrameTime += frameDelay;
                                    _cumulativeFrameTimes[i] = TotalFrameTime;
                                }

                                Stopwatch.Restart();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error loading image: {ex.Message}");
                    }
                }
            }
        }

        private static Bitmap SKBitmapToBitmap(SKBitmap skBitmap)
        {
            Bitmap bitmap = new(
                skBitmap.Width,
                skBitmap.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb
            );

            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb
            );

            int byteCount = skBitmap.Width * skBitmap.Height * 4;
            byte[] pixels = new byte[byteCount];

            Marshal.Copy(skBitmap.GetPixels(), pixels, 0, byteCount);
            Marshal.Copy(pixels, 0, data.Scan0, byteCount);

            bitmap.UnlockBits(data);

            return bitmap;
        }

        private static void ResetCompositeBitmap(SKBitmap bitmap)
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
        }

        private int GetCurrentFrameCount()
        {
            if (
                _codec == null
                || _cumulativeFrameTimes == null
                || Frames <= 1
                || TotalFrameTime == 0
            )
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

        private Bitmap? GetBitmapFromSK(int frame)
        {
            if (_fileStream != null && _codec != null)
            {
                _compositeBitmap ??= new SKBitmap(_codec.Info.Width, _codec.Info.Height);

                if (frame != _lastRenderedFrame)
                {
                    if (_lastRenderedFrame >= frame)
                    {
                        ResetCompositeBitmap(_compositeBitmap);
                        _lastRenderedFrame = -1;
                    }

                    for (int i = _lastRenderedFrame + 1; i <= frame; i++)
                    {
                        if (_codec.FrameCount > 0)
                        {
                            var frameInfo = _codec.FrameInfo[i];
                            if (
                                frameInfo.DisposalMethod
                                == SKCodecAnimationDisposalMethod.RestoreBackgroundColor
                            )
                            {
                                ResetCompositeBitmap(_compositeBitmap);
                            }
                        }

                        var options = new SKCodecOptions(i, i - 1);
                        try
                        {
                            _codec.GetPixels(_codec.Info, _compositeBitmap.GetPixels(), options);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error decoding frame {i}: {ex.Message}");
                        }
                    }

                    _lastRenderedFrame = frame;
                }

                return SKBitmapToBitmap(_compositeBitmap);
            }

            return null;
        }

        public void AccessD2D(
            D2DDevice device,
            IntPtr handle,
            Action<D2DBitmap?> action,
            bool cache = true
        )
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
                else if (D2DHandle != handle)
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
                    bool dispose = false;
                    try
                    {
                        if (d2dbitmap == null)
                        {
                            if (Frames == 1)
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

            // Lock only the critical section
            lock (Lock)
            {
                if (_codec != null && BitmapCache != null)
                {
                    var frame = GetCurrentFrameCount();
                    var bitmap = BitmapCache[frame];
                    bool dispose = false;

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

            Access(
                (bitmap) =>
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
                },
                false
            );

            return result;
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
                    _fileWatcher?.Dispose();
                    DisposeAssets();
                    DisposeD2DAssets();
                    _codec?.Dispose();
                    _fileStream?.Dispose();
                    _compositeBitmap?.Dispose();
                    IsDisposed = true;
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
