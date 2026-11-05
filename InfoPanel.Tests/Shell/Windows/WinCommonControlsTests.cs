using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Shell.Windows;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// Common Controls 6 for the prompts whichever program runs the engine (<see cref="WinCommonControls"/>): the
/// activation context's description at its 64-bit Windows size (any 64-bit OS); on Windows, inside the test host (a
/// program whose manifest, like <c>dotnet.exe</c>'s, isn't the engine's), the context made from the engine's assembly
/// is activated and its comctl32 has <c>TaskDialogIndirect</c>.
/// </summary>
public sealed class WinCommonControlsTests
{
    [Fact]
    public void TheActivationContextsDescription_HasItsWindowsSize_AndOffsets()
    {
        Assert.SkipUnless(IntPtr.Size == 8, "The layouts pinned are 64-bit Windows'.");

        Assert.Equal(56, Marshal.SizeOf<ACTCTXW>());
        Assert.Equal(8, (int)Marshal.OffsetOf<ACTCTXW>(nameof(ACTCTXW.LpSource)));
        Assert.Equal(24, (int)Marshal.OffsetOf<ACTCTXW>(nameof(ACTCTXW.LpAssemblyDirectory)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ACTCTXW>(nameof(ACTCTXW.LpResourceName)));
        Assert.Equal(48, (int)Marshal.OffsetOf<ACTCTXW>(nameof(ACTCTXW.HModule)));
    }

    [Fact]
    public void TheTaskDialog_IsThere_InsideTheEnginesCommonControls_WhicheverProgramRunsTheEngine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Common Controls 6 is Windows'.");
        if (OperatingSystem.IsWindows())
            TaskDialogIsThere();
    }

    [SupportedOSPlatform("windows")]
    private static void TaskDialogIsThere()
    {
        using var scope = WinCommonControls.Enter();

        Assert.True(scope.Active);
        Assert.True(NativeLibrary.TryLoad("comctl32.dll", out var comctl32));
        Assert.True(NativeLibrary.TryGetExport(comctl32, "TaskDialogIndirect", out _));
    }
}
