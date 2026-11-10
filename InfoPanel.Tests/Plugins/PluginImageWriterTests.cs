using System.IO.MemoryMappedFiles;
using InfoPanel.Plugins.Graphics;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A plugin image's writer draws into shared memory through a pointer to its mapping. A resize that fails (the system can't
// map the new size, or the size can't be held) leaves the writer as it was: the old mapping stays mapped and the next
// frame is drawn and published into it, never through a pointer to memory already released. Each test gives the writer an
// unnamed mapping of its own and makes a resize's new mapping fail through its seam.
public class PluginImageWriterTests
{
    private const int Width = 4;
    private const int Height = 2;
    private const int HeaderSize = 16;

    [Fact]
    public void A_resize_whose_mapping_fails_keeps_the_old_image_and_the_next_frame_draws_into_it()
    {
        var size = PluginImageWriter.ComputeMmfSize(Width, Height);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);
        // Disposed at the end, not by using: a failed test's writer may hold a released view, which its Dispose would trip on.
        var writer = new PluginImageWriter("test-image", mmf, view, Width, Height)
        {
            OpenMapping = (_, _) => throw new IOException("Not enough memory to map the new size."),
        };

        Assert.Throws<IOException>(() => writer.Resize(8, 4));

        Assert.Equal((Width, Height, "test-image"), (writer.Width, writer.Height, writer.MmfName));
        Assert.False(view.SafeMemoryMappedViewHandle.IsClosed, "the old view was released");

        writer.Bitmap.Erase(SKColors.Red);
        writer.Invalidate();

        // Read through a view of its own: buffer 1, the first one drawn, is now the active one, and holds the frame.
        using var check = mmf.CreateViewAccessor(0, size);
        Assert.Equal(1, check.ReadInt32(0));
        var pixel = HeaderSize + Width * Height * 4;
        Assert.Equal((byte)255, check.ReadByte(pixel));     // red
        Assert.Equal((byte)0, check.ReadByte(pixel + 1));   // green
        Assert.Equal((byte)255, check.ReadByte(pixel + 3)); // alpha
        writer.Dispose();
    }

    // A guard for the above: a resize whose mapping succeeds still moves the writer to it (passes before and after the fix).
    [Fact]
    public void A_resize_whose_mapping_succeeds_moves_to_the_new_mapping_and_says_so()
    {
        var size = PluginImageWriter.ComputeMmfSize(Width, Height);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);
        MemoryMappedFile? made = null;
        var writer = new PluginImageWriter("test-image", mmf, view, Width, Height)
        {
            OpenMapping = (_, bytes) => made = MemoryMappedFile.CreateNew(null, bytes),
        };
        var resized = 0;
        writer.Resized += _ => resized++;

        writer.Resize(8, 4);
        writer.Bitmap.Erase(SKColors.Red);
        writer.Invalidate();

        Assert.Equal((8, 4, "test-image.v1", 1), (writer.Width, writer.Height, writer.MmfName, resized));
        Assert.True(view.SafeMemoryMappedViewHandle.IsClosed, "the old view is released");
        using var check = made!.CreateViewAccessor(0, PluginImageWriter.ComputeMmfSize(8, 4));
        Assert.Equal((1, 8, 4), (check.ReadInt32(0), check.ReadInt32(4), check.ReadInt32(8)));
        Assert.Equal((byte)255, check.ReadByte(HeaderSize + 8 * 4 * 4));
        writer.Dispose();
        made.Dispose();
    }

    // 23,171 × 23,171 × 4 bytes is 2,147,610,724: one past int's range, so the second buffer's offset would wrap. At
    // int.MaxValue × int.MaxValue the byte count itself overflows a long, and must still be refused.
    [Theory]
    [InlineData(23_171, 23_171)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void A_resize_whose_buffer_doesnt_fit_an_int_is_refused_before_anything_changes(int width, int height)
    {
        var size = PluginImageWriter.ComputeMmfSize(Width, Height);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);
        var asked = false;
        // Disposed at the end, not by using: a failed test's writer may hold a released view, which its Dispose would trip on.
        var writer = new PluginImageWriter("test-image", mmf, view, Width, Height)
        {
            OpenMapping = (_, _) =>
            {
                asked = true;
                throw new IOException("Not enough memory to map the new size.");
            },
        };

        Assert.Throws<ArgumentException>(() => writer.Resize(width, height));

        Assert.False(asked, "a mapping was asked for");
        Assert.Equal((Width, Height, "test-image"), (writer.Width, writer.Height, writer.MmfName));
        Assert.False(view.SafeMemoryMappedViewHandle.IsClosed, "the old view was released");
        writer.Dispose();
    }

    // The host computes a mapping's size before it maps anything: a size whose buffer can't be held (past int's range,
    // a byte count that overflows, or negative) is refused there, so the host never maps what the writer would refuse.
    [Theory]
    [InlineData(23_171, 23_171)]
    [InlineData(65_536, 65_536)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(-50_000, -50_000)]
    public void A_size_whose_buffer_doesnt_fit_an_int_is_refused_before_the_host_maps_it(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => PluginImageWriter.ComputeMmfSize(width, height));
    }

    // A writer whose constructor fails after it has taken the view's pointer (the bitmap can't be made) gives the pointer
    // back, so the host's disposing the view it passed in really unmaps it. The bitmap's creation is the writer's seam.
    [Fact]
    public void A_writer_whose_bitmap_cant_be_made_releases_the_views_pointer()
    {
        var size = PluginImageWriter.ComputeMmfSize(Width, Height);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);

        Assert.Throws<InvalidOperationException>(() => new BitmapFails("test-image", mmf, view, Width, Height));

        view.Dispose();
        Assert.True(view.SafeMemoryMappedViewHandle.IsClosed, "the view is still pinned by a pointer nobody released");
    }

    // A resize whose new bitmap can't be made leaves the writer as it was: its old size and name, a bitmap that draws into
    // the old mapping, and no Resized raised; a second resize to the same size is tried again, and succeeds.
    [Fact]
    public void A_resize_whose_bitmap_cant_be_made_keeps_the_old_image_and_is_tried_again()
    {
        var size = PluginImageWriter.ComputeMmfSize(Width, Height);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);
        var made = new List<MemoryMappedFile>();
        // Disposed at the end, not by using: a failed test's writer may hold a released view, which its Dispose would trip on.
        var writer = new BitmapFailsOnce("test-image", mmf, view, Width, Height)
        {
            OpenMapping = (_, bytes) =>
            {
                var mapping = MemoryMappedFile.CreateNew(null, bytes);
                made.Add(mapping);
                return mapping;
            },
        };
        var resized = 0;
        writer.Resized += _ => resized++;
        writer.FailNext = true;

        Assert.Throws<InvalidOperationException>(() => writer.Resize(8, 4));

        Assert.Equal((Width, Height, "test-image", 0), (writer.Width, writer.Height, writer.MmfName, resized));
        Assert.False(view.SafeMemoryMappedViewHandle.IsClosed, "the old view was released");
        writer.Bitmap.Erase(SKColors.Red);
        writer.Invalidate();
        using (var check = mmf.CreateViewAccessor(0, size))
        {
            Assert.Equal(1, check.ReadInt32(0));
            Assert.Equal((byte)255, check.ReadByte(HeaderSize + Width * Height * 4));
        }

        writer.Resize(8, 4);

        Assert.Equal((8, 4, "test-image.v1", 1), (writer.Width, writer.Height, writer.MmfName, resized));
        Assert.True(view.SafeMemoryMappedViewHandle.IsClosed, "the old view is released");
        writer.Dispose();
        foreach (var mapping in made)
            mapping.Dispose();
    }

    private sealed class BitmapFailsOnce(string name, MemoryMappedFile mmf, MemoryMappedViewAccessor view, int width, int height)
        : PluginImageWriter(name, mmf, view, width, height)
    {
        public bool FailNext;

        internal override SKBitmap CreateBitmapForBuffer(int bufferIndex)
        {
            if (!FailNext)
                return base.CreateBitmapForBuffer(bufferIndex);
            FailNext = false;
            throw new InvalidOperationException("No bitmap.");
        }
    }

    private sealed class BitmapFails(string name, MemoryMappedFile mmf, MemoryMappedViewAccessor view, int width, int height)
        : PluginImageWriter(name, mmf, view, width, height)
    {
        internal override SKBitmap CreateBitmapForBuffer(int bufferIndex) => throw new InvalidOperationException("No bitmap.");
    }
}
