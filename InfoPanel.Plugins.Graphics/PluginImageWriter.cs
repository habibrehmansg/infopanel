using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace InfoPanel.Plugins.Graphics
{
    /// <summary>
    /// MMF-backed image writer using double buffering.
    ///
    /// MMF layout:
    ///   Offset 0:   4 bytes  — active buffer index (0 or 1)
    ///   Offset 4:   4 bytes  — width
    ///   Offset 8:   4 bytes  — height
    ///   Offset 12:  4 bytes  — reserved
    ///   Offset 16:  W*H*4    — buffer 0 (RGBA pixels)
    ///   Offset 16+B: W*H*4   — buffer 1 (RGBA pixels)
    /// </summary>
    public class PluginImageWriter : IPluginImageWriter
    {
        public SKBitmap Bitmap { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>
        /// Current MMF name. Changes on resize (versioned).
        /// </summary>
        public string MmfName { get; private set; }

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private unsafe byte* _basePtr;
        private int _pixelBufferSize;
        private int _writeIndex;
        private int _version;
        private readonly string _mmfBaseName;
        private readonly object _sync = new();

        private const int HeaderSize = 16;

        /// <summary>Creates or opens a resize's named mapping of the given size; a seam for tests.</summary>
        internal Func<string, long, MemoryMappedFile> OpenMapping { get; init; } = MemoryMappedFile.CreateOrOpen;

        /// <summary>
        /// Raised after a successful resize. Host subscribes to notify the main app.
        /// </summary>
        public event Action<PluginImageWriter>? Resized;

        public PluginImageWriter(string mmfName, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, int width, int height)
        {
            var pixelBufferSize = PixelBufferSize(width, height);
            _mmfBaseName = mmfName;
            MmfName = mmfName;
            _mmf = mmf;
            _accessor = accessor;
            Width = width;
            Height = height;
            _pixelBufferSize = pixelBufferSize;
            _writeIndex = 1; // Start writing to buffer 1, buffer 0 is initially active (empty)

            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _basePtr = ptr;

                // Write header
                *(int*)(_basePtr + 0) = 0;      // Active buffer index = 0
                *(int*)(_basePtr + 4) = width;
                *(int*)(_basePtr + 8) = height;
                *(int*)(_basePtr + 12) = 0;      // Reserved
            }

            // Point bitmap at the write buffer (buffer 1). If that fails the pointer taken above is given back before the
            // failure goes on: the caller disposes the view it passed in, and a pointer still held would keep it mapped.
            try
            {
                Bitmap = CreateBitmapForBuffer(_writeIndex);
            }
            catch
            {
                unsafe
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePtr = null;
                }
                throw;
            }
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                unsafe
                {
                    // Disposed: there is no mapping to write to.
                    if (_basePtr == null) return;

                    // Atomically swap active index to the buffer we just wrote
                    Interlocked.Exchange(ref *(int*)_basePtr, _writeIndex);
                }

                // Switch to the other buffer for the next frame
                _writeIndex = 1 - _writeIndex;
                var old = Bitmap;
                Bitmap = CreateBitmapForBuffer(_writeIndex);
                old?.Dispose();
            }
        }

        public void Resize(int newWidth, int newHeight)
        {
            if (newWidth == Width && newHeight == Height) return;
            if (newWidth <= 0 || newHeight <= 0)
                throw new ArgumentException("Width and height must be positive.");

            var pixelBufferSize = PixelBufferSize(newWidth, newHeight);

            lock (_sync)
            {
                // The new mapping is made first and the old one released only once it is ready: a resize that fails
                // (the system can't map the size) leaves the writer as it was, drawing into its old mapping.
                var version = _version + 1;
                var name = $"{_mmfBaseName}.v{version}";
                var mmfSize = ComputeMmfSize(newWidth, newHeight);
                MemoryMappedFile? mmf = null;
                MemoryMappedViewAccessor? accessor = null;
                var acquired = false;
                unsafe
                {
                    byte* ptr = null;
                    try
                    {
                        mmf = OpenMapping(name, mmfSize);
                        accessor = mmf.CreateViewAccessor(0, mmfSize);
                        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                        acquired = true;
                    }
                    catch
                    {
                        if (acquired) accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
                        accessor?.Dispose();
                        mmf?.Dispose();
                        throw;
                    }

                    *(int*)(ptr + 0) = 0;
                    *(int*)(ptr + 4) = newWidth;
                    *(int*)(ptr + 8) = newHeight;
                    *(int*)(ptr + 12) = 0;

                    // The bitmap over the new mapping is made before the old one is released too: a failure here leaves
                    // the writer as it was, and a later resize to this size is tried again.
                    var oldPtr = _basePtr;
                    var (oldWidth, oldHeight, oldSize, oldIndex) = (Width, Height, _pixelBufferSize, _writeIndex);
                    _basePtr = ptr;
                    (Width, Height, _pixelBufferSize, _writeIndex) = (newWidth, newHeight, pixelBufferSize, 1);
                    SKBitmap bitmap;
                    try
                    {
                        bitmap = CreateBitmapForBuffer(_writeIndex);
                    }
                    catch
                    {
                        _basePtr = oldPtr;
                        (Width, Height, _pixelBufferSize, _writeIndex) = (oldWidth, oldHeight, oldSize, oldIndex);
                        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                        accessor.Dispose();
                        mmf.Dispose();
                        throw;
                    }

                    _basePtr = oldPtr;
                    ReleaseMapping();

                    _mmf = mmf;
                    _accessor = accessor;
                    _basePtr = ptr;
                    Bitmap = bitmap;
                    _version = version;
                    MmfName = name;
                }
            }

            Resized?.Invoke(this);
        }

        /// <summary>Makes the bitmap over one buffer; a seam for tests.</summary>
        internal virtual SKBitmap CreateBitmapForBuffer(int bufferIndex)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap();

            unsafe
            {
                var bufferPtr = (IntPtr)(_basePtr + HeaderSize + (bufferIndex * _pixelBufferSize));
                bitmap.InstallPixels(info, bufferPtr, info.RowBytes);
            }

            return bitmap;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                ReleaseMapping();
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the bitmap, releases the view's pointer (nulled, so nothing writes through it again) and disposes the
        /// view and the mapping. Safe to call twice.
        /// </summary>
        private void ReleaseMapping()
        {
            Bitmap?.Dispose();
            unsafe
            {
                if (_basePtr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePtr = null;
                }
            }
            _accessor.Dispose();
            _mmf.Dispose();
        }

        /// <summary>
        /// One buffer's bytes, RGBA. A negative size, or one whose buffer can't be held in an int (its offsets would wrap),
        /// is refused (<see cref="ArgumentException"/>). The pixel count is compared, not the bytes, so no product can
        /// overflow: any two ints multiply within a long.
        /// </summary>
        private static int PixelBufferSize(int width, int height)
        {
            if (width < 0 || height < 0)
                throw new ArgumentException($"An image of {width}x{height} can't be made: its width and height can't be negative.");
            if ((long)width * height > int.MaxValue / 4)
                throw new ArgumentException($"An image of {width}x{height} is too large: its buffer would pass {int.MaxValue} bytes.");
            return width * height * 4;
        }

        /// <summary>
        /// Computes the total MMF size needed for the given dimensions: the header and two buffers. Throws
        /// <see cref="ArgumentException"/> for a size whose buffer can't be held (negative, or past an int's range of
        /// bytes), so a caller learns it before it maps anything.
        /// </summary>
        public static long ComputeMmfSize(int width, int height)
        {
            return HeaderSize + 2L * PixelBufferSize(width, height); // Header + 2 buffers
        }
    }
}
