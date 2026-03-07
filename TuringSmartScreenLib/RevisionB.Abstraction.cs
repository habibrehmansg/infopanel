namespace TuringSmartScreenLib;

using System.Buffers;
using System.Buffers.Binary;

internal abstract class ScreenWrapperRevisionB : ScreenBase
{
    private readonly TuringSmartScreenRevisionB screen;

    protected ScreenWrapperRevisionB(TuringSmartScreenRevisionB screen)
        : base(screen.Width, screen.Height, ScreenOrientation.Portrait)
    {
        this.screen = screen;
    }

    public override void Dispose() => screen.Dispose();

    public override void Reset()
    {
        // Do Nothing
    }

    public override void Clear() => Clear(0, 0, 0);

    public override void Clear(byte r, byte g, byte b)
    {
        // Emulation
        var buffer = ArrayPool<byte>.Shared.Rent(Width * Height * 2);

        var pattern = (Span<byte>)stackalloc byte[2];
        var rgb = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        BinaryPrimitives.WriteInt16BigEndian(pattern, (short)rgb);
        Helper.Fill(buffer, pattern);

        screen.DisplayBitmap(0, 0, buffer, Width, Height);

        ArrayPool<byte>.Shared.Return(buffer);
    }

    public override void ScreenOff()
    {
        // Emulation
        SetBrightness(0);
    }

    public override void ScreenOn()
    {
        // Emulation
        SetBrightness(100);
    }

    public override void SetBrightness(byte level) => screen.SetBrightness(CalcBrightness(level));

    protected abstract byte CalcBrightness(byte value);

    protected override bool IsRotated(ScreenOrientation orientation) =>
        orientation is ScreenOrientation.Landscape or ScreenOrientation.ReverseLandscape;

    protected override bool SetOrientation(ScreenOrientation orientation)
    {
        switch (orientation)
        {
            case ScreenOrientation.Portrait:
            case ScreenOrientation.ReversePortrait:
                screen.SetOrientation(TuringSmartScreenRevisionB.Orientation.Portrait);
                return true;
            case ScreenOrientation.Landscape:
            case ScreenOrientation.ReverseLandscape:
                screen.SetOrientation(TuringSmartScreenRevisionB.Orientation.Landscape);
                return true;
        }

        return false;
    }

    public override IScreenBuffer CreateBuffer(int width, int height) => new ScreenBufferBgr353(width, height);

    public override bool DisplayBuffer(int x, int y, IScreenBuffer buffer)
    {
        var bitmap = ((ScreenBufferBgr353)buffer).Buffer;

        if (IsReverse())
        {
            var size = buffer.Width * buffer.Height * 2;
            var reverseBitmap = ArrayPool<byte>.Shared.Rent(size);
            for (var offset = 0; offset < size; offset += 2)
            {
                bitmap.AsSpan(offset, 2).CopyTo(reverseBitmap.AsSpan(size - offset - 2));
            }

            var result = screen.DisplayBitmap(Width - x - buffer.Width, Height - y - buffer.Height, reverseBitmap, buffer.Width, buffer.Height);

            ArrayPool<byte>.Shared.Return(reverseBitmap);
            return result;
        }

        return screen.DisplayBitmap(x, y, bitmap, buffer.Width, buffer.Height);
    }

    private bool IsReverse() => Orientation is ScreenOrientation.ReversePortrait or ScreenOrientation.ReverseLandscape;
}

internal sealed class ScreenWrapperRevisionB0 : ScreenWrapperRevisionB
{
    public ScreenWrapperRevisionB0(TuringSmartScreenRevisionB screen)
        : base(screen)
    {
    }

    protected override byte CalcBrightness(byte value) => value == 0 ? (byte)0 : (byte)1;
}

internal sealed class ScreenWrapperRevisionB1 : ScreenWrapperRevisionB
{
    public ScreenWrapperRevisionB1(TuringSmartScreenRevisionB screen)
        : base(screen)
    {
    }

    protected override byte CalcBrightness(byte value) => (byte)((float)value / 100 * 255);
}
