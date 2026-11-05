using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.MacOS;
using Serilog;

namespace InfoPanel.Shell.MacOS;

/// <summary>
/// The menu-bar item on macOS through AppKit (libobjc interop, as <see cref="MacDesktopPlatform"/>): an
/// <c>NSStatusItem</c> whose button shows <see cref="ShellGlyph"/> as a template image (18 points, drawn at 2×) with the
/// engine's address as its tooltip, and an <c>NSMenu</c> rebuilt from the engine's model each time it opens
/// (<c>menuNeedsUpdate:</c>), its submenus and check marks included. Copied text goes to the general <c>NSPasteboard</c>. Prompts are <c>NSAlert</c>s run modally, the app brought forward first (an accessory app
/// isn't active), one at a time, each from a run-loop timer rather than from the main queue's callback: an alert run
/// inside a queued callback would hold the main queue, so nothing else posted (closing windows, removing the item, the
/// engine's stop) could run until someone answered it. The engine's stop (<see cref="Remove"/>) dismisses an open
/// prompt and answers every waiting one with its cancel button. The item's target is also the application's delegate: a system quit (<c>applicationShouldTerminate:</c>)
/// answers <c>NSTerminateLater</c> and asks the engine to stop; <see cref="MacMainLoop"/> lets the quit go on once the
/// host has stopped. Everything AppKit runs on the main thread, inside <see cref="MacMainLoop"/>.
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

    /// <summary><c>objc_msgSend</c> for a method of two objects that answers a <c>BOOL</c> (<c>setString:forType:</c>).</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte> SendBoolIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private static readonly Serilog.ILogger Logger = Log.ForContext<MacShellPlatform>();

    private static IntPtr _targetClass;
    private static MacShellPlatform? _current;

    private IntPtr _target;
    private IntPtr _item;
    private IntPtr _menu;
    private Func<ShellMenu>? _menuSource;
    private Func<string, Task>? _invoked;
    private readonly List<string> _ids = [];

    // Main thread only.
    private readonly Queue<(ShellPrompt Prompt, TaskCompletionSource<int> Answer)> _prompts = new();
    private bool _promptOpen;
    private bool _closed;

    public bool IsSupported => true;

    public event Action? TerminateRequested;

    public void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked) => loop.Post(() =>
    {
        if (_item != IntPtr.Zero)
            return;
        EnsureTarget();
        _menuSource = menu;
        _invoked = invoked;

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

    public void Remove() => loop.Post(() =>
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
    });

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

    private void EnsureTarget()
    {
        _current = this;
        if (_targetClass == IntPtr.Zero)
        {
            _targetClass = ObjC.DefineClass("InfoPanelShellTarget", "NSObject",
                ("menuNeedsUpdate:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MenuNeedsUpdate, "v@:@"),
                ("invoke:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Invoke, "v@:@"),
                ("prompt:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Prompt, "v@:@"),
                ("applicationShouldTerminate:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&ShouldTerminate, "Q@:@"));
        }

        if (_target == IntPtr.Zero)
            _target = ObjC.Send(ObjC.Send(_targetClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Rebuilds the menu from the engine's model as it opens.</summary>
    private void Rebuild(IntPtr menu)
    {
        ObjC.SendVoid(menu, ObjC.Sel("removeAllItems"));
        _ids.Clear();
        if (_menuSource is not { } source)
            return;
        AddLines(menu, source().Items);
    }

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

    [UnmanagedCallersOnly]
    private static void MenuNeedsUpdate(IntPtr self, IntPtr sel, IntPtr menu) => Guard(() => _current?.Rebuild(menu));

    [UnmanagedCallersOnly]
    private static void Invoke(IntPtr self, IntPtr sel, IntPtr sender) => Guard(() =>
    {
        if (_current is not { } shell || shell._invoked is not { } invoked)
            return;
        var tag = (int)ObjC.SendIntRet(sender, ObjC.Sel("tag"));
        if (tag >= 0 && tag < shell._ids.Count)
            _ = invoked(shell._ids[tag]);
    });

    [UnmanagedCallersOnly]
    private static void Prompt(IntPtr self, IntPtr sel, IntPtr argument) => Guard(() => _current?.ShowNextPrompt());

    [UnmanagedCallersOnly]
    private static nuint ShouldTerminate(IntPtr self, IntPtr sel, IntPtr app)
    {
        Guard(() =>
        {
            MacMainLoop.TerminateLater = true;
            _current?.TerminateRequested?.Invoke();
        });
        return TerminateLater;
    }

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
