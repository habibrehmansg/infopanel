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
        public int Width { get; }
        public int Height { get; }

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly unsafe byte* _basePtr;
        private readonly int _pixelBufferSize;
        private int _writeIndex;

        private const int HeaderSize = 16;

        public PluginImageWriter(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, int width, int height)
        {
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
