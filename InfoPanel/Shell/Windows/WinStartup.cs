using System.Runtime.Versioning;
using Microsoft.Win32;

namespace InfoPanel.Shell.Windows;

/// <summary>The current user's registry values the shell reads and writes (a seam, so the rules are tested on any OS).</summary>
internal interface IWinRegistry
{
    /// <summary>The value <paramref name="name"/> under <c>HKEY_CURRENT_USER\</c><paramref name="key"/>, or null.</summary>
    object? Get(string key, string name);

    /// <summary>Writes a string value, creating the key when it is missing.</summary>
    void SetString(string key, string name, string value);

    /// <summary>Removes a value; nothing when it isn't there.</summary>
    void Delete(string key, string name);
}

/// <summary>The current user's registry (<c>HKEY_CURRENT_USER</c>).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinRegistry : IWinRegistry
{
    public object? Get(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened?.GetValue(name);
    }

    public void SetString(string key, string name, string value)
    {
        using var opened = Registry.CurrentUser.CreateSubKey(key);
        opened.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Start when I sign in: the value <c>InfoPanel 2</c> under the user's Run key, which Windows starts at sign-in and Task
/// Manager lists under Startup apps. Not 1.x's <c>InfoPanel</c>: 1.4.x, installed beside 2.0, deletes that value at
/// every start. Read each time the menu opens, as Task Manager reads it: the value is there and its
/// StartupApproved entry (Task Manager's switch) doesn't turn it off. So what Task Manager shows is what the menu shows.
/// Turning it on writes the engine's own path and clears that switch; turning it off removes both.
/// </summary>
internal sealed class WinStartup(IWinRegistry registry, string command)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Task Manager's switch for each Run value: a binary value whose first byte is odd when it is turned off.</summary>
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string ValueName = "InfoPanel 2";

    /// <summary>Whether Windows starts InfoPanel when the user signs in.</summary>
    public bool IsOn()
    {
        if (registry.Get(RunKey, ValueName) is not string { Length: > 0 })
            return false;
        return registry.Get(ApprovedKey, ValueName) is not byte[] { Length: > 0 } approved || (approved[0] & 1) == 0;
    }

    /// <summary>Turns it on (the engine's path as the Run value) or off.</summary>
    public void Set(bool on)
    {
        registry.Delete(ApprovedKey, ValueName);
        if (on)
            registry.SetString(RunKey, ValueName, command);
        else
            registry.Delete(RunKey, ValueName);
    }

    /// <summary>
    /// The command the Run value holds: the engine's own program, quoted; when the engine runs under the <c>dotnet</c>
    /// host, the host with the engine's assembly.
    /// </summary>
    public static string CommandFor(string processPath, string? entryAssembly)
    {
        var host = Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/').Split('/')[^1]);
        return string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssembly)
            ? $"\"{processPath}\" \"{entryAssembly}\""
            : $"\"{processPath}\"";
    }
}
