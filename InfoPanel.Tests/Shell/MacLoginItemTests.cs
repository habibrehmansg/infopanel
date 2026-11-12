using System.Xml.Linq;
using InfoPanel.Shell;
using InfoPanel.Shell.MacOS;
using InfoPanel.Tests.Editing;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// Start at Login on macOS (<see cref="MacLoginItem"/>): the menu line from a fake status source in each of its states,
/// what choosing it does (the action the line showed, even when the status changed while the menu was open), failures
/// of the system's calls, where it goes in the menu, the App Translocation check on sample paths, and that a process
/// outside an app bundle (this test host, a <c>dotnet run</c>, a plain publish, even one in a folder named
/// <c>InfoPanel.app</c>) gets no line. Nothing is registered.
/// </summary>
public sealed class MacLoginItemTests
{
    private const string BundlePath = "/Applications/InfoPanel.app";

    /// <summary>An app bundle's metadata, as the packaged app's <c>Info.plist</c> gives it.</summary>
    private static MacBundle App(string? path = BundlePath) => new(path, "net.infopanel.InfoPanel", "APPL");

    private sealed class FakeLoginService : IMacLoginService
    {
        public MacLoginStatus Current { get; set; } = MacLoginStatus.NotRegistered;

        public bool Unreadable { get; set; }

        public bool Refuses { get; set; }

        /// <summary>The status a successful register leaves (the user may still have to approve it).</summary>
        public MacLoginStatus AfterRegister { get; set; } = MacLoginStatus.Enabled;

        public List<string> Calls { get; } = [];

        public MacLoginStatus Status() => Unreadable ? throw new InvalidOperationException("status") : Current;

        public void Register()
        {
            Calls.Add("register");
            if (Refuses)
                throw new InvalidOperationException("SMAppService registerAndReturnError: failed (1): Operation not permitted");
            Current = AfterRegister;
        }

        public void Unregister()
        {
            Calls.Add("unregister");
            if (Refuses)
                throw new InvalidOperationException("SMAppService unregisterAndReturnError: failed (1): Operation not permitted");
            Current = MacLoginStatus.NotRegistered;
        }

        public void OpenLoginItemsSettings() => Calls.Add("open-settings");
    }

    private sealed class Collecting(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private readonly FakeLoginService _service = new();

    private readonly List<LogEvent> _log = [];

    private MacLoginItem Item(MacBundle? bundle = null) =>
        new(bundle ?? App(), _service, new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Collecting(_log)).CreateLogger());

    [Fact]
    public void Off_StartAtLogin_Unticked_AndChoosingIt_Registers()
    {
        var item = Item();

        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login"), item.Line());
        item.Choose();
        Assert.Equal(["register"], _service.Calls);
    }

    [Fact]
    public void On_StartAtLogin_Ticked_AndChoosingIt_Unregisters()
    {
        _service.Current = MacLoginStatus.Enabled;
        var item = Item();

        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login") { Checked = true }, item.Line());
        item.Choose();
        Assert.Equal(["unregister"], _service.Calls);
    }

    [Fact]
    public void RequiresApproval_SaysSo_AndChoosingIt_OpensTheLoginItemsPane()
    {
        _service.Current = MacLoginStatus.RequiresApproval;
        var item = Item();

        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login (approve in System Settings…)"), item.Line());
        item.Choose();
        Assert.Equal(["open-settings"], _service.Calls);
    }

    [Fact]
    public void Translocated_Disabled_AskingToMoveTheApp_AndChoosingIt_DoesNothing()
    {
        _service.Current = MacLoginStatus.Enabled;
        var item = Item(App("/private/var/folders/x1/abc123/T/AppTranslocation/0A1B2C3D-0000-4000-8000-000000000000/d/InfoPanel.app"));

        var line = item.Line();
        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Move InfoPanel to Applications to start at login", ShellItemKind.Status), line);
        Assert.False(line!.Enabled);
        item.Choose();
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void NotFound_ReadsAsOff()
    {
        _service.Current = MacLoginStatus.NotFound;

        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login"), Item().Line());
    }

    [Fact]
    public void AStatusThatCantBeRead_Greys_TheLine_AndChoosingIt_DoesNothing()
    {
        _service.Unreadable = true;
        var item = Item();

        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login", ShellItemKind.Status), item.Line());
        item.Choose();
        Assert.Empty(_service.Calls);
    }

    // The status changes while the menu is open (the user approves it in System Settings, or turns it off there):
    // choosing the line does what it showed, or nothing when that is already done; never the opposite.

    [Theory]
    [InlineData((int)MacLoginStatus.Enabled)]
    [InlineData((int)MacLoginStatus.RequiresApproval)]
    public void ShownOff_ButOnBeforeItIsChosen_RegistersNothing_AndNeverUnregisters(int now)
    {
        var item = Item();
        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login"), item.Line());

        _service.Current = (MacLoginStatus)now;
        item.Choose();

        Assert.Empty(_service.Calls);
    }

    [Theory]
    [InlineData((int)MacLoginStatus.NotRegistered)]
    [InlineData((int)MacLoginStatus.NotFound)]
    public void ShownOn_ButOffBeforeItIsChosen_UnregistersNothing_AndNeverRegisters(int now)
    {
        _service.Current = MacLoginStatus.Enabled;
        var item = Item();
        Assert.True(item.Line()!.Checked);

        _service.Current = (MacLoginStatus)now;
        item.Choose();

        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void ShownOn_ButWaitingForApprovalBeforeItIsChosen_StillUnregisters()
    {
        _service.Current = MacLoginStatus.Enabled;
        var item = Item();
        Assert.True(item.Line()!.Checked);

        _service.Current = MacLoginStatus.RequiresApproval;
        item.Choose();

        Assert.Equal(["unregister"], _service.Calls);
    }

    [Theory]
    [InlineData((int)MacLoginStatus.Enabled)]
    [InlineData((int)MacLoginStatus.NotRegistered)]
    public void ShownWaitingForApproval_ButChangedBeforeItIsChosen_StillOpensTheLoginItemsPane(int now)
    {
        _service.Current = MacLoginStatus.RequiresApproval;
        var item = Item();
        Assert.Equal(MacLoginItem.ApproveLabel, item.Line()!.Label);

        _service.Current = (MacLoginStatus)now;
        item.Choose();

        Assert.Equal(["open-settings"], _service.Calls);
    }

    [Fact]
    public void ShownGreyed_ButReadableBeforeItIsChosen_DoesNothing()
    {
        _service.Unreadable = true;
        var item = Item();
        Assert.Equal(ShellItemKind.Status, item.Line()!.Kind);

        _service.Unreadable = false;
        item.Choose();

        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void ChosenWithoutALineShown_DoesNothing()
    {
        Item().Choose();

        Assert.Empty(_service.Calls);
    }

    // The system refuses, or asks for approval.

    [Fact]
    public void ARefusedRegister_IsLogged_NeverThrown_AndTheNextMenuShowsItOff()
    {
        _service.Refuses = true;
        var item = Item();
        item.Line();

        item.Choose();

        Assert.Equal(["register"], _service.Calls);
        var error = Assert.Single(_log, e => e.Level >= LogEventLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login"), item.Line());
    }

    [Fact]
    public void ARefusedUnregister_IsLogged_NeverThrown_AndTheNextMenuShowsItOn()
    {
        _service.Current = MacLoginStatus.Enabled;
        _service.Refuses = true;
        var item = Item();
        item.Line();

        item.Choose();

        Assert.Equal(["unregister"], _service.Calls);
        var error = Assert.Single(_log, e => e.Level >= LogEventLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login") { Checked = true }, item.Line());
    }

    [Fact]
    public void ARegisterThatNeedsApproval_TheNextMenuAsksForIt()
    {
        _service.AfterRegister = MacLoginStatus.RequiresApproval;
        var item = Item();
        item.Line();

        item.Choose();

        Assert.Equal(["register"], _service.Calls);
        Assert.Equal(new ShellMenuItem(MacLoginItem.Id, "Start at Login (approve in System Settings…)"), item.Line());
    }

    // Where the line shows.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Users/someone/InfoPanel/bin/Release/net10.0/osx-arm64/publish")]
    [InlineData("/Users/someone/.dotnet")]
    [InlineData("/Applications/InfoPanel.app/Contents/Resources/engine")]
    public void OutsideAnAppBundle_NoLine_AndChoosingDoesNothing(string? path)
    {
        _service.Current = MacLoginStatus.Enabled;
        var item = Item(App(path));

        Assert.Null(item.Line());
        item.Choose();
        Assert.Empty(_service.Calls);
    }

    /// <summary>
    /// A plain publish copied into a folder named <c>InfoPanel.app</c>: the main bundle's path ends in <c>.app</c>, but
    /// it has no <c>Contents/Info.plist</c>, so no identifier and no package type (or not an application's).
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("net.infopanel.InfoPanel", null)]
    [InlineData(null, "APPL")]
    [InlineData("", "APPL")]
    [InlineData("net.infopanel.InfoPanel", "BNDL")]
    [InlineData("net.infopanel.InfoPanel", "FMWK")]
    public void AFolderNamedLikeAnApp_WithoutAnApplicationsMetadata_NoLine_AndChoosingDoesNothing(string? identifier, string? packageType)
    {
        var bundle = new MacBundle(BundlePath, identifier, packageType);
        var item = Item(bundle);

        Assert.False(MacLoginItem.IsAppBundle(bundle));
        Assert.Null(item.Line());
        item.Choose();
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void ThePackagedAppsMetadata_IsAnAppBundle_WithALine()
    {
        var plist = XDocument.Load(Path.Combine(LiveEngine.RepoRoot(), "packaging", "macos", "Info.plist"));
        var top = plist.Root!.Element("dict")!;
        var bundle = new MacBundle(BundlePath, ValueOf(top, "CFBundleIdentifier"), ValueOf(top, "CFBundlePackageType"));

        Assert.True(MacLoginItem.IsAppBundle(bundle));
        Assert.NotNull(Item(bundle).Line());
    }

    private static string? ValueOf(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().First().Value;

    [Fact]
    public void WithoutServiceManagement_NoLine()
    {
        Assert.Null(new MacLoginItem(App(), null).Line());
    }

    [Theory]
    [InlineData("/private/var/folders/x1/abc123/T/AppTranslocation/0A1B2C3D-0000-4000-8000-000000000000/d/InfoPanel.app", true)]
    [InlineData("/var/folders/x1/abc123/T/AppTranslocation/0A1B2C3D-0000-4000-8000-000000000000/d/InfoPanel.app", true)]
    [InlineData("/Applications/InfoPanel.app", false)]
    [InlineData("/Users/someone/Applications/InfoPanel.app", false)]
    [InlineData("/Users/someone/Downloads/InfoPanel.app", false)]
    [InlineData("/Volumes/InfoPanel 2.0.0/InfoPanel.app", false)]
    [InlineData("/Users/someone/AppTranslocation/InfoPanel.app", false)]
    [InlineData("/private/var/folders/x1/abc123/T/NotTranslocation/InfoPanel.app", false)]
    public void Translocation_IsTheSystemsTemporaryFolder_AppTranslocation(string path, bool translocated)
    {
        Assert.Equal(translocated, MacLoginItem.IsTranslocated(path));
    }

    [Fact]
    public void TheLine_GoesAboveQuit_InAGroupOfItsOwn()
    {
        var menu = ShellMenu.Build(new ShellState("2.0.0"));
        var line = new ShellMenuItem(MacLoginItem.Id, "Start at Login");

        var items = MacLoginItem.WithLine(menu, line).Items;

        var at = items.ToList().FindIndex(i => i.Id == MacLoginItem.Id);
        Assert.Equal(
            [ShellMenu.CopyEngineDetails, "", MacLoginItem.Id, "", ShellMenu.Quit],
            items.Skip(at - 2).Select(i => i.Id));
        Assert.Equal(menu.Items.Count + 2, items.Count);
    }

    [Fact]
    public void NoLine_TheMenuAsItIs()
    {
        var menu = ShellMenu.Build(new ShellState("2.0.0"));

        Assert.Same(menu, MacLoginItem.WithLine(menu, null));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ThisProcess_OutsideAnAppBundle_HasNoLine()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");

        // The test host runs as a plain program, as a dotnet run or a plain publish does (the preview engine): no .app.
        Assert.Null(MacLoginItem.ForThisProcess().Line());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ThisProcess_MainBundle_IsReadWithoutAnApplicationsMetadata()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Foundation");

        // The test host isn't an app bundle: a path (its folder), and no package type of an application's.
        var bundle = MacLoginItem.MainBundle();

        Assert.False(string.IsNullOrEmpty(bundle.Path));
        Assert.NotEqual("APPL", bundle.PackageType);
        Assert.False(MacLoginItem.IsAppBundle(bundle));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ServiceManagement_HasEveryMethodUsed_NoneCalled()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ServiceManagement");

        var service = MacAppService.TryCreate();

        Assert.NotNull(service);
        Assert.All(MacAppService.Methods, m => Assert.True(service.Has(m.Selector, m.OfClass), m.Selector));
    }
}
