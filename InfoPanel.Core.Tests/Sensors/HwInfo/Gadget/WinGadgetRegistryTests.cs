using System.Runtime.Versioning;
using System.Security.Principal;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo.Gadget;
using Microsoft.Win32;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo.Gadget;

/// <summary>
/// The Windows registry reader on real keys, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped elsewhere).
/// Each test makes its own <c>HKCU\Software\InfoPanelTest-{guid}\VSB</c>, points the reader at it and deletes only that
/// key afterwards: HWiNFO's key is never touched. The last test runs only unelevated (a limited scheduled task), the
/// proof that the normal user reads it.
/// </summary>
public sealed class WinGadgetRegistryTests
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Reads the real Windows registry: set INFOPANEL_WINDOWS_TESTS=1 on Windows to run it.";

    [Fact]
    public void ATestKey_ItsValueNamesKindsTextsAndCount_AreRead_InThe64BitView()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            WithTestKey(ReadsNamesKindsAndTexts);
    }

    [Fact]
    public void TheKeysLastWriteTime_MovesWhenAValuesDataChanges()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            WithTestKey(RewriteMovesLastWrite);
    }

    [Fact]
    public void ADeletedKey_ReadsAsMissing_AndATextOver1024CharactersIsNeverRead()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            WithTestKey(DeletedKeyAndLongText);
    }

    [Fact]
    public void TheSource_ListsAndReadsATestKey_ThroughTheWindowsReader()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            WithTestKey(SourceReadsTestKey);
    }

    [Fact]
    public void AValueDeletedAfterItIsListed_ReadsAsGone_NotAsAnError()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            WithTestKey(DeletedValueReadsAsGone);
    }

    [Fact]
    public void TheProcessCheck_CountsOnlyAHWiNFOInThisSession()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
        {
            var session = HwInfoGadgetSource.CurrentSession();
            var sessions = HwInfoGadgetSource.HwInfoSessions();
            // Run as a service (session 0), this process counts no HWiNFO: one there may be another user's.
            Assert.Equal(session != 0 && sessions.Contains(session), HwInfoGadgetSource.IsHwInfoRunning());
            TestContext.Current.TestOutputHelper?.WriteLine($"This process's session {session}; HWiNFO64 in sessions [{string.Join(", ", sessions)}]; counted as running: {HwInfoGadgetSource.IsHwInfoRunning()}");
        }
    }

    [Fact]
    public void ATestKey_IsReadAsTheNormalUser()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
        {
            Assert.SkipWhen(IsElevated(), "This process is elevated: run it unelevated (a limited scheduled task) to prove the normal user reads the registry.");
            WithTestKey(ReadsNamesKindsAndTexts);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadsNamesKindsAndTexts(RegistryKey key, string path)
    {
        key.SetValue("Sensor0", "CPU [#0]: AMD Ryzen 7 8845HS: Enhanced", RegistryValueKind.String);
        key.SetValue("Label0", "CPU (Tctl/Tdie)", RegistryValueKind.String);
        key.SetValue("Value0", "45.1 °C", RegistryValueKind.String);
        key.SetValue("ValueRaw0", "45.1", RegistryValueKind.String);
        key.SetValue("ValueRaw1", 7, RegistryValueKind.DWord);
        key.SetValue("Value2", "%TEMP%", RegistryValueKind.ExpandString);
        // Not HWiNFO's names: neither is read.
        key.SetValue("Expand2", "%TEMP%", RegistryValueKind.ExpandString);
        key.SetValue("AValueNameLongerThanAnyOfHWiNFOs", "x", RegistryValueKind.String);

        var snapshot = WinGadgetRegistry.Reader(path).Read(GadgetHive.CurrentUser);

        Assert.NotNull(snapshot);
        Assert.Equal(GadgetHive.CurrentUser, snapshot.Hive);
        Assert.Equal(RegistryView.Registry64, WinGadgetRegistry.View);
        Assert.Equal(8, snapshot.ValueCount);
        Assert.Equal(
            [
                ("Label0", GadgetValueKind.String, (string?)"CPU (Tctl/Tdie)"),
                ("Sensor0", GadgetValueKind.String, "CPU [#0]: AMD Ryzen 7 8845HS: Enhanced"),
                ("Value0", GadgetValueKind.String, "45.1 °C"),
                ("Value2", GadgetValueKind.Other, null),
                ("ValueRaw0", GadgetValueKind.String, "45.1"),
                ("ValueRaw1", GadgetValueKind.Other, null),
            ],
            snapshot.Values.Select(v => (v.Name, v.Kind, v.Text)).OrderBy(v => v.Name, StringComparer.Ordinal));
        var age = DateTime.UtcNow - snapshot.LastWriteUtc;
        Assert.InRange(age, TimeSpan.FromSeconds(-5), TimeSpan.FromMinutes(1));
        Assert.Null(WinGadgetRegistry.Reader(path).Read(GadgetHive.LocalMachine));
        TestContext.Current.TestOutputHelper?.WriteLine($"Read {snapshot.ValueCount} values as {WindowsIdentity.GetCurrent().Name}, elevated: {IsElevated()}, last write {snapshot.LastWriteUtc:O}; HWiNFO64 running: {HwInfoGadgetSource.IsHwInfoRunning()}");
    }

    /// <summary>
    /// What the freshness rule rests on: a write of new data moves the key's last-write time. A write of the same data
    /// may not (it doesn't on Windows 11 26300): that is why the sampler also asks whether HWiNFO runs. Both go to the
    /// output.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RewriteMovesLastWrite(RegistryKey key, string path)
    {
        var registry = WinGadgetRegistry.Reader(path);
        key.SetValue("ValueRaw0", "45.1", RegistryValueKind.String);
        var first = registry.Read(GadgetHive.CurrentUser)!.LastWriteUtc;
        Assert.Equal(first, registry.Read(GadgetHive.CurrentUser)!.LastWriteUtc);

        Thread.Sleep(50);
        key.SetValue("ValueRaw0", "45.1", RegistryValueKind.String);
        var same = registry.Read(GadgetHive.CurrentUser)!.LastWriteUtc;

        Thread.Sleep(50);
        key.SetValue("ValueRaw0", "45.2", RegistryValueKind.String);
        var changed = registry.Read(GadgetHive.CurrentUser)!.LastWriteUtc;
        Assert.True(changed > first && changed > same, $"The last-write time didn't move on a write of new data: {first:O}, {same:O}, then {changed:O}");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Last write {first:O}; the same data again: {(same == first ? "unchanged" : "moved")} ({same:O}); new data: {changed:O}");
    }

    /// <summary>A text up to 1,024 characters is read whole; a longer one only by its size, its data never read.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeletedKeyAndLongText(RegistryKey key, string path)
    {
        var registry = WinGadgetRegistry.Reader(path);
        var text = new string('x', HwInfoGadgetSampler.MaxTextLength);
        key.SetValue("Label0", text, RegistryValueKind.String);
        key.SetValue("Label1", text + "x", RegistryValueKind.String);
        Assert.Equal(
            [("Label0", GadgetValueKind.String, (string?)text), ("Label1", GadgetValueKind.TooLong, null)],
            registry.Read(GadgetHive.CurrentUser)!.Values.Select(v => (v.Name, v.Kind, v.Text)).OrderBy(v => v.Name, StringComparer.Ordinal));

        using (var parent = Registry.CurrentUser.OpenSubKey(Path.GetDirectoryName(path)!, writable: true)!)
            parent.DeleteSubKey("VSB");
        Assert.Null(registry.Read(GadgetHive.CurrentUser));
    }

    [SupportedOSPlatform("windows")]
    private static void SourceReadsTestKey(RegistryKey key, string path)
    {
        key.SetValue("Sensor0", "CPU [#0]: AMD Ryzen 7 8845HS: Enhanced", RegistryValueKind.String);
        key.SetValue("Label0", "CPU (Tctl/Tdie)", RegistryValueKind.String);
        key.SetValue("Value0", "45,1 °C", RegistryValueKind.String);
        key.SetValue("ValueRaw0", "45,1", RegistryValueKind.String);
        key.SetValue("Sensor1", "GPU [#0]: AMD Radeon 780M", RegistryValueKind.String);
        key.SetValue("Label1", "GPU Load", RegistryValueKind.String);
        key.SetValue("ValueRaw1", "7", RegistryValueKind.String);

        using var source = HwInfoGadgetSource.Create(WinGadgetRegistry.Reader(path));
        source.Open();
        Assert.Equal(
            ["plugin:/infopanel.hwinfo-gadget/CPU [#0]: AMD Ryzen 7 8845HS: Enhanced/CPU (Tctl%2FTdie)", "plugin:/infopanel.hwinfo-gadget/GPU [#0]: AMD Radeon 780M/GPU Load"],
            source.GetSensors().Select(s => s.Key.ToString()));
        Assert.True(source.TryRead(source.GetSensors()[0].Key, out var cpu));
        Assert.Equal((45.1, "°C"), (cpu.ValueNow, cpu.Unit));
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.GetSensors()[1].Key));
    }

    /// <summary>A value deleted after it is listed: its size and its text read as gone (<c>ERROR_FILE_NOT_FOUND</c>), never thrown.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeletedValueReadsAsGone(RegistryKey key, string path)
    {
        key.SetValue("ValueRaw0", "45.1", RegistryValueKind.String);
        using var open = new WinGadgetRegistry(path).Open(GadgetHive.CurrentUser);
        Assert.NotNull(open);
        Assert.Equal(GadgetEnumResult.Found, open.EnumName(0, 12, out var name));
        Assert.Equal("ValueRaw0", name);
        Assert.Equal((GadgetValueKind.String, 10), open.QueryValue("ValueRaw0"));
        Assert.Equal("45.1", open.ReadText("ValueRaw0", 10));
        Assert.Null(open.ReadText("ValueRaw0", 4));

        key.DeleteValue("ValueRaw0");
        Assert.Null(open.QueryValue("ValueRaw0"));
        Assert.Null(open.ReadText("ValueRaw0", 10));
        Assert.Equal(GadgetEnumResult.NoMore, open.EnumName(0, 12, out _));

        key.SetValue("ValueRaw0Extra", "1", RegistryValueKind.String);
        Assert.Equal(GadgetEnumResult.TooLong, open.EnumName(0, 12, out var longName));
        Assert.Null(longName);
    }

    /// <summary>Makes <c>HKCU\Software\InfoPanelTest-{guid}\VSB</c>, runs <paramref name="test"/> on it, then deletes that key and its parent only.</summary>
    [SupportedOSPlatform("windows")]
    private static void WithTestKey(Action<RegistryKey, string> test)
    {
        var parentPath = @"Software\InfoPanelTest-" + Guid.NewGuid().ToString("N");
        var path = parentPath + @"\VSB";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = root.CreateSubKey(path, writable: true);
            test(key, path);
        }
        finally
        {
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            root.DeleteSubKey(parentPath, throwOnMissingSubKey: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
