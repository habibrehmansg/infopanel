namespace TuringSmartScreenLib;

public static class ScreenFactory
{
    public static IScreen Create(ScreenType type, string name, int width = 0, int height = 0)
    {
        if (type == ScreenType.RevisionE)
        {
            var screen = (width > 0 && height > 0)
                ? new TuringSmartScreenRevisionE(name, width, height)
                : new TuringSmartScreenRevisionE(name);
            try
            {
                screen.Open();
            }
            catch
            {
                screen.Dispose();
                throw;
            }
            return new ScreenWrapperRevisionE(screen);
        }
        if (type == ScreenType.RevisionC)
        {
            var screen = new TuringSmartScreenRevisionC(name);
            try
            {
                screen.Open();
            }
            catch
            {
                screen.Dispose();
                throw;
            }
            return new ScreenWrapperRevisionC(screen);
        }
        if (type == ScreenType.RevisionB)
        {
            var screen = new TuringSmartScreenRevisionB(name);
            try
            {
                screen.Open();
            }
            catch
            {
                screen.Dispose();
                throw;
            }
            return (screen.Version & 0x10) != 0
                ? new ScreenWrapperRevisionB1(screen)
                : new ScreenWrapperRevisionB0(screen);
        }
        if (type == ScreenType.RevisionA)
        {
            var screen = new TuringSmartScreenRevisionA(name);
            try
            {
                screen.Open();
            }
            catch
            {
                screen.Dispose();
                throw;
            }
            return new ScreenWrapperRevisionA(screen);
        }

        throw new NotSupportedException("Unsupported type.");
    }
}
