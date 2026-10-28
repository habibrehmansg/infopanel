using System.Xml.Linq;
using InfoPanel.Import;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// The 1.x settings reader keeps the rates and the storage interval within 2.0's ranges (the engine settings' limits),
/// so the migration envelope carries only values the engine accepts. 1.4.x's controls keep them in range
/// (<c>Views/Pages/SettingsPage.xaml</c>: frame rate 1–60, graph rate 100–3000 ms, storage interval 1–300 s), but a
/// hand-edited file can hold anything that parses.
/// </summary>
public sealed class V1SettingsTests
{
    private static (V1Settings Settings, IReadOnlyList<ImportDiagnostic> Diagnostics) Read(string inner)
    {
        var diagnostics = new ImportDiagnostics(100);
        var settings = V1SettingsReader.Read(XElement.Parse($"<Settings><Version>131</Version>{inner}</Settings>", LoadOptions.SetLineInfo),
            "settings.xml", new ImportLimits(), diagnostics);
        return (settings, diagnostics.ToList());
    }

    [Fact]
    public void V1SettingLength_HoldsDeviceFieldsAndHotkeys_NotTheScalarSettings()
    {
        // spec/docs/limits.md, "1.x import limits": v1SettingLength is the cap on a device field's value and on a
        // hotkey's; a scalar setting is read by its own parse (a frame rate of 1,024 spaces and 30 is 30, no warning).
        var (settings, diagnostics) = Read($"<TargetFrameRate>{new string(' ', 1_024)}30</TargetFrameRate>");
        Assert.Equal(30, settings.TargetFrameRate);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(61, 60)]
    [InlineData(120, 60)]
    public void FrameRate_OutOfRange_IsClamped_WithAWarning(int stored, int read)
    {
        var (settings, diagnostics) = Read($"<TargetFrameRate>{stored}</TargetFrameRate>");

        Assert.Equal(read, settings.TargetFrameRate);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(("warning", "import.setting", "/settings/targetFrameRate", "settings.xml"), (warning.Severity, warning.Code, warning.Path, warning.File));
        Assert.Contains($"{stored}", warning.Message);
        Assert.NotNull(warning.Line);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(99, 100)]
    [InlineData(3001, 3000)]
    [InlineData(5000, 3000)]
    public void GraphRate_OutOfRange_IsClamped_WithAWarning(int stored, int read)
    {
        var (settings, diagnostics) = Read($"<TargetGraphUpdateRate>{stored}</TargetGraphUpdateRate>");

        Assert.Equal(read, settings.TargetGraphUpdateRate);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(("import.setting", "/settings/targetGraphUpdateRate"), (warning.Code, warning.Path));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(301, 300)]
    [InlineData(999, 300)]
    public void StorageInterval_OutOfRange_IsClamped_WithAWarning(int stored, int read)
    {
        var (settings, diagnostics) = Read($"<LibreHardwareMonitorStorageInterval>{stored}</LibreHardwareMonitorStorageInterval>");

        Assert.Equal(read, settings.Libre.StorageInterval);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(("import.setting", "/settings/libre/storageInterval"), (warning.Code, warning.Path));
    }

    [Fact]
    public void InRangeValues_AndTheirEdges_AreReadAsTheyAre_WithoutWarnings()
    {
        var (low, lowDiagnostics) = Read(
            "<TargetFrameRate>1</TargetFrameRate><TargetGraphUpdateRate>100</TargetGraphUpdateRate>" +
            "<LibreHardwareMonitorStorageInterval>1</LibreHardwareMonitorStorageInterval>");
        var (high, highDiagnostics) = Read(
            "<TargetFrameRate>60</TargetFrameRate><TargetGraphUpdateRate>3000</TargetGraphUpdateRate>" +
            "<LibreHardwareMonitorStorageInterval>300</LibreHardwareMonitorStorageInterval>");
        var (middle, middleDiagnostics) = Read(
            "<TargetFrameRate>24</TargetFrameRate><TargetGraphUpdateRate>250</TargetGraphUpdateRate>" +
            "<LibreHardwareMonitorStorageInterval>45</LibreHardwareMonitorStorageInterval>");

        Assert.Equal((1, 100, 1), (low.TargetFrameRate, low.TargetGraphUpdateRate, low.Libre.StorageInterval));
        Assert.Equal((60, 3000, 300), (high.TargetFrameRate, high.TargetGraphUpdateRate, high.Libre.StorageInterval));
        Assert.Equal((24, 250, 45), (middle.TargetFrameRate, middle.TargetGraphUpdateRate, middle.Libre.StorageInterval));
        Assert.Empty(lowDiagnostics);
        Assert.Empty(highDiagnostics);
        Assert.Empty(middleDiagnostics);
    }
}
