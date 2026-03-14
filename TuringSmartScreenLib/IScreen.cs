namespace TuringSmartScreenLib;

public interface IScreen : IDisposable
{
    int Width { get; }

    int Height { get; }

    ScreenOrientation Orientation { get; set; }

    void Reset();

    void Clear();

    void Clear(byte r, byte g, byte b);

    void ScreenOff();

    void ScreenOn();

    void SetBrightness(byte level);

    IScreenBuffer CreateBuffer(int width, int height);

    bool DisplayBuffer(int x, int y, IScreenBuffer buffer);
}
