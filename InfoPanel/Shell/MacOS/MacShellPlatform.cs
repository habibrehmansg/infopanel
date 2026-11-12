using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.MacOS;
using Serilog;

namespace InfoPanel.Shell.MacOS;

/// <summary>
/// The menu-bar item on macOS through AppKit (libobjc interop, as <see cref="MacDesktopPlatform"/>): an
/// <c>NSStatusItem</c> whose button shows <see cref="ShellGlyph"/> as a template image (18 points, drawn at 2×) with the
/// engine's address as its tooltip, and an <c>NSMenu</c> rebuilt from the engine's model each time it opens
/// (<c>menuNeedsUpdate:</c>), its submenus and check marks included: the header in the bold menu font, the status line in
/// the secondary colour, and the row of who is signed in, its second line in the secondary colour (attributed titles)
/// and its round initial as the item's image (<see cref="ShellInitial"/>). Copied text goes to the general <c>NSPasteboard</c>. Prompts are <c>NSAlert</c>s run modally, the app brought forward first (an accessory app
/// isn't active), one at a time, each from a run-loop timer rather than from the main queue's callback: an alert run
/// inside a queued callback would hold the main queue, so nothing else posted (closing windows, removing the item, the
/// engine's stop) could run until someone answered it. The engine's stop (<see cref="Remove"/>) dismisses an open
/// prompt and answers every waiting one with its cancel button. The item's target is also the application's delegate: a system quit (<c>applicationShouldTerminate:</c>)
/// answers <c>NSTerminateLater</c> and asks the engine to stop; <see cref="MacMainLoop"/> lets the quit go on once the
/// host has stopped. Opening the app again while it runs (Finder or the Dock: <c>applicationShouldHandleReopen:hasVisibleWindows:</c>)
/// raises <see cref="ReopenRequested"/>, which opens the editor; so does an <c>infopanel:</c> link (<c>application:openURLs:</c>,
/// the URLs never read). The delegate is set before the AppKit loop runs (<see cref="InstallDelegate"/>), so a link that
/// starts the app isn't lost; until the item shows, a system quit goes on at once. The menu's own Start at Login line
/// (<see cref="MacLoginItem"/>, only from an app bundle) is handled here, not by the engine. Everything AppKit runs on the
/// main thread, inside <see cref="MacMainLoop"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacShellPlatform(MacMainLoop loop) : IShellPlatform
{
    /// <summary>The glyph's size in points; its image is drawn at 2× for Retina menu bars.</summary>
    private const double GlyphPoints = 18;

    /// <summary><c>NSVariableStatusItemLength</c>.</summary>
    private const double VariableLength = -1;

    /// <summary><c>NSTerminateLater</c>.</summary>
    private const nuint TerminateLater = 2;

    /// <summary><c>NSAlertFirstButtonReturn</c>: the first button's answer; the others follow it.</summary>
    private const nint FirstButtonReturn = 1000;

    /// <summary><c>NSEventTypeApplicationDefined</c>, posted to wake the loop after a prompt is dismissed.</summary>
    private const nuint ApplicationDefinedEvent = 15;

    /// <summary><c>NSControlStateValueOn</c>: a menu item's check mark.</summary>
    private const nint ControlStateOn = 1;

    /// <summary><c>NSPasteboardTypeString</c>'s value.</summary>
    private const string PlainTextType = "public.utf8-plain-text";

    /// <summary>The round initial's size in points; its image is drawn at 2×.</summary>
    private const double InitialPoints = 26;

    /// <summary><c>objc_msgSend</c> for a method of two objects that answers a <c>BOOL</c> (<c>setString:forType:</c>).</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte> SendBoolIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for a method of two objects that answers an object (<c>initWithString:attributes:</c>).</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> SendIdIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for <c>dictionaryWithObjects:forKeys:count:</c>.</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr*, IntPtr*, nuint, IntPtr> SendDictionary =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, IntPtr*, nuint, IntPtr>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private static readonly Serilog.ILogger Logger = Log.ForContext<MacShellPlatform>();

    private static IntPtr _targetClass;
    private static MacShellPlatform? _current;

    private IntPtr _target;
    private IntPtr _item;
    private IntPtr _menu;
    private Func<ShellMenu>? _menuSource;
    private Func<string, Task>? _invoked;
    private MacLoginItem? _login;
    private readonly List<string> _ids = [];

    // Main thread only.
    private readonly Queue<(ShellPrompt Prompt, TaskCompletionSource<int> Answer)> _prompts = new();
    private bool _promptOpen;
    private bool _closed;

    /// <summary><c>NSTerminateNow</c>: AppKit's own answer to a system quit.</summary>
    private const nuint TerminateNow = 1;

    // Main thread only: whether the item has shown (a system quit before it goes on at once).
    private bool _shown;

    private readonly Lock _reopenLock = new();
    private Action? _reopen;
    private bool _reopenMissed;

    public bool IsSupported => true;

    public event Action? TerminateRequested;

    /// <summary>
    /// Raised when the app is opened again or an <c>infopanel:</c> link is opened. One raised before anyone listens (a
    /// link that started the app arrives while AppKit finishes launching, before the engine's services start) is kept
    /// and given to the first listener as it subscribes, once.
    /// </summary>
    public event Action? ReopenRequested
    {
        add
        {
            bool missed;
            lock (_reopenLock)
            {
                _reopen += value;
                missed = _reopenMissed && value != null;
                if (missed)
                    _reopenMissed = false;
            }

            if (missed)
                value!();
        }
        remove
        {
            lock (_reopenLock)
                _reopen -= value;
        }
    }

    private void RaiseReopen()
    {
        Action? handlers;
        lock (_reopenLock)
        {
            handlers = _reopen;
            if (handlers == null)
                _reopenMissed = true;
        }

        handlers?.Invoke();
    }

    /// <summary>
    /// Makes the item's target the application's delegate now, on the main thread before the AppKit loop runs, so a link
    /// that started the app (delivered while AppKit finishes launching) reaches it. Until the item shows, the delegate
    /// lets a system quit go on at once, as AppKit's default does.
    /// </summary>
    internal void InstallDelegate()
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            EnsureTarget();
            var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.SendVoidId(app, ObjC.Sel("setDelegate:"), _target);
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    public void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked) => loop.Post(() =>
    {
        if (_item != IntPtr.Zero)
            return;
        EnsureTarget();
        _shown = true;
        _menuSource = menu;
        _invoked = invoked;
        _login = MacLoginItem.ForThisProcess();

        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        _item = ObjC.SendIdDouble(bar, ObjC.Sel("statusItemWithLength:"), VariableLength);
        ObjC.Send(_item, ObjC.Sel("retain"));
        var button = ObjC.Send(_item, ObjC.Sel("button"));
        ObjC.SendVoidId(button, ObjC.Sel("setImage:"), Glyph());
        ObjC.SendVoidId(button, ObjC.Sel("setToolTip:"), ObjC.NSString(icon.Tooltip));

        _menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.SendVoidBool(_menu, ObjC.Sel("setAutoenablesItems:"), 0);
        ObjC.SendVoidId(_menu, ObjC.Sel("setDelegate:"), _target);
        ObjC.SendVoidId(_item, ObjC.Sel("setMenu:"), _menu);

        // A system quit stops the engine as Quit does, without asking.
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.SendVoidId(app, ObjC.Sel("setDelegate:"), _target);
        Logger.Information("Menu bar item added");
    });

    public void Remove()
    {
        // The engine is stopping: no notification is shown after.
        MacNotifications.Stop();
        loop.Post(RemoveOnLoop);
    }

    private void RemoveOnLoop()
    {
        // The engine is stopping: no prompt waits for an answer any more.
        _closed = true;
        while (_prompts.TryDequeue(out var waiting))
            waiting.Answer.TrySetResult(waiting.Prompt.CancelButton);
        if (_promptOpen)
            DismissPrompt();

        if (_item == IntPtr.Zero)
            return;
        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        ObjC.SendVoidId(bar, ObjC.Sel("removeStatusItem:"), _item);
        ObjC.SendVoid(_item, ObjC.Sel("release"));
        _item = IntPtr.Zero;
        if (_menu != IntPtr.Zero)
        {
            ObjC.SendVoid(_menu, ObjC.Sel("release"));
            _menu = IntPtr.Zero;
        }
        Logger.Information("Menu bar item removed");
    }

    public Task<int> ConfirmAsync(ShellPrompt prompt)
    {
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Post(() =>
        {
            if (_closed)
            {
                answer.TrySetResult(prompt.CancelButton);
                return;
            }

            EnsureTarget();
            _prompts.Enqueue((prompt, answer));
            SchedulePrompt();
        });
        return answer.Task;
    }

    /// <summary>
    /// Shows the next prompt from a run-loop timer (<c>performSelector:withObject:afterDelay:</c>), outside the main queue's
    /// callback this runs in, so the alert's modal loop goes on serving the main queue.
    /// </summary>
    private void SchedulePrompt() =>
        ObjC.SendVoidSelIdDouble(_target, ObjC.Sel("performSelector:withObject:afterDelay:"), ObjC.Sel("prompt:"), IntPtr.Zero, 0);

    /// <summary>Runs the next waiting prompt, unless one is open (it schedules the next when it closes).</summary>
    private void ShowNextPrompt()
    {
        if (_promptOpen || !_prompts.TryDequeue(out var next))
            return;
        if (_closed)
        {
            next.Answer.TrySetResult(next.Prompt.CancelButton);
            return;
        }

        _promptOpen = true;
        try
        {
            next.Answer.TrySetResult(RunAlert(next.Prompt));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The menu bar item's prompt failed");
            next.Answer.TrySetResult(next.Prompt.CancelButton);
        }
        finally
        {
            _promptOpen = false;
        }

        if (_prompts.Count > 0)
            SchedulePrompt();
    }

    /// <summary>
    /// Ends the open prompt's modal loop (<c>abortModal</c>: <c>runModal</c> answers <c>NSModalResponseAbort</c>, read as
    /// the cancel button), then wakes the loop with an application-defined event so it notices.
    /// </summary>
    private static void DismissPrompt()
    {
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.SendVoid(app, ObjC.Sel("abortModal"));
        var wake = ObjC.SendOtherEvent(ObjC.Class("NSEvent"),
            ObjC.Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            ApplicationDefinedEvent, new NSPoint(0, 0), 0, 0, 0, IntPtr.Zero, 0, 0, 0);
        ObjC.SendVoidIdBool(app, ObjC.Sel("postEvent:atStart:"), wake, 1);
        Logger.Information("The menu bar item's prompt was dismissed: the engine is stopping");
    }

    /// <summary>
    /// An <c>NSAlert</c> with the prompt's buttons in order (AppKit puts the first on the right): Return chooses the default,
    /// Escape the cancel button. Brought forward first, since an accessory app isn't active.
    /// </summary>
    private static int RunAlert(ShellPrompt prompt)
    {
        var alert = ObjC.Send(ObjC.Send(ObjC.Class("NSAlert"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        try
        {
            ObjC.SendVoidId(alert, ObjC.Sel("setMessageText:"), ObjC.NSString(prompt.Title));
            ObjC.SendVoidId(alert, ObjC.Sel("setInformativeText:"), ObjC.NSString(prompt.Text));
            var buttons = new List<IntPtr>();
            foreach (var label in prompt.Buttons)
                buttons.Add(ObjC.SendId(alert, ObjC.Sel("addButtonWithTitle:"), ObjC.NSString(label)));
            for (var i = 0; i < buttons.Count; i++)
            {
                var key = i == prompt.DefaultButton ? "\r" : i == prompt.CancelButton ? "\u001b" : "";
                ObjC.SendVoidId(buttons[i], ObjC.Sel("setKeyEquivalent:"), ObjC.NSString(key));
            }

            var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.SendVoidBool(app, ObjC.Sel("activateIgnoringOtherApps:"), 1);
            Logger.Information("The menu bar item's prompt is open");
            var chosen = (int)(ObjC.SendIntRet(alert, ObjC.Sel("runModal")) - FirstButtonReturn);
            return chosen >= 0 && chosen < prompt.Buttons.Count ? chosen : prompt.CancelButton;
        }
        finally
        {
            ObjC.SendVoid(alert, ObjC.Sel("release"));
        }
    }

    /// <summary>The glyph as a template <c>NSImage</c> of <see cref="GlyphPoints"/> points, drawn at 2×.</summary>
    private static IntPtr Glyph()
    {
        var png = ShellGlyph.Png((int)(GlyphPoints * 2));
        IntPtr data;
        fixed (byte* bytes = png)
            data = ObjC.SendIdPtrUInt(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), (IntPtr)bytes, (nuint)png.Length);
        var image = ObjC.SendId(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
        ObjC.SendVoidSize(image, ObjC.Sel("setSize:"), new NSSize(GlyphPoints, GlyphPoints));
        ObjC.SendVoidBool(image, ObjC.Sel("setTemplate:"), 1);
        ObjC.Send(image, ObjC.Sel("autorelease"));
        return image;
    }

    /// <summary>
    /// The reopen method's type encoding: <c>BOOL</c> is <c>bool</c> (<c>B</c>) on Apple silicon and <c>signed char</c>
    /// (<c>c</c>) on Intel, and the managed method takes and returns that one byte.
    /// </summary>
    private static string ReopenTypes =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@B" : "c@:@c";

    private void EnsureTarget()
    {
        _current = this;
        if (_targetClass == IntPtr.Zero)
        {
            _targetClass = ObjC.DefineClass("InfoPanelShellTarget", "NSObject",
                ("menuNeedsUpdate:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MenuNeedsUpdate, "v@:@"),
                ("invoke:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Invoke, "v@:@"),
                ("prompt:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Prompt, "v@:@"),
                ("applicationShouldTerminate:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&ShouldTerminate, "Q@:@"),
                ("applicationShouldHandleReopen:hasVisibleWindows:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, byte>)&ShouldHandleReopen, ReopenTypes),
                ("application:openURLs:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OpenUrls, "v@:@@"));
        }

        if (_target == IntPtr.Zero)
            _target = ObjC.Send(ObjC.Send(_targetClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Rebuilds the menu from the engine's model as it opens, with the platform's own Start at Login above Quit (<see cref="MacLoginItem"/>).</summary>
    private void Rebuild(IntPtr menu)
    {
        if (_menuSource is { } source)
            Fill(menu, MacLoginItem.WithLine(source(), _login?.Line()));
        else
            Fill(menu, new ShellMenu([]));
    }

    /// <summary>
    /// <paramref name="menu"/>'s items made afresh from <paramref name="model"/>, on the main thread (a test builds a menu
    /// that is never shown on any thread).
    /// </summary>
    internal void Fill(IntPtr menu, ShellMenu model)
    {
        ObjC.SendVoid(menu, ObjC.Sel("removeAllItems"));
        _ids.Clear();
        EnsureTarget();
        AddLines(menu, model.Items);
    }

    /// <summary>The model's id of the item tagged <paramref name="tag"/> (what a click on it reports), or null.</summary>
    internal string? IdOf(nint tag) => tag >= 0 && tag < _ids.Count ? _ids[(int)tag] : null;

    /// <summary>
    /// Adds <paramref name="lines"/> to <paramref name="menu"/>: separators, greyed status lines, actions (each tagged with
    /// its index in <see cref="_ids"/>, check marks as the model says) and submenus, each an <c>NSMenu</c> of its own built
    /// now with the rest (only the top menu is rebuilt as it opens, and a submenu is part of it).
    /// </summary>
    private void AddLines(IntPtr menu, IReadOnlyList<ShellMenuItem> lines)
    {
        foreach (var line in lines)
        {
            if (line.Kind == ShellItemKind.Separator)
            {
                ObjC.SendVoidId(menu, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem")));
                continue;
            }

            var action = line.Kind == ShellItemKind.Action ? ObjC.Sel("invoke:") : IntPtr.Zero;
            var item = ObjC.SendIdIdSelId(ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
                ObjC.NSString(line.Label), action, ObjC.NSString(line.KeyEquivalent ?? ""));
            ObjC.SendVoidInt(item, ObjC.Sel("setTag:"), _ids.Count);
            _ids.Add(line.Id);
            if (action != IntPtr.Zero)
                ObjC.SendVoidId(item, ObjC.Sel("setTarget:"), _target);
            if (line.Checked)
                ObjC.SendVoidInt(item, ObjC.Sel("setState:"), ControlStateOn);
            if (Title(line) is var title && title != IntPtr.Zero)
            {
                ObjC.SendVoidId(item, ObjC.Sel("setAttributedTitle:"), title);
                ObjC.SendVoid(title, ObjC.Sel("release"));
            }

            if (line.Initial is { } letter)
                ObjC.SendVoidId(item, ObjC.Sel("setImage:"), Initial(letter));
            if (line.Kind == ShellItemKind.Submenu)
            {
                var submenu = ObjC.SendId(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:"), ObjC.NSString(line.Label));
                ObjC.SendVoidBool(submenu, ObjC.Sel("setAutoenablesItems:"), 0);
                AddLines(submenu, line.Children);
                ObjC.SendVoidId(item, ObjC.Sel("setSubmenu:"), submenu);
                ObjC.SendVoid(submenu, ObjC.Sel("release"));
            }

            ObjC.SendVoidBool(item, ObjC.Sel("setEnabled:"), line.Enabled ? (byte)1 : (byte)0);
            ObjC.SendVoidId(menu, ObjC.Sel("addItem:"), item);
            ObjC.SendVoid(item, ObjC.Sel("release"));
        }
    }

    /// <summary>
    /// The line's attributed title (owned by the caller), or <see cref="IntPtr.Zero"/> for a plain one: the header in the
    /// bold menu font and the label colour (a disabled item keeps the colour its title names, so it isn't greyed); a status
    /// line in the secondary colour; a line with a second line (who is signed in) as its label in the menu font and the
    /// label colour (named: an attributed title without a colour is drawn black, unreadable on a dark menu), then its
    /// second line beneath in the small system size and the secondary colour; the colours the other way round when the
    /// second line is what the line does (Sign In…), so the row doesn't read as disabled.
    /// </summary>
    private static IntPtr Title(ShellMenuItem line)
    {
        var font = ObjC.Class("NSFont");
        var colour = ObjC.Class("NSColor");
        var menuFont = ObjC.SendIdDouble(font, ObjC.Sel("menuFontOfSize:"), 0);
        switch (line.Kind)
        {
            case ShellItemKind.Header:
                return Text(line.Label, ObjC.SendIdDouble(font, ObjC.Sel("boldSystemFontOfSize:"), ObjC.SendDouble(menuFont, ObjC.Sel("pointSize"))),
                    ObjC.Send(colour, ObjC.Sel("labelColor")));
            case ShellItemKind.Status:
                return Text(line.Label, menuFont, ObjC.Send(colour, ObjC.Sel("secondaryLabelColor")));
        }

        if (line.Detail is not { } detail)
            return IntPtr.Zero;
        var primary = ObjC.Send(colour, ObjC.Sel("labelColor"));
        var secondary = ObjC.Send(colour, ObjC.Sel("secondaryLabelColor"));
        var title = Text(line.Label + "\n", menuFont, line.DetailIsAction ? secondary : primary);
        var beneath = Text(detail, ObjC.SendIdDouble(font, ObjC.Sel("menuFontOfSize:"), ObjC.SendDouble(font, ObjC.Sel("smallSystemFontSize"))),
            line.DetailIsAction ? primary : secondary);
        ObjC.SendVoidId(title, ObjC.Sel("appendAttributedString:"), beneath);
        ObjC.SendVoid(beneath, ObjC.Sel("release"));
        return title;
    }

    /// <summary>An <c>NSMutableAttributedString</c> (owned by the caller) of <paramref name="text"/> in <paramref name="font"/> and <paramref name="colour"/> (none: zero).</summary>
    private static IntPtr Text(string text, IntPtr font, IntPtr colour)
    {
        var keys = stackalloc IntPtr[] { ObjC.Constant("AppKit", "NSFontAttributeName"), ObjC.Constant("AppKit", "NSForegroundColorAttributeName") };
        var values = stackalloc IntPtr[] { font, colour };
        var attributes = SendDictionary(ObjC.Class("NSDictionary"), ObjC.Sel("dictionaryWithObjects:forKeys:count:"), values, keys, colour == IntPtr.Zero ? 1u : 2u);
        return SendIdIdId(ObjC.Send(ObjC.Class("NSMutableAttributedString"), ObjC.Sel("alloc")), ObjC.Sel("initWithString:attributes:"), ObjC.NSString(text), attributes);
    }

    /// <summary>The round initial of <paramref name="letter"/> as an <c>NSImage</c> of <see cref="InitialPoints"/> points, drawn at 2×, in its own colours (not a template).</summary>
    private static IntPtr Initial(string letter)
    {
        var png = ShellInitial.Png(letter, (int)(InitialPoints * 2));
        IntPtr data;
        fixed (byte* bytes = png)
            data = ObjC.SendIdPtrUInt(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), (IntPtr)bytes, (nuint)png.Length);
        var image = ObjC.SendId(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
        ObjC.SendVoidSize(image, ObjC.Sel("setSize:"), new NSSize(InitialPoints, InitialPoints));
        ObjC.Send(image, ObjC.Sel("autorelease"));
        return image;
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the general pasteboard as plain text, on the main thread: <c>clearContents</c>, then
    /// <c>setString:forType:</c> with <c>public.utf8-plain-text</c> (<c>NSPasteboardTypeString</c>).
    /// </summary>
    public void CopyText(string text) => loop.Post(() =>
    {
        var board = ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"));
        ObjC.Send(board, ObjC.Sel("clearContents"));
        if (SendBoolIdId(board, ObjC.Sel("setString:forType:"), ObjC.NSString(text), ObjC.NSString(PlainTextType)) == 0)
            Logger.Warning("The engine's details couldn't be put on the clipboard");
    });

    /// <summary>Shows <paramref name="note"/> in the Notification Centre (<see cref="MacNotifications"/>): only from the app bundle.</summary>
    public void Notify(ShellNotification note, Func<Task> clicked) => MacNotifications.Show(note, clicked);

    [UnmanagedCallersOnly]
    private static void MenuNeedsUpdate(IntPtr self, IntPtr sel, IntPtr menu) => Guard(() => _current?.Rebuild(menu));

    [UnmanagedCallersOnly]
    private static void Invoke(IntPtr self, IntPtr sel, IntPtr sender) => Guard(() =>
    {
        if (_current is not { } shell || shell._invoked is not { } invoked)
            return;
        var tag = (int)ObjC.SendIntRet(sender, ObjC.Sel("tag"));
        if (tag < 0 || tag >= shell._ids.Count)
            return;
        if (shell._ids[tag] == MacLoginItem.Id)
            shell._login?.Choose();
        else
            _ = invoked(shell._ids[tag]);
    });

    [UnmanagedCallersOnly]
    private static void Prompt(IntPtr self, IntPtr sel, IntPtr argument) => Guard(() => _current?.ShowNextPrompt());

    /// <summary>
    /// A system quit: once the item has shown, answers <c>NSTerminateLater</c> and asks the engine to stop; before then
    /// (the delegate is set before the loop runs), <c>NSTerminateNow</c>, as AppKit does without a delegate.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nuint ShouldTerminate(IntPtr self, IntPtr sel, IntPtr app)
    {
        if (_current is not { _shown: true })
            return TerminateNow;
        Guard(() =>
        {
            MacMainLoop.TerminateLater = true;
            _current?.TerminateRequested?.Invoke();
        });
        return TerminateLater;
    }

    /// <summary>
    /// The app was opened again while it runs: the engine opens the editor. Answers NO, so AppKit does nothing more (an
    /// accessory app has no window of its own to bring back).
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte ShouldHandleReopen(IntPtr self, IntPtr sel, IntPtr app, byte hasVisibleWindows)
    {
        Guard(() => _current?.RaiseReopen());
        return 0;
    }

    /// <summary>
    /// An <c>infopanel:</c> link was opened (a web page, Finder, <c>open</c>): the same request as opening the app again,
    /// which opens the editor. <paramref name="urls"/> is never read: nothing of the link is parsed, logged or passed on.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OpenUrls(IntPtr self, IntPtr sel, IntPtr app, IntPtr urls) => Guard(() =>
    {
        Logger.Information("InfoPanel was opened from a link: opening the editor");
        _current?.RaiseReopen();
    });

    /// <summary>Exceptions must never unwind into Objective-C.</summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A menu bar item callback failed");
        }
    }
}
