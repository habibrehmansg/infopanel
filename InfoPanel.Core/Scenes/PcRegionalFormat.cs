using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using InfoPanel.Sensors.MacOS;

namespace InfoPanel.Scenes;

/// <summary>
/// The PC's regional format: what the user chose in the operating system's regional settings, read by the engine once
/// at start-up. On macOS from System Settings through CoreFoundation (the region and its number format, whatever the
/// launch: .NET's culture there follows <c>LC_ALL</c>, <c>LC_MESSAGES</c> and <c>LANG</c> and never the number format);
/// elsewhere .NET's current culture (on Windows the user's locale with its Region overrides).
/// </summary>
public static class PcRegionalFormat
{
    private static readonly Lazy<RegionalFormat> Cached = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The format read once for this process (the runtime's default when its options give none).</summary>
    public static RegionalFormat Current => Cached.Value;

    /// <summary>Reads the PC's regional format now.</summary>
    public static RegionalFormat Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                if (MacLocale.Current() is { } mac) return mac;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // No CoreFoundation: .NET's culture below.
            }
        }
        return RegionalFormat.Of(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The culture for a CoreFoundation locale identifier: its <c>@</c> keywords dropped and <c>_</c> read as <c>-</c>
    /// (<c>en_US@rg=dezzzz</c> is en-US, <c>zh_SG</c> zh-SG), looked up with no user overrides; else its language alone;
    /// else the invariant culture.
    /// </summary>
    public static CultureInfo CultureOf(string identifier)
    {
        var at = identifier.IndexOf('@');
        var tag = (at < 0 ? identifier : identifier[..at]).Replace('_', '-');
        if (Predefined(tag) is { } culture) return culture;
        var dash = tag.IndexOf('-');
        if (dash > 0 && Predefined(tag[..dash]) is { } language) return language;
        return CultureInfo.InvariantCulture;
    }

    private static CultureInfo? Predefined(string tag)
    {
        if (tag.Length == 0) return null;
        try
        {
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A CoreFoundation locale's format: the culture for its identifier (<see cref="CultureOf"/>) with CoreFoundation's
    /// decimal and grouping separators, which honour a custom number format.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class MacLocale
    {
        private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Lib)] private static extern IntPtr CFLocaleCopyCurrent();
        [DllImport(Lib)] private static extern IntPtr CFLocaleCreate(IntPtr allocator, IntPtr identifier);
        [DllImport(Lib)] private static extern IntPtr CFLocaleGetIdentifier(IntPtr locale);
        [DllImport(Lib)] private static extern IntPtr CFLocaleGetValue(IntPtr locale, IntPtr key);

        private static readonly Lazy<(IntPtr Decimal, IntPtr Group)> Keys = new(() =>
        {
            var lib = NativeLibrary.Load(Lib);
            return (Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kCFLocaleDecimalSeparator")),
                Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kCFLocaleGroupingSeparator")));
        });

        /// <summary>The user's current locale (System Settings); null when CoreFoundation gives none.</summary>
        public static RegionalFormat? Current()
        {
            var locale = CFLocaleCopyCurrent();
            if (locale == IntPtr.Zero) return null;
            try
            {
                return Read(locale);
            }
            finally
            {
                CF.CFRelease(locale);
            }
        }

        /// <summary>A locale made from <paramref name="identifier"/> (tests: the mapping without changing System Settings).</summary>
        public static RegionalFormat? Create(string identifier)
        {
            var name = CF.Str(identifier);
            try
            {
                var locale = CFLocaleCreate(IntPtr.Zero, name);
                if (locale == IntPtr.Zero) return null;
                try
                {
                    return Read(locale);
                }
                finally
                {
                    CF.CFRelease(locale);
                }
            }
            finally
            {
                CF.CFRelease(name);
            }
        }

        /// <summary>The locale's identifier as CoreFoundation gives it.</summary>
        public static string? Identifier(IntPtr locale) => CF.ToManaged(CFLocaleGetIdentifier(locale));

        private static RegionalFormat? Read(IntPtr locale)
        {
            if (Identifier(locale) is not { } identifier) return null;
            var (decimalKey, groupKey) = Keys.Value;
            return RegionalFormat.Create(CultureOf(identifier),
                CF.ToManaged(CFLocaleGetValue(locale, decimalKey)), CF.ToManaged(CFLocaleGetValue(locale, groupKey)));
        }
    }
}
