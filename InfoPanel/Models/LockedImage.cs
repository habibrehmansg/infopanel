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
        public readonly string ImagePath; // Path to the image file
        private Bitmap?[]? BitmapCache; // Cache for GDI+ bitmaps
        private IntPtr? D2DHandle; // Handle for Direct2D device
        private D2DBitmap?[]? D2DBitmapCache; // Cache for Direct2D bitmaps
        public int Width = 0; // Width of the image
        public int Height = 0; // Height of the image

        public int Frames = 0; // Number of frames in the image (for animations)
        public long TotalFrameTime = 0; // Total time for all frames (for animations)

        private SKCodec? _codec; // SKCodec for decoding image frames
        private FileStream? _fileStream; // FileStream for reading the image file
        private SKBitmap? _compositeBitmap; // Composite bitmap for rendering frames
        private int _lastRenderedFrame = -1; // Index of the last rendered frame
        private long[]? _cumulativeFrameTimes; // Cumulative times for each frame

        private readonly object Lock = new(); // Lock object for thread safety
        private bool IsDisposed = false; // Flag to check if the object is disposed

        private readonly Stopwatch Stopwatch = new(); // Stopwatch for frame timing

        private FileSystemWatcher? _fileWatcher; // Watcher for file changes
        private DateTime _lastModified; // Last modified time of the image file

        public event EventHandler? ImageUpdated; // Event triggered when the image is updated

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;
            _lastModified = File.GetLastWriteTime(imagePath); // Get initial last modified time
            SetupFileWatcher(); // Set up file watcher for changes
            LoadImage(); // Load image initially
        }

        private void SetupFileWatcher()
        {
            if (_fileWatcher == null) // Lazy initialization of file watcher
            {
                _fileWatcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(ImagePath), // Directory of the image
                    Filter = Path.GetFileName(ImagePath), // Filter for the specific image file
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, // Watch for changes in write time and file name
                };
                _fileWatcher.Changed += FileChanged; // Subscribe to the Changed event
                _fileWatcher.EnableRaisingEvents = true; // Enable event raising
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
                        OnImageUpdated(); // Trigger the ImageUpdated event
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
            ImageUpdated?.Invoke(this, EventArgs.Empty); // Invoke the ImageUpdated event
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

                        _codec = SKCodec.Create(_fileStream); // Create codec for image decoding

                        if (_codec != null)
                        {
                            Width = _codec.Info.Width; // Set image width
                            Height = _codec.Info.Height; // Set image height
                            Frames = _codec.FrameCount; // Set number of frames

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
                                    frameDelay = frameDelay == 0 ? 100 : frameDelay; // Default frame delay if not specified

                                    TotalFrameTime += frameDelay;
                                    _cumulativeFrameTimes[i] = TotalFrameTime; // Calculate cumulative frame times
                                }

                                Stopwatch.Restart(); // Restart stopwatch for frame timing
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

            Marshal.Copy(skBitmap.GetPixels(), pixels, 0, byteCount); // Copy pixels from SKBitmap
            Marshal.Copy(pixels, 0, data.Scan0, byteCount); // Copy pixels to Bitmap

            bitmap.UnlockBits(data); // Unlock the bitmap data

            return bitmap;
        }

        private static void ResetCompositeBitmap(SKBitmap bitmap)
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent); // Clear the bitmap to transparent
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
                return 0; // Return 0 if conditions are not met for animation
            }

            var elapsedTime = Stopwatch.ElapsedMilliseconds; // Get elapsed time

            // Reset stopwatch every day (24 hours).
            if (elapsedTime >= 86400000)
            {
                Stopwatch.Restart();
                elapsedTime = 0;
            }

            var elapsedFrameTime = elapsedTime % TotalFrameTime; // Calculate elapsed frame time

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
                _compositeBitmap ??= new SKBitmap(_codec.Info.Width, _codec.Info.Height); // Initialize composite bitmap if null

                if (frame != _lastRenderedFrame)
                {
                    if (_lastRenderedFrame >= frame)
                    {
                        ResetCompositeBitmap(_compositeBitmap); // Reset composite bitmap if needed
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
                                ResetCompositeBitmap(_compositeBitmap); // Reset for specific disposal method
                            }
                        }

                        var options = new SKCodecOptions(i, i - 1);
                        try
                        {
                            _codec.GetPixels(_codec.Info, _compositeBitmap.GetPixels(), options); // Decode frame pixels
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error decoding frame {i}: {ex.Message}");
                        }
                    }

                    _lastRenderedFrame = frame; // Update last rendered frame
                }

                return SKBitmapToBitmap(_compositeBitmap); // Convert to Bitmap
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
                    D2DHandle = handle; // Set handle if null
                }
                else if (D2DHandle != handle)
                {
                    Trace.WriteLine("D2DDevice changed, disposing assets");
                    DisposeD2DAssets(); // Dispose assets if handle changes
                    D2DHandle = handle;
                }

                D2DBitmap? d2dbitmap = null;

                if (_codec != null && D2DBitmapCache != null)
                {
                    var frame = GetCurrentFrameCount(); // Get current frame index
                    d2dbitmap = D2DBitmapCache[frame]; // Retrieve cached bitmap
                    bool dispose = false;
                    try
                    {
                        if (d2dbitmap == null)
                        {
                            if (Frames == 1)
                            {
                                d2dbitmap = device.CreateBitmapFromFile(ImagePath); // Create bitmap from file for single frame
                            }
                            else
                            {
                                using var bitmap = GetBitmapFromSK(frame); // Get bitmap for current frame

                                if (bitmap != null)
                                {
                                    d2dbitmap = device.CreateBitmapFromGDIBitmap(bitmap, true); // Create D2D bitmap from GDI+ bitmap
                                }
                            }

                            if (d2dbitmap != null && cache)
                            {
                                D2DBitmapCache[frame] = d2dbitmap; // Cache the bitmap
                            }
                            else
                            {
                                dispose = true; // Mark for disposal if not cached
                            }
                        }
                        action(d2dbitmap); // Execute the action with the bitmap
                    }
                    finally
                    {
                        if (dispose)
                        {
                            d2dbitmap?.Dispose(); // Dispose if marked
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
                    var frame = GetCurrentFrameCount(); // Get current frame index
                    var bitmap = BitmapCache[frame]; // Retrieve cached bitmap
                    bool dispose = false;

                    try
                    {
                        if (bitmap == null && _codec != null)
                        {
                            bitmap = GetBitmapFromSK(frame); // Get bitmap for current frame

                            if (cache)
                            {
                                BitmapCache[frame] = bitmap; // Cache the bitmap
                            }
                            else
                            {
                                dispose = true; // Mark for disposal if not cached
                            }
                        }

                        access(bitmap); // Execute the action with the bitmap
                    }
                    finally
                    {
                        if (dispose)
                        {
                            bitmap?.Dispose(); // Dispose if marked
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
                            result = new Bitmap(bitmap); // Create a copy if sizes match
                        }
                        else
                        {
                            result = BitmapExtensions.EnsureBitmapSize(bitmap, width, height); // Resize if needed
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
                        D2DBitmapCache[i]?.Dispose(); // Dispose each cached bitmap
                        D2DBitmapCache[i] = null; // Clear the cache
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
                        BitmapCache[i]?.Dispose(); // Dispose each cached bitmap
                        BitmapCache[i] = null; // Clear the cache
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
                    _fileWatcher?.Dispose(); // Dispose the file watcher
                    DisposeAssets(); // Dispose GDI+ assets
                    DisposeD2DAssets(); // Dispose D2D assets
                    _codec?.Dispose(); // Dispose the codec
                    _fileStream?.Dispose(); // Dispose the file stream
                    _compositeBitmap?.Dispose(); // Dispose the composite bitmap
                    IsDisposed = true; // Mark as disposed
                }
            }

            GC.SuppressFinalize(this); // Suppress finalization
        }
    }
}