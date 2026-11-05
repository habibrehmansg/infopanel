using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.Windows;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Shell.Windows;
using InfoPanel.Tests.Editing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// The notification-area icon's platform (chosen on Windows unless <c>--no-tray</c>: DesktopHostTests): never in a test
/// engine, which passes the flag; its icon, the coloured logo at the taskbar's size; the Win32 structures it
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

    [Fact]
    public void TheIcon_IsDrawnFromTheColouredLogo_TheProgramsArt()
    {
        Assert.Equal(File.ReadAllBytes(LogoFile), WinTrayLogo.Source());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    public void TheIcon_IsTheColouredLogo_AtTheTaskbarsSize_WhateverTheTheme(int pixels)
    {
        using var icon = SKBitmap.Decode(WinTrayLogo.Png(pixels));
        using var logo = SKBitmap.Decode(LogoFile);

        Assert.Equal((pixels, pixels), (icon.Width, icon.Height));
        Assert.Equal(0, icon.GetPixel(0, 0).Alpha);
        // In colour, not one tone: the frame's blues and violets and the face's navy.
        var opaque = Pixels(icon).Where(p => p.Alpha == 255).ToList();
        Assert.Contains(opaque, p => Math.Max(p.Red, Math.Max(p.Green, p.Blue)) - Math.Min(p.Red, Math.Min(p.Green, p.Blue)) >= 60);
        var centre = icon.GetPixel(pixels * 4 / 10, pixels / 2);
        Assert.True(centre.Alpha == 255 && centre.Blue > centre.Red + 20 && centre.Blue < 90, $"the logo's dark face at the centre: {centre}");
        // Each pixel the logo's own, area-averaged to this size.
        var difference = 0.0;
        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                var (expected, actual) = (Average(logo, x, y, pixels), Premultiplied(icon.GetPixel(x, y)));
                difference += Enumerable.Range(0, 4).Sum(c => Math.Abs(expected[c] - actual[c])) / 4;
            }
        }

        Assert.True(difference / (pixels * pixels) < 12, $"mean difference from the logo {difference / (pixels * pixels):F1}");
    }

    private static string LogoFile => Path.Combine(LiveEngine.RepoRoot(), "Images", "logo.png");

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap) =>
        Enumerable.Range(0, bitmap.Width * bitmap.Height).Select(i => bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width));

    private static double[] Premultiplied(SKColor c) =>
        [c.Red * c.Alpha / 255.0, c.Green * c.Alpha / 255.0, c.Blue * c.Alpha / 255.0, c.Alpha];

    /// <summary>The premultiplied average of the source pixels whose centres fall in target pixel (x, y) of a square of <paramref name="pixels"/>.</summary>
    private static double[] Average(SKBitmap source, int x, int y, int pixels)
    {
        var scale = (double)source.Width / pixels;
        var sum = new double[4];
        var count = 0;
        for (var sy = (int)Math.Ceiling(y * scale - 0.5); sy + 0.5 <= (y + 1) * scale && sy < source.Height; sy++)
        {
            for (var sx = (int)Math.Ceiling(x * scale - 0.5); sx + 0.5 <= (x + 1) * scale && sx < source.Width; sx++)
            {
                var p = Premultiplied(source.GetPixel(sx, sy));
                for (var c = 0; c < 4; c++)
                    sum[c] += p[c];
                count++;
            }
        }

        return [.. sum.Select(v => v / count)];
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
