namespace TuringSmartScreenLib;

internal sealed class ScreenWrapperRevisionC : ScreenBase
{
    private readonly TuringSmartScreenRevisionC screen;

    public ScreenWrapperRevisionC(TuringSmartScreenRevisionC screen)
        : base(screen.Width, screen.Height, ScreenOrientation.Landscape)
    {
        this.screen = screen;
    }

    public override void Dispose() => screen.Dispose();

    public override void Reset()
    {
        // Do Nothing
    }

    public override void Clear() => screen.Clear();

    public override void Clear(byte r, byte g, byte b) => screen.Clear(r, g, b);

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

    public override void SetBrightness(byte level) => screen.SetBrightness(level);

    protected override bool IsRotated(ScreenOrientation orientation) =>
        orientation is ScreenOrientation.Portrait or ScreenOrientation.ReversePortrait;

    protected override bool SetOrientation(ScreenOrientation orientation) => true;

    public override IScreenBuffer CreateBuffer(int width, int height) => new ScreenBufferBgr888(width, height);

    public override bool DisplayBuffer(int x, int y, IScreenBuffer buffer) => screen.DisplayBitmap(x, y, ((ScreenBufferBgr888)buffer).Buffer, buffer.Width, buffer.Height, CalcRotateOption());

    private RotateOption CalcRotateOption() =>
        Orientation switch
        {
            ScreenOrientation.Portrait => RotateOption.Rotate270,
            ScreenOrientation.ReversePortrait => RotateOption.Rotate90,
            ScreenOrientation.ReverseLandscape => RotateOption.Rotate180,
            _ => RotateOption.None
        };
}
