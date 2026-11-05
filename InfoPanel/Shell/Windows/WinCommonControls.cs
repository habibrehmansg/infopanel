using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using static InfoPanel.Shell.Windows.WinShellInterop;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// Common Controls 6, whose task dialog the notification-area icon's prompts use, whichever program runs the engine.
/// <c>InfoPanel.exe</c> carries the engine's manifest (<c>app.manifest</c>), but under the <c>dotnet</c> host the
/// program is <c>dotnet.exe</c>, whose manifest doesn't ask for it, and comctl32 would be the old one, without
/// <c>TaskDialogIndirect</c>. So the prompt runs inside an activation context made from the same manifest, which the
/// compiler also embeds in the engine's assembly (<c>InfoPanel.dll</c>, resource 1). UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WinCommonControls
{
    private static readonly Serilog.ILogger Logger = Log.ForContext(typeof(WinCommonControls));

    /// <summary>The context, made once; <see cref="INVALID_HANDLE_VALUE"/> when it couldn't be (the program's own applies).</summary>
    private static readonly Lazy<nint> Context = new(Create);

    /// <summary>Activates Common Controls 6 on this thread until the scope is disposed.</summary>
    public static Scope Enter()
    {
        var context = Context.Value;
        if (context == INVALID_HANDLE_VALUE)
            return default;

        nuint cookie;
        if (ActivateActCtx(context, &cookie) == 0)
        {
            Logger.Warning("Common Controls 6 couldn't be activated ({Error})", Marshal.GetLastPInvokeError());
            return default;
        }

        return new Scope(cookie, true);
    }

    /// <summary>An activation, undone when disposed.</summary>
    public readonly struct Scope(nuint cookie, bool active) : IDisposable
    {
        /// <summary>Whether the engine's context was activated (false: the program's own applies).</summary>
        public bool Active => active;

        public void Dispose()
        {
            if (active && DeactivateActCtx(0, cookie) == 0)
                Logger.Warning("Common Controls 6 couldn't be deactivated ({Error})", Marshal.GetLastPInvokeError());
        }
    }

    private static nint Create()
    {
        // Empty in a single-file build, whose executable carries the manifest itself.
        var assembly = typeof(WinCommonControls).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
            return INVALID_HANDLE_VALUE;

        fixed (char* source = assembly)
        {
            var description = new ACTCTXW
            {
                CbSize = (uint)sizeof(ACTCTXW),
                DwFlags = ACTCTX_FLAG_RESOURCE_NAME_VALID,
                LpSource = source,
                LpResourceName = (char*)CREATEPROCESS_MANIFEST_RESOURCE_ID,
            };
            var context = CreateActCtxW(&description);
            if (context == INVALID_HANDLE_VALUE)
                Logger.Warning("Common Controls 6's activation context couldn't be made from {Assembly} ({Error})", assembly, Marshal.GetLastPInvokeError());
            return context;
        }
    }
}
