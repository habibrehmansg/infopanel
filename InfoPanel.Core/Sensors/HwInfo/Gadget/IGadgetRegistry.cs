using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>The registry hive a key is read from.</summary>
public enum GadgetHive : byte
{
    /// <summary><c>HKEY_CURRENT_USER</c>: the user's own hive, read first.</summary>
    CurrentUser,

    /// <summary><c>HKEY_LOCAL_MACHINE</c>, read when the user's hive has no key.</summary>
    LocalMachine,
}

/// <summary>A registry value's type, as far as the reader cares: text (<c>REG_SZ</c>) or anything else.</summary>
public enum GadgetValueKind : byte
{
    /// <summary><c>REG_SZ</c>: <see cref="GadgetValue.Text"/> holds it.</summary>
    String,

    /// <summary>Any other type (<c>REG_EXPAND_SZ</c>, <c>REG_DWORD</c> …): not read.</summary>
    Other,

    /// <summary>
    /// <c>REG_SZ</c>, but larger than <see cref="GadgetRegistryReader.MaxTextBytes"/>: its data isn't read, and its reading
    /// is skipped.
    /// </summary>
    TooLong,
}

/// <summary>One value of the key: its name, type and, for text, the text, whole.</summary>
public sealed record GadgetValue(string Name, GadgetValueKind Kind, string? Text);

/// <summary>
/// The key as read once: the hive it came from, its last-write time (UTC), the number of values it holds and HWiNFO's
/// values among them (none when <see cref="ValueCount"/> passes <see cref="HwInfoGadgetSampler.MaxValues"/>: they aren't read).
/// </summary>
public sealed record GadgetSnapshot(GadgetHive Hive, DateTime LastWriteUtc, int ValueCount, IReadOnlyList<GadgetValue> Values);

/// <summary>
/// Reads HWiNFO's gadget key (<c>Software\HWiNFO64\VSB</c>): <see cref="GadgetRegistryReader"/> over the real registry
/// on Windows (<see cref="WinGadgetRegistry"/>), a test's elsewhere. Read only: nothing is ever written.
/// </summary>
public interface IGadgetRegistry
{
    /// <summary>The key in <paramref name="hive"/>, opened afresh and read once, or null when it doesn't exist.</summary>
    GadgetSnapshot? Read(GadgetHive hive);
}

/// <summary>What <see cref="IGadgetOpenKey.EnumName"/> found at an index.</summary>
public enum GadgetEnumResult : byte
{
    /// <summary>A value whose name fits: it is returned.</summary>
    Found,

    /// <summary>A value whose name is longer than asked for: not one of HWiNFO's, and not returned.</summary>
    TooLong,

    /// <summary>No value at this index or after it.</summary>
    NoMore,
}

/// <summary>
/// The raw registry calls under <see cref="GadgetRegistryReader"/>: Windows' (<see cref="WinGadgetRegistry"/>), a test's
/// elsewhere. Read only.
/// </summary>
public interface IGadgetKeyApi
{
    /// <summary>The key in <paramref name="hive"/>, opened read-only, or null when it doesn't exist.</summary>
    IGadgetOpenKey? Open(GadgetHive hive);
}

/// <summary>One opened key (<c>RegQueryInfoKeyW</c>, <c>RegEnumValueW</c>, <c>RegQueryValueExW</c> on Windows).</summary>
public interface IGadgetOpenKey : IDisposable
{
    /// <summary>The number of values the key holds, and its last-write time (UTC).</summary>
    (int ValueCount, DateTime LastWriteUtc) QueryInfo();

    /// <summary>The name of the value at <paramref name="index"/> when it is at most <paramref name="maxLength"/> characters.</summary>
    GadgetEnumResult EnumName(int index, int maxLength, out string? name);

    /// <summary>The value's type and size in bytes, its data unread, or null when there is no such value.</summary>
    (GadgetValueKind Kind, int Bytes)? QueryValue(string name);

    /// <summary>
    /// The text of the <c>REG_SZ</c> value, read into a buffer of <paramref name="bytes"/> bytes; null when it is gone,
    /// no longer text, or no longer fits.
    /// </summary>
    string? ReadText(string name, int bytes);
}
