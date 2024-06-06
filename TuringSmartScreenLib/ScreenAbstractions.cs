namespace TuringSmartScreenLib
{

    internal abstract class ScreenBase : IScreen
    {
        private readonly int width;

        private readonly int height;

        private ScreenOrientation orientation = ScreenOrientation.Portrait;

        public int Width
        {
            get
            {
                if ((orientation == ScreenOrientation.Portrait) || (orientation == ScreenOrientation.ReversePortrait))
                {
                    return width;
                }
                return height;
            }
        }

        public int Height
        {
            get
            {
                if ((orientation == ScreenOrientation.Portrait) || (orientation == ScreenOrientation.ReversePortrait))
                {
                    return height;
                }
                return width;
            }
        }

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

        protected ScreenBase(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        protected abstract bool SetOrientation(ScreenOrientation orientation);

        public abstract void Dispose();

        public abstract void Reset();

        public abstract void Clear();

        public abstract void ScreenOff();

        public abstract void ScreenOn();

        public abstract void SetBrightness(byte level);

        public abstract IScreenBuffer CreateBuffer(int width, int height);

        public abstract void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap);
    }

    internal sealed class ScreenWrapperRevisionA : ScreenBase
    {
        private readonly TuringSmartScreenRevisionA screen;

        public ScreenWrapperRevisionA(TuringSmartScreenRevisionA screen, int width, int height)
            : base(width, height)
        {
            this.screen = screen;
        }

        public override void Dispose() => screen.Dispose();

        public override void Reset() => screen.Reset();

        public override void Clear() => screen.Clear();

        public override void ScreenOff() => screen.ScreenOff();

        public override void ScreenOn() => screen.ScreenOn();

        public override void SetBrightness(byte level) => screen.SetBrightness(level);

        protected override bool SetOrientation(ScreenOrientation orientation)
        {
            switch (orientation)
            {
                case ScreenOrientation.Portrait:
                    screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.Portrait, Width, Height);
                    return true;
                case ScreenOrientation.ReversePortrait:
                    screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.ReversePortrait, Width, Height);
                    return true;
                case ScreenOrientation.Landscape:
                    screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.Landscape, Width, Height);
                    return true;
                case ScreenOrientation.ReverseLandscape:
                    screen.SetOrientation(TuringSmartScreenRevisionA.Orientation.ReverseLandscape, Width, Height);
                    return true;
            }

            return false;
        }

        public override IScreenBuffer CreateBuffer(int width, int height) => new TuringSmartScreenBufferA(width, height);

        public override void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap) => screen.DisplayBitmap(x, y, width, height, bitmap);
    }

    internal abstract class ScreenWrapperRevisionB : ScreenBase
    {
        private readonly TuringSmartScreenRevisionB screen;

        protected ScreenWrapperRevisionB(TuringSmartScreenRevisionB screen, int width, int height)
            : base(width, height)
        {
            this.screen = screen;
        }

        public override void Dispose() => screen.Dispose();

        public override void Reset()
        {
            // Do Nothing
        }

        public override void Clear()
        {
            // TODO Emulation
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

        protected override bool SetOrientation(ScreenOrientation orientation)
        {
            switch (orientation)
            {
                case ScreenOrientation.Portrait:
                    screen.SetOrientation(TuringSmartScreenRevisionB.Orientation.Portrait);
                    return true;
                case ScreenOrientation.ReversePortrait:
                    // TODO Emulation ?
                    return false;
                case ScreenOrientation.Landscape:
                    screen.SetOrientation(TuringSmartScreenRevisionB.Orientation.Landscape);
                    return true;
                case ScreenOrientation.ReverseLandscape:
                    // TODO Emulation ?
                    return false;
            }

            return false;
        }

        public override IScreenBuffer CreateBuffer(int width, int height) => new TuringSmartScreenBufferB(width, height);

        public override void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap) => screen.DisplayBitmap(x, y, width, height, bitmap);
    }

    internal sealed class ScreenWrapperRevisionB0 : ScreenWrapperRevisionB
    {
        public ScreenWrapperRevisionB0(TuringSmartScreenRevisionB screen, int width, int height)
            : base(screen, width, height)
        {
        }

        protected override byte CalcBrightness(byte value) => value == 0 ? (byte)0 : (byte)1;
    }

    internal sealed class ScreenWrapperRevisionB1 : ScreenWrapperRevisionB
    {
        public ScreenWrapperRevisionB1(TuringSmartScreenRevisionB screen, int width, int height)
            : base(screen, width, height)
        {
        }

        protected override byte CalcBrightness(byte value) => (byte)((float)value / 100 * 255);
    }

    internal sealed class ScreenWrapperRevisionC : ScreenBase
    {
        private readonly TuringSmartScreenRevisionC screen;

        public ScreenWrapperRevisionC(TuringSmartScreenRevisionC screen, int width, int height)
            : base(width, height)
        {
            this.screen = screen;
        }

        public override void Dispose() => screen.Dispose();

        public override void Reset() => screen.Reset();

        public override void Clear()
        {
        }

        public override void ScreenOff()
        {
        }

        public override void ScreenOn()
        {
        }

        public override void SetBrightness(byte level) => screen.SetBrightness(level);

        protected override bool SetOrientation(ScreenOrientation orientation)
        {
            return false;
        }

        public override IScreenBuffer CreateBuffer(int width, int height) => new TuringSmartScreenBufferC(width, height);

        public override void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap)
        {
            screen.DisplayBitmap(x, y, width, height, bitmap);
        }
    }
}