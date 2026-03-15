using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using InfoPanel.Plugins.Ipc;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Monitors.PluginProxies
{
    /// <summary>
    /// Read-only consumer of a plugin image via shared memory (MMF).
    /// Opens the MMF created by the host process and reads the active buffer at render time.
    /// </summary>
    internal class ProxyPluginImage : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ProxyPluginImage>();

        public string PluginId { get; }
        public string ImageId { get; }
        public string ImageName { get; }
        public int Width { get; }
        public int Height { get; }

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private unsafe byte* _basePtr;
        private readonly int _pixelBufferSize;
        private bool _disposed;

        private const int HeaderSize = 16;

        public ProxyPluginImage(string pluginId, ImageDescriptorDto descriptor)
        {
            PluginId = pluginId;
            ImageId = descriptor.Id;
            ImageName = descriptor.Name;
            Width = descriptor.Width;
            Height = descriptor.Height;
            _pixelBufferSize = Width * Height * 4;

            try
            {
                _mmf = MemoryMappedFile.OpenExisting(descriptor.MmfName, MemoryMappedFileRights.Read);
                _accessor = _mmf.CreateViewAccessor(0, descriptor.BufferSize, MemoryMappedFileAccess.Read);

                unsafe
                {
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _basePtr = ptr;
                }

                Logger.Information("Opened MMF {MmfName} for plugin image {PluginId}/{ImageId}",
                    descriptor.MmfName, pluginId, descriptor.Id);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open MMF {MmfName} for plugin image {PluginId}/{ImageId}",
                    descriptor.MmfName, pluginId, descriptor.Id);
            }
        }

        /// <summary>
        /// Copies the active buffer from shared memory into a self-contained SKImage.
        /// The returned image is safe to use and dispose independently of buffer swaps.
        /// </summary>
        public SKImage? GetCurrentFrame()
        {
            unsafe
            {
                if (_basePtr == null) return null;

                int activeIndex = Volatile.Read(ref *(int*)_basePtr);
                int w = *(int*)(_basePtr + 4);
                int h = *(int*)(_basePtr + 8);
                if (w == 0 || h == 0) return null;

                var offset = HeaderSize + (activeIndex * _pixelBufferSize);
                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                return SKImage.FromPixelCopy(info, (IntPtr)(_basePtr + offset), info.RowBytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            unsafe
            {
                if (_basePtr != null && _accessor != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePtr = null;
                }
            }

            _accessor?.Dispose();
            _accessor = null;
            _mmf?.Dispose();
            _mmf = null;

            GC.SuppressFinalize(this);
        }
    }
}
