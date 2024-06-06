namespace TuringSmartScreenLib
{

    public static class Extensions
    {
        public static IScreenBuffer CreateBuffer(this IScreen screen) =>
            screen.CreateBuffer(screen.Width, screen.Height);

        public static void DisplayBuffer(this IScreen screen, IScreenBuffer buffer) =>
            screen.DisplayBitmap(0, 0, buffer.Width, buffer.Height, buffer.Buffer);

        public static void DisplayBuffer(this IScreen screen, int x, int y, IScreenBuffer buffer) =>
            screen.DisplayBitmap(x, y, buffer.Width, buffer.Height, buffer.Buffer);

        public static void DisplayBuffer(this TuringSmartScreenRevisionA screen, TuringSmartScreenBufferA buffer) =>
            screen.DisplayBitmap(0, 0, buffer.Width, buffer.Height, buffer.Buffer);

        public static void DisplayBuffer(this TuringSmartScreenRevisionA screen, int x, int y, TuringSmartScreenBufferA buffer) =>
            screen.DisplayBitmap(x, y, buffer.Width, buffer.Height, buffer.Buffer);

        public static void DisplayBuffer(this TuringSmartScreenRevisionB screen, TuringSmartScreenBufferB buffer) =>
            screen.DisplayBitmap(0, 0, buffer.Width, buffer.Height, buffer.Buffer);

        public static void DisplayBuffer(this TuringSmartScreenRevisionB screen, int x, int y, TuringSmartScreenBufferB buffer) =>
            screen.DisplayBitmap(x, y, buffer.Width, buffer.Height, buffer.Buffer);
    }
}