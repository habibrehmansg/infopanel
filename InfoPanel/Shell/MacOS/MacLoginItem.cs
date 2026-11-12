using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.MacOS;
using Serilog;

namespace InfoPanel.Shell.MacOS;

/// <summary><c>SMAppServiceStatus</c>: whether the app is a login item.</summary>
internal enum MacLoginStatus
{
    NotRegistered = 0,
    Enabled = 1,
    RequiresApproval = 2,
    NotFound = 3,
}

/// <summary>The app's login item (a seam: <see cref="MacAppService"/> on macOS, a fake in the tests).</summary>
internal interface IMacLoginService
{
    /// <summary>The login item's status now; throws when it can't be read.</summary>
    MacLoginStatus Status();

    /// <summary>Makes the app a login item; throws when the system refuses.</summary>
    void Register();

    /// <summary>Removes the app from the login items; throws when the system refuses.</summary>
    void Unregister();

    /// <summary>Opens System Settings at General › Login Items.</summary>
    void OpenLoginItemsSettings();
}

/// <summary>
/// Start at Login, the macOS menu's own line above Quit (as Windows' Start when I sign in): the app itself as a
/// login item (<c>SMAppService.mainAppService</c>, macOS 13 and later), so it is listed, and can be turned off, in
/// System Settings › General › Login Items. Read each time the menu opens: ticked while it is on; "Start at Login (approve in
/// System Settings…)" while the user has to allow it there, which opens that pane; greyed when the status can't be read.
/// Choosing it does what the line showed when the menu opened, even if the status changed since. Only from an app
/// bundle (<see cref="IsAppBundle"/>): a <c>dotnet run</c> or a plain publish, even one in a folder named <c>.app</c>,
/// has no line. A bundle run from App Translocation's read-only copy (Gatekeeper's, for an app opened where it was
/// downloaded) can't be registered for its real place: the line is greyed and asks the user to move InfoPanel to
/// Applications.
/// </summary>
internal sealed class MacLoginItem(MacBundle bundle, IMacLoginService? service, Serilog.ILogger? logger = null)
{
    /// <summary>What choosing the line does: what the line last shown offered.</summary>
    private enum LineAction
    {
        None,
        Register,
        Unregister,
        OpenSettings,
    }

    /// <summary>The action of the line <see cref="Line"/> last gave (main thread only).</summary>
    private LineAction _shown;

    /// <summary>The line's id: the platform's own, not the model's.</summary>
    public const string Id = "macos.start-at-login";

    public const string Label = "Start at Login";

    public const string ApproveLabel = "Start at Login (approve in System Settings…)";

    public const string MoveLabel = "Move InfoPanel to Applications to start at login";

    private readonly Serilog.ILogger Logger = logger ?? Log.ForContext<MacLoginItem>();

    /// <summary>
    /// Whether <paramref name="main"/> is an application's bundle: its path ends in <c>.app</c> and its
    /// <c>Info.plist</c> gives an identifier and the package type <c>APPL</c>. A plain publish has neither, wherever it is
    /// (<c>NSBundle.mainBundle</c> of a program outside a bundle is its folder's, with no <c>Info.plist</c>).
    /// </summary>
    public static bool IsAppBundle(MacBundle main) =>
        !string.IsNullOrEmpty(main.Path) && main.Path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(main.Identifier)
        && main.PackageType == "APPL";

    /// <summary>
    /// Whether <paramref name="path"/> is in App Translocation's folder: the system's temporary folder
    /// (<c>/private/var/folders/…</c>, also seen as <c>/var/folders/…</c>) with an <c>AppTranslocation</c> folder in it.
    /// </summary>
    public static bool IsTranslocated(string path) =>
        (path.StartsWith("/private/var/folders/", StringComparison.Ordinal) || path.StartsWith("/var/folders/", StringComparison.Ordinal))
        && path.Contains("/AppTranslocation/", StringComparison.Ordinal);

    /// <summary>
    /// The menu's line as it is now, or null for none (outside an app bundle, or no ServiceManagement); what choosing it
    /// will do is kept for <see cref="Choose"/>.
    /// </summary>
    public ShellMenuItem? Line()
    {
        _shown = LineAction.None;
        if (!IsAppBundle(bundle) || service == null)
            return null;
        if (IsTranslocated(bundle.Path!))
            return new ShellMenuItem(Id, MoveLabel, ShellItemKind.Status);
        switch (ReadStatus())
        {
            case null:
                return new ShellMenuItem(Id, Label, ShellItemKind.Status);
            case MacLoginStatus.Enabled:
                _shown = LineAction.Unregister;
                return new ShellMenuItem(Id, Label) { Checked = true };
            case MacLoginStatus.RequiresApproval:
                _shown = LineAction.OpenSettings;
                return new ShellMenuItem(Id, ApproveLabel);
            default:
                _shown = LineAction.Register;
                return new ShellMenuItem(Id, Label);
        }
    }

    /// <summary>
    /// The line was chosen: it does what the line showed (<see cref="Line"/>), never the opposite when the status
    /// changed while the menu was open. Unticked registers, unless the app is a login item by now (on, or waiting for
    /// approval); ticked unregisters, unless it is off by now; the approve line opens System Settings at Login Items.
    /// Nothing for a greyed line or none. Never throws: a refusal is logged, and the next menu shows the real status.
    /// </summary>
    public void Choose()
    {
        var action = _shown;
        _shown = LineAction.None;
        if (action == LineAction.None || service == null)
            return;
        try
        {
            switch (action)
            {
                case LineAction.OpenSettings:
                    service.OpenLoginItemsSettings();
                    Logger.Information("Start at Login waits for approval: System Settings opened at Login Items");
                    return;
                case LineAction.Register when ReadStatus() is MacLoginStatus.Enabled or MacLoginStatus.RequiresApproval:
                    Logger.Information("Start at Login was already on");
                    return;
                case LineAction.Register:
                    service.Register();
                    Logger.Information("Start at Login turned on");
                    return;
                case LineAction.Unregister when ReadStatus() is MacLoginStatus.NotRegistered or MacLoginStatus.NotFound:
                    Logger.Information("Start at Login was already off");
                    return;
                case LineAction.Unregister:
                    service.Unregister();
                    Logger.Information("Start at Login turned off");
                    return;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Start at Login couldn't be changed");
        }
    }

    private MacLoginStatus? ReadStatus()
    {
        try
        {
            return service!.Status();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Start at Login couldn't be read");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="menu"/> with <paramref name="line"/> in a group of its own above Quit (at the end when there is no
    /// Quit); the menu itself when there is no line.
    /// </summary>
    public static ShellMenu WithLine(ShellMenu menu, ShellMenuItem? line)
    {
        if (line == null)
            return menu;
        var items = menu.Items.ToList();
        var quit = items.FindIndex(i => i.Id == ShellMenu.Quit && i.Kind != ShellItemKind.Separator);
        var head = quit < 0 ? items : items[..quit];
        var tail = quit < 0 ? [] : items[quit..];
        while (head.Count > 0 && head[^1].Kind == ShellItemKind.Separator)
            head = head[..^1];

        var separator = new ShellMenuItem("", "", ShellItemKind.Separator);
        var lines = new List<ShellMenuItem>(head);
        if (lines.Count > 0)
            lines.Add(separator);
        lines.Add(line);
        if (tail.Count > 0)
            lines.Add(separator);
        lines.AddRange(tail);
        return new ShellMenu(lines);
    }

    /// <summary>
    /// This process's line: its main bundle's path, and the app's login item when it runs from a bundle and
    /// ServiceManagement's <c>SMAppService</c> is there (macOS 13 and later).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static MacLoginItem ForThisProcess()
    {
        var main = MainBundle();
        if (!IsAppBundle(main))
            return new MacLoginItem(main, null);
        return new MacLoginItem(main, MacAppService.TryCreate());
    }

    /// <summary>
    /// This process's main bundle (<c>NSBundle.mainBundle</c>): its path and its <c>Info.plist</c>'s
    /// <c>CFBundleIdentifier</c> and <c>CFBundlePackageType</c> (<c>objectForInfoDictionaryKey:</c>; null when absent or
    /// not a string).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static unsafe MacBundle MainBundle()
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var bundle = ObjC.Send(ObjC.Class("NSBundle"), ObjC.Sel("mainBundle"));
            if (bundle == IntPtr.Zero)
                return new MacBundle(null, null, null);
            return new MacBundle(
                ObjC.ToManagedString(ObjC.Send(bundle, ObjC.Sel("bundlePath"))),
                InfoString(bundle, "CFBundleIdentifier"),
                InfoString(bundle, "CFBundlePackageType"));
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    [SupportedOSPlatform("macos")]
    private static unsafe string? InfoString(IntPtr bundle, string key)
    {
        var value = ObjC.SendId(bundle, ObjC.Sel("objectForInfoDictionaryKey:"), ObjC.NSString(key));
        if (value == IntPtr.Zero || MacAppService.SendBoolId(value, ObjC.Sel("isKindOfClass:"), ObjC.Class("NSString")) == 0)
            return null;
        return ObjC.ToManagedString(value);
    }
}

/// <summary>
/// The main bundle as the menu's line needs it: its path and its <c>Info.plist</c>'s <c>CFBundleIdentifier</c> and
/// <c>CFBundlePackageType</c> (null when it has none).
/// </summary>
internal sealed record MacBundle(string? Path, string? Identifier, string? PackageType);

/// <summary>
/// The app's own login item through ServiceManagement (loaded by path, through <see cref="ObjC"/>):
/// <c>[SMAppService mainAppService]</c>'s <c>status</c>, <c>registerAndReturnError:</c> and
/// <c>unregisterAndReturnError:</c>, and <c>+[SMAppService openSystemSettingsLoginItems]</c>. Main thread (the menu's).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacAppService : IMacLoginService
{
    private const string FrameworkPath = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";

    /// <summary><c>objc_msgSend</c> for a method that takes an <c>NSError **</c> and answers a <c>BOOL</c>.</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr*, byte> SendBoolError =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, byte>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for a method that takes an id (or a selector) and answers a <c>BOOL</c>.</summary>
    internal static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte> SendBoolId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary>The methods used: each selector, and whether it is the class's (else an instance's).</summary>
    internal static readonly (string Selector, bool OfClass)[] Methods =
    [
        ("mainAppService", true),
        ("openSystemSettingsLoginItems", true),
        ("status", false),
        ("registerAndReturnError:", false),
        ("unregisterAndReturnError:", false),
    ];

    private readonly IntPtr _class;

    private MacAppService(IntPtr serviceClass) => _class = serviceClass;

    /// <summary>Whether <c>SMAppService</c> has the method (asked of the runtime, nothing called).</summary>
    internal bool Has(string selector, bool ofClass) =>
        SendBoolId(_class, ObjC.Sel(ofClass ? "respondsToSelector:" : "instancesRespondToSelector:"), ObjC.Sel(selector)) != 0;

    /// <summary>The service, or null when ServiceManagement or <c>SMAppService</c> isn't there (before macOS 13).</summary>
    public static MacAppService? TryCreate()
    {
        try
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(13) || !NativeLibrary.TryLoad(FrameworkPath, out _))
                return null;
            return new MacAppService(ObjC.Class("SMAppService"));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public MacLoginStatus Status() => Call(main => (MacLoginStatus)ObjC.SendIntRet(main, ObjC.Sel("status")));

    public void Register() => Call(main => Check(main, "registerAndReturnError:"));

    public void Unregister() => Call(main => Check(main, "unregisterAndReturnError:"));

    public void OpenLoginItemsSettings() => ObjC.SendVoid(_class, ObjC.Sel("openSystemSettingsLoginItems"));

    private T Call<T>(Func<IntPtr, T> action)
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var main = ObjC.Send(_class, ObjC.Sel("mainAppService"));
            if (main == IntPtr.Zero)
                throw new InvalidOperationException("SMAppService gave no main app service.");
            return action(main);
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    private static bool Check(IntPtr main, string selector)
    {
        IntPtr error = IntPtr.Zero;
        if (SendBoolError(main, ObjC.Sel(selector), &error) != 0)
            return true;
        var reason = error == IntPtr.Zero ? null : ObjC.ToManagedString(ObjC.Send(error, ObjC.Sel("localizedDescription")));
        var code = error == IntPtr.Zero ? 0 : ObjC.SendIntRet(error, ObjC.Sel("code"));
        throw new InvalidOperationException($"SMAppService {selector} failed ({code}): {reason}");
    }
}
