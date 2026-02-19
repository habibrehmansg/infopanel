namespace TuringSmartScreenLib;

internal abstract class ScreenBase : IScreen
{
    private readonly int width;

    private readonly int height;

    private ScreenOrientation orientation;

    public int Width => IsRotated(orientation) ? height : width;

    public int Height => IsRotated(orientation) ? width : height;

    public ScreenOrientation Orientation
    {
        get => orientation;
        set
        {
            if (SetOrientation(value))
            {
                orientation = value;
            }
        }
    }

    protected ScreenBase(int width, int height, ScreenOrientation orientation)
    {
        this.width = width;
        this.height = height;
        this.orientation = orientation;
    }

    protected abstract bool IsRotated(ScreenOrientation orientation);

    protected abstract bool SetOrientation(ScreenOrientation orientation);

    public abstract void Dispose();

    public abstract void Reset();

    public abstract void Clear();

    public abstract void Clear(byte r, byte g, byte b);

    public abstract void ScreenOff();

    public abstract void ScreenOn();

    public abstract void SetBrightness(byte level);

    public abstract IScreenBuffer CreateBuffer(int width, int height);

    public abstract bool DisplayBuffer(int x, int y, IScreenBuffer buffer);
}
