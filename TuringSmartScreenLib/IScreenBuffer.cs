namespace TuringSmartScreenLib;

public interface IScreenBuffer : IDisposable
{
    int Width { get; }

    int Height { get; }

    void SetPixel(int x, int y, byte r, byte g, byte b);

    void Clear();

    void Clear(byte r, byte g, byte b);
}
