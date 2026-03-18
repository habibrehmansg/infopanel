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

        /// <summary>
        /// Raised after a successful resize. Host subscribes to notify the main app.
        /// </summary>
        public event Action<PluginImageWriter>? Resized;

        public PluginImageWriter(string mmfName, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, int width, int height)
        {
            _mmfBaseName = mmfName;
            MmfName = mmfName;
            _mmf = mmf;
            _accessor = accessor;
            Width = width;
            Height = height;
            _pixelBufferSize = width * height * 4;
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

            // Point bitmap at the write buffer (buffer 1)
            Bitmap = CreateBitmapForBuffer(_writeIndex);
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                unsafe
                {
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

            lock (_sync)
            {
                // Dispose old resources
                Bitmap?.Dispose();
                unsafe
                {
                    if (_basePtr != null)
                        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                _accessor.Dispose();
                _mmf.Dispose();

                // Create new MMF with versioned name
                _version++;
                MmfName = $"{_mmfBaseName}.v{_version}";
                Width = newWidth;
                Height = newHeight;
                _pixelBufferSize = newWidth * newHeight * 4;
                _writeIndex = 1;

                var mmfSize = ComputeMmfSize(newWidth, newHeight);
                _mmf = MemoryMappedFile.CreateOrOpen(MmfName, mmfSize);
                _accessor = _mmf.CreateViewAccessor(0, mmfSize);

                unsafe
                {
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _basePtr = ptr;

                    *(int*)(_basePtr + 0) = 0;
                    *(int*)(_basePtr + 4) = newWidth;
                    *(int*)(_basePtr + 8) = newHeight;
                    *(int*)(_basePtr + 12) = 0;
                }

                Bitmap = CreateBitmapForBuffer(_writeIndex);
            }

            Resized?.Invoke(this);
        }

        private SKBitmap CreateBitmapForBuffer(int bufferIndex)
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
                Bitmap?.Dispose();
                unsafe
                {
                    if (_basePtr != null)
                    {
                        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
                _accessor.Dispose();
                _mmf.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Computes the total MMF size needed for the given dimensions.
        /// </summary>
        public static long ComputeMmfSize(int width, int height)
        {
            return HeaderSize + ((long)width * height * 4 * 2); // Header + 2 buffers
        }
    }
}
