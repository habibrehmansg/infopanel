using System;

namespace TuringSmartScreenLib
{

#pragma warning disable CA1819
    public interface IScreen : IDisposable
    {
        int Width { get; }

        int Height { get; }

        ScreenOrientation Orientation { get; set; }

        void Reset();

        void Clear();

        void ScreenOff();

        void ScreenOn();

        void SetBrightness(byte level);

        IScreenBuffer CreateBuffer(int width, int height);

        void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap);
    }
#pragma warning restore CA1819
}