using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.Windows;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Shell.Windows;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// The notification-area icon's platform (chosen on Windows unless <c>--no-tray</c>: DesktopHostTests): never in a test
/// engine, which passes the flag; its glyph for light and dark taskbars; the Win32 structures it
/// passes, at their 64-bit Windows sizes and offsets (on any 64-bit OS); and, on Windows with
/// <c>INFOPANEL_SHELL_TESTS=1</c> in a signed-in session, the real icon added, added again when Explorer says the
/// taskbar was created, and removed.
/// </summary>
public sealed class WinShellPlatformTests
{
    [Fact]
    public void ATestEngine_HasNoIcon_OnAnyOS()
    {
        using var engine = new EngineFactory();

        Assert.True(engine.Services.GetRequiredService<EngineOptions>().NoTray);
        Assert.IsType<NullShellPlatform>(engine.Services.GetRequiredService<IShellPlatform>());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 255)]
    public void TheGlyph_IsDarkOnALightTaskbar_AndWhiteOnADarkOne(bool light, byte channel)
    {
        using var bitmap = SKBitmap.Decode(WinTrayGlyph.Png(32, light));

        Assert.Equal((32, 32), (bitmap.Width, bitmap.Height));
        var opaque = Enumerable.Range(0, 32 * 32).Select(i => bitmap.GetPixel(i % 32, i / 32)).Where(p => p.Alpha == 255).ToList();
        Assert.NotEmpty(opaque);
        Assert.All(opaque, p => Assert.Equal((channel, channel, channel), (p.Red, p.Green, p.Blue)));
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void TheStructures_HaveTheirWindowsSizes_AndOffsets()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(976, Unsafe.SizeOf<NOTIFYICONDATAW>());
        Assert.Equal(160, Unsafe.SizeOf<TASKDIALOGCONFIG>());
        Assert.Equal(12, Unsafe.SizeOf<TASKDIALOG_BUTTON>());

        Assert.Equal(8, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.HWnd)));
        Assert.Equal(16, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.UId)));
        Assert.Equal(20, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.UFlags)));
        Assert.Equal(24, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.UCallbackMessage)));
        Assert.Equal(32, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.HIcon)));
        Assert.Equal(40, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.SzTip)));
        Assert.Equal(816, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.UVersion)));
        Assert.Equal(968, Offset<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.HBalloonIcon)));

        Assert.Equal(4, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.HwndParent)));
        Assert.Equal(20, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.DwFlags)));
        Assert.Equal(28, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.PszWindowTitle)));
        Assert.Equal(44, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.PszMainInstruction)));
        Assert.Equal(52, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.PszContent)));
        Assert.Equal(60, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.CButtons)));
        Assert.Equal(64, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.PButtons)));
        Assert.Equal(72, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.NDefaultButton)));
        Assert.Equal(140, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.PfCallback)));
        Assert.Equal(156, Offset<TASKDIALOGCONFIG>(nameof(TASKDIALOGCONFIG.CxWidth)));

        Assert.Equal(4, Offset<TASKDIALOG_BUTTON>(nameof(TASKDIALOG_BUTTON.PszButtonText)));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_SHELL_TESTS") == "1";

    [Fact]
    public async Task TheRealIcon_IsAdded_AddedAgainWhenTheTaskbarIsCreated_AndRemoved()
    {
        Assert.SkipUnless(Enabled, "Puts an icon by the clock: set INFOPANEL_SHELL_TESTS=1 on Windows, in a signed-in session, to run it.");
        if (OperatingSystem.IsWindows())
            await RealIcon();
    }

    [SupportedOSPlatform("windows")]
    private static async Task RealIcon()
    {
        var thread = new WinUiThread();
        try
        {
            var shell = new WinShellPlatform(thread);
            Assert.True(shell.IsSupported);

            shell.Show(new ShellIcon("InfoPanel"), () => ShellMenu.Build(new ShellState("2.0.0")), _ => Task.CompletedTask);
            var tray = await OnThread(thread, () => shell.Tray);
            Assert.NotNull(tray);
            Assert.True(await OnThread(thread, () => tray.Present));
            Assert.Equal(1, await OnThread(thread, () => tray.Additions));

            Assert.NotEqual(0, Win32.PostMessageW(thread.Hwnd, WinShellPlatform.TaskbarCreatedMessage, 0, 0));
            Assert.Equal(2, await OnThread(thread, () => tray.Additions));
            Assert.True(await OnThread(thread, () => tray.Present));

            shell.Remove();
            Assert.False(await OnThread(thread, () => tray.Present));
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Reads <paramref name="read"/> on the UI thread, after everything posted before it.</summary>
    [SupportedOSPlatform("windows")]
    private static Task<T> OnThread<T>(WinUiThread thread, Func<T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() => done.SetResult(read()));
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
