namespace TuringSmartScreenLib
{

#pragma warning disable CA1819
    public interface IScreenBuffer
    {
        int Width { get; }

        int Height { get; }

        byte[] Buffer { get; }

        void SetPixel(int x, int y, byte r, byte g, byte b);

        void Clear(byte r = 0, byte g = 0, byte b = 0);
    }
#pragma warning restore CA1819
}