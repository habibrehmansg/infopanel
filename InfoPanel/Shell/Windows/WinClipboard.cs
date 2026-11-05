using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static InfoPanel.Shell.Windows.WinShellInterop;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// The Windows clipboard as plain text (<c>CF_UNICODETEXT</c>), for Copy engine details. On the UI thread, owned by its
/// hidden window: a clipboard opened without an owner can't be written once emptied.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WinClipboard
{
    /// <summary>How many times opening it is tried: another program may hold it for a moment.</summary>
    private const int Tries = 5;

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard, owned by <paramref name="owner"/>, with Windows' line endings
    /// (CR LF, as <c>CF_UNICODETEXT</c> expects); false when it couldn't.
    /// </summary>
    public static bool SetText(nint owner, string text)
    {
        text = text.ReplaceLineEndings("\r\n");
        if (!Open(owner))
            return false;
        try
        {
            if (EmptyClipboard() == 0)
                return false;

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (memory == 0)
                return false;
            var target = (char*)GlobalLock(memory);
            if (target == null)
            {
                GlobalFree(memory);
                return false;
            }

            text.AsSpan().CopyTo(new Span<char>(target, text.Length));
            target[text.Length] = '\0';
            GlobalUnlock(memory);

            // Once set, the memory is the system's; until then, still ours to free.
            if (SetClipboardData(CF_UNICODETEXT, memory) != 0)
                return true;
            GlobalFree(memory);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>The clipboard's plain text, or null when it holds none or couldn't be opened.</summary>
    public static string? GetText(nint owner)
    {
        if (!Open(owner))
            return null;
        try
        {
            var memory = GetClipboardData(CF_UNICODETEXT);
            if (memory == 0)
                return null;
            var text = (char*)GlobalLock(memory);
            if (text == null)
                return null;
            try
            {
                return new string(text);
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Open(nint owner)
    {
        for (var i = 0; i < Tries; i++)
        {
            if (OpenClipboard(owner) != 0)
                return true;
            Thread.Sleep(10);
        }

        return false;
    }
}
