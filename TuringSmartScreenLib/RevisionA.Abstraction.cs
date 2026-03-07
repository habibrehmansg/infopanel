namespace TuringSmartScreenLib;

using System.Buffers.Binary;
using System.Buffers;

internal sealed class ScreenWrapperRevisionA : ScreenBase
{
    private readonly TuringSmartScreenRevisionA screen;

    public ScreenWrapperRevisionA(TuringSmartScreenRevisionA screen)
        : base(screen.Width, screen.Height, ScreenOrientation.Portrait)
    {
        this.screen = screen;
    }

    public override void Dispose() => screen.Dispose();

    public override void Reset() => screen.Reset();

    public override void Clear() => screen.Clear();

    public override void Clear(byte r, byte g, byte b)
    {
        // Emulation
        var buffer = ArrayPool<byte>.Shared.Rent(Width * Height * 2);

        var pattern = (Span<byte>)stackalloc byte[2];
        var rgb = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        BinaryPrimitives.WriteInt16LittleEndian(pattern, (short)rgb);
        Helper.Fill(buffer, pattern);

        screen.DisplayBitmap(0, 0, buffer, Width, Height);

        ArrayPool<byte>.Shared.Return(buffer);
    }

    public override void ScreenOff() => screen.ScreenOff();

    public override void ScreenOn() => screen.ScreenOn();

    public override void SetBrightness(byte level) => screen.SetBrightness(255 - (byte)((float)level / 100 * 255));

    protected override bool IsRotated(ScreenOrientation orientation) =>
        orientation is ScreenOrientation.Landscape or ScreenOrientation.ReverseLandscape;

    protected override bool SetOrientation(ScreenOrientation orientation)
    {
        switch (orientation)
        {
            case ScreenOrientation.Portrait:
                screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.Portrait);
                return true;
            case ScreenOrientation.ReversePortrait:
                screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.ReversePortrait);
                return true;
            case ScreenOrientation.Landscape:
                screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.Landscape);
                return true;
            case ScreenOrientation.ReverseLandscape:
                screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.ReverseLandscape);
                return true;
        }

        return false;
    }

    public override IScreenBuffer CreateBuffer(int width, int height) => new ScreenBufferRgb353(width, height);

    public override bool DisplayBuffer(int x, int y, IScreenBuffer buffer) => screen.DisplayBitmap(x, y, ((ScreenBufferRgb353)buffer).Buffer, buffer.Width, buffer.Height);
}
