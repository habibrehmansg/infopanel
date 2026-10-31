using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Hosting;

/// <summary>
/// The default web browser: the app the system opens <c>http</c> links with, which <see cref="BrowserLauncher"/> hands the
/// redirect file to by name. Opening the file by its association instead would reach whatever opens <c>.html</c> files,
/// which may be a text editor. Each OS answers in its own form (<see cref="Find"/>), and <see cref="BrowserLauncher"/> turns
/// it into the command that opens a file with it:
/// <list type="bullet">
/// <item>macOS: the app's path, from Launch Services (<c>LSCopyDefaultApplicationURLForURL</c> for an <c>http</c> URL);
/// opened with <c>open -a {app} {file}</c>.</item>
/// <item>Windows: the command of the <c>http</c> protocol's association (the user's choice, else the computer's), its
/// <c>%1</c> the file (<see cref="WindowsCommand"/>).</item>
/// <item>Linux: the <c>Exec</c> line of the desktop entry <c>xdg-settings get default-web-browser</c> names, found in the
/// XDG data folders (<see cref="LinuxExec"/>), its field code the file (<see cref="LinuxCommand"/>).</item>
/// </list>
/// Null when it can't be told; the launcher then opens the file by its association, as the last resort.
/// </summary>
public static partial class DefaultBrowser
{
    /// <summary>The default web browser on <paramref name="os"/> (this computer's), in that OS's form; null when it can't be told.</summary>
    public static string? Find(BrowserOS os)
    {
        try
        {
            if (os == BrowserOS.MacOS && OperatingSystem.IsMacOS())
                return MacApp();
            if (os == BrowserOS.Windows && OperatingSystem.IsWindows())
                return WindowsTemplate();
            if (os == BrowserOS.Linux && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return LinuxExec(LinuxDataDirectories(), XdgSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
                                       or System.ComponentModel.Win32Exception or System.Security.SecurityException or InvalidOperationException)
        {
        }

        return null;
    }

    // ---- macOS ----------------------------------------------------------------------------------------------------------

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const uint AllRoles = 0xFFFFFFFF; // kLSRolesAll

    /// <summary>The path of the app Launch Services opens <c>http</c> links with, or null.</summary>
    [SupportedOSPlatform("macos")]
    public static string? MacApp()
    {
        var text = CFStringCreateWithCString(IntPtr.Zero, "http://127.0.0.1/", Utf8);
        if (text == IntPtr.Zero)
            return null;
        var url = IntPtr.Zero;
        var app = IntPtr.Zero;
        try
        {
            url = CFURLCreateWithString(IntPtr.Zero, text, IntPtr.Zero);
            if (url == IntPtr.Zero)
                return null;
            app = LSCopyDefaultApplicationURLForURL(url, AllRoles, IntPtr.Zero);
            if (app == IntPtr.Zero)
                return null;
            var buffer = new byte[4096];
            if (!CFURLGetFileSystemRepresentation(app, true, buffer, buffer.Length))
                return null;
            var end = Array.IndexOf(buffer, (byte)0);
            var path = Path.TrimEndingDirectorySeparator(Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end));
            return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path) ? path : null;
        }
        finally
        {
            if (app != IntPtr.Zero)
                CFRelease(app);
            if (url != IntPtr.Zero)
                CFRelease(url);
            CFRelease(text);
        }
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateWithString(IntPtr allocator, IntPtr text, IntPtr baseUrl);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFURLGetFileSystemRepresentation(IntPtr url, [MarshalAs(UnmanagedType.U1)] bool resolveAgainstBase, [Out] byte[] buffer, nint length);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyDefaultApplicationURLForURL(IntPtr url, uint roles, IntPtr error);

    // ---- Windows --------------------------------------------------------------------------------------------------------

    /// <summary>The <c>http</c> protocol's open command: the user's choice (its ProgId), else the computer's.</summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsTemplate()
    {
        string? progId;
        using (var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
            progId = choice?.GetValue("ProgId") as string;
        var key = string.IsNullOrWhiteSpace(progId) ? @"http\shell\open\command" : $@"{progId}\shell\open\command";
        using var command = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(key);
        return command?.GetValue(null) as string is { Length: > 0 } template ? template : null;
    }

    /// <summary>
    /// The command an association's open command <paramref name="template"/> runs for <paramref name="file"/>: its program
    /// (quoted or up to the first space, environment variables expanded), and the rest as the command line with <c>%1</c>,
    /// <c>%L</c> and <c>%l</c> the file (quoted unless the template quotes it), <c>%*</c> dropped and <c>%%</c> a percent sign;
    /// with none of those the file is added at the end. Null for a template with no program.
    /// </summary>
    public static ProcessStartInfo? WindowsCommand(string template, string file)
    {
        template = template.Trim();
        string program;
        string rest;
        if (template.StartsWith('"'))
        {
            var end = template.IndexOf('"', 1);
            if (end < 0)
                return null;
            program = template[1..end];
            rest = template[(end + 1)..];
        }
        else
        {
            var space = template.IndexOf(' ');
            program = space < 0 ? template : template[..space];
            rest = space < 0 ? "" : template[space..];
        }

        program = Environment.ExpandEnvironmentVariables(program).Trim();
        if (program.Length == 0)
            return null;

        var arguments = new StringBuilder();
        var quoted = false;
        var placed = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '"')
                quoted = !quoted;
            if (c != '%' || i + 1 >= rest.Length)
            {
                arguments.Append(c);
                continue;
            }

            switch (rest[++i])
            {
                case '1' or 'L' or 'l':
                    arguments.Append(quoted ? file : $"\"{file}\"");
                    placed = true;
                    break;
                case '*':
                    break;
                case '%':
                    arguments.Append('%');
                    break;
                default:
                    arguments.Append('%').Append(rest[i]);
                    break;
            }
        }

        var line = arguments.ToString().Trim();
        if (!placed)
            line = line.Length == 0 ? $"\"{file}\"" : $"{line} \"{file}\"";
        return new ProcessStartInfo(program) { Arguments = line, UseShellExecute = false };
    }

    // ---- Linux ----------------------------------------------------------------------------------------------------------

    /// <summary>The XDG data folders in order: <c>$XDG_DATA_HOME</c> (<c>~/.local/share</c>), then <c>$XDG_DATA_DIRS</c>.</summary>
    private static IReadOnlyList<string> LinuxDataDirectories()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dataDirs ? dataDirs : "/usr/local/share:/usr/share";
        return [home, .. dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>What <c>xdg-settings get default-web-browser</c> prints, or null when it fails or takes over 2 s.</summary>
    private static string? XdgSettings()
    {
        var start = new ProcessStartInfo("xdg-settings") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("get");
        start.ArgumentList.Add("default-web-browser");
        using var process = Process.Start(start);
        if (process == null)
            return null;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return null;
        }

        return process.ExitCode == 0 && output.Wait(TimeSpan.FromSeconds(1)) ? output.Result : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]*\.desktop$")]
    private static partial Regex DesktopId();

    /// <summary>
    /// The <c>Exec</c> line (its string escapes undone) of the desktop entry <paramref name="settings"/> names (what
    /// <c>xdg-settings get default-web-browser</c> prints, a file name like <c>firefox.desktop</c>), from the first of
    /// <paramref name="dataDirs"/> whose <c>applications</c> folder has it; null when there is none.
    /// </summary>
    public static string? LinuxExec(IReadOnlyList<string> dataDirs, Func<string?> settings)
    {
        var id = settings()?.Trim();
        if (id == null || !DesktopId().IsMatch(id))
            return null;
        foreach (var dir in dataDirs)
        {
            var path = Path.Combine(dir, "applications", id);
            if (!File.Exists(path))
                continue;
            var group = "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    group = line;
                    continue;
                }

                if (group == "[Desktop Entry]" && line.StartsWith("Exec", StringComparison.Ordinal)
                    && line[4..].TrimStart() is { } after && after.StartsWith('='))
                {
                    var value = Unescape(after[1..].Trim());
                    return value.Length == 0 ? null : value;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>A desktop entry's string escapes: <c>\s</c>, <c>\n</c>, <c>\t</c>, <c>\r</c>, <c>\\</c>.</summary>
    private static string Unescape(string value)
    {
        var text = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                text.Append(value[i]);
                continue;
            }

            text.Append(value[++i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', var other => other });
        }

        return text.ToString();
    }

    /// <summary>
    /// The command a desktop entry's <c>Exec</c> line runs for <paramref name="file"/>: its words (split at spaces outside
    /// double quotes; in quotes <c>\"</c>, <c>\`</c>, <c>\$</c> and <c>\\</c> stand for the character), with <c>%f</c>,
    /// <c>%F</c>, <c>%u</c> and <c>%U</c> the file (once), <c>%%</c> a percent sign, and every other field code dropped; with no
    /// file code the file is added at the end. Null for a line with no program or an unclosed quote.
    /// </summary>
    public static ProcessStartInfo? LinuxCommand(string exec, string file)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        var placed = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (quoted)
            {
                if (c == '"')
                    quoted = false;
                else if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                    word.Append(exec[++i]);
                else
                    word.Append(c);
                continue;
            }

            switch (c)
            {
                case ' ' or '\t':
                    if (inWord)
                        words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                    break;
                case '"':
                    quoted = true;
                    inWord = true;
                    break;
                case '%' when i + 1 < exec.Length:
                    var code = exec[++i];
                    if (code is 'f' or 'F' or 'u' or 'U')
                    {
                        if (!placed)
                        {
                            word.Append(file);
                            inWord = true;
                        }

                        placed = true;
                    }
                    else if (code == '%')
                    {
                        word.Append('%');
                        inWord = true;
                    }

                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }

        if (quoted)
            return null;
        if (inWord)
            words.Add(word.ToString());
        if (words.Count == 0)
            return null;
        if (!placed)
            words.Add(file);

        var command = new ProcessStartInfo(words[0]) { UseShellExecute = false };
        foreach (var argument in words.Skip(1))
            command.ArgumentList.Add(argument);
        return command;
    }
}
