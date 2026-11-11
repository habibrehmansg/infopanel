using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace InfoPanel.Output.Gpu;

/// <summary>
/// The system's default Metal device (macOS), a command queue on it and Skia's context over them, reached through the
/// Objective-C runtime by P/Invoke (no binding library). Each draw runs inside an autorelease pool of its own
/// (<see cref="DrawScope"/>): render loops tick on thread-pool threads, which have none, so the objects Metal
/// autoreleases each frame would otherwise pile up unseen.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalDevice : GrContextDevice
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";

    private IntPtr _device;
    private IntPtr _queue;

    private MetalDevice(GRContext context, IntPtr device, IntPtr queue, string name)
        : base(context, GpuRendering.Metal, name)
    {
        _device = device;
        _queue = queue;
    }

    /// <summary>
    /// The default device, a queue and Skia's context. No device (a Mac or virtual machine without Metal):
    /// <see cref="GpuUnavailableException"/> <c>gpu.noDevice</c>; a queue or context that can't be made: an exception
    /// whose message says which.
    /// </summary>
    public static MetalDevice Create()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var device = MTLCreateSystemDefaultDevice();
            if (device == IntPtr.Zero)
                throw new GpuUnavailableException(GpuRendering.NoDevice, "This Mac has no Metal device.");

            var queue = IntPtr.Zero;
            try
            {
                queue = Send(device, sel_registerName("newCommandQueue"));
                if (queue == IntPtr.Zero)
                    throw new InvalidOperationException("Metal made no command queue.");

                var name = Marshal.PtrToStringUTF8(Send(Send(device, sel_registerName("name")), sel_registerName("UTF8String")));
                var context = GRContext.CreateMetal(new GRMtlBackendContext { DeviceHandle = device, QueueHandle = queue })
                    ?? throw new InvalidOperationException("Skia couldn't make a Metal context.");
                return new MetalDevice(context, device, queue, string.IsNullOrWhiteSpace(name) ? "Metal device" : name);
            }
            catch
            {
                Release(queue);
                Release(device);
                throw;
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public override IDisposable DrawScope() => new AutoreleasePool();

    /// <summary>After Skia's context is gone: the queue, then the device.</summary>
    protected override void ReleaseDevice()
    {
        Release(_queue);
        Release(_device);
        _queue = IntPtr.Zero;
        _device = IntPtr.Zero;
    }

    private static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            Send(handle, sel_registerName("release"));
    }

    private sealed class AutoreleasePool : IDisposable
    {
        private IntPtr _pool = objc_autoreleasePoolPush();

        public void Dispose()
        {
            if (_pool == IntPtr.Zero)
                return;
            objc_autoreleasePoolPop(_pool);
            _pool = IntPtr.Zero;
        }
    }

    [DllImport(Metal)]
    private static extern IntPtr MTLCreateSystemDefaultDevice();

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);
}
