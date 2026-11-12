using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InfoPanel.Panels.Usb;

namespace InfoPanel.Panels;

/// <summary>
/// A USB id pattern a family's <c>detect</c> declares (<c>panel.json</c>): its vendor exactly, its product as four
/// lower-case hexadecimal digits of which any may be <c>?</c> (one digit, any), or <c>*</c> (any product). Parsed into the
/// product's bits that must equal <see cref="Product"/> (<see cref="Mask"/>).
/// </summary>
public sealed record UsbIdPattern(ushort VendorId, ushort Product, ushort Mask)
{
    /// <summary>Whether it names the device <paramref name="vendorId"/>:<paramref name="productId"/>.</summary>
    public bool Names(ushort vendorId, ushort productId) => vendorId == VendorId && (productId & Mask) == (Product & Mask);

    /// <summary>
    /// The pattern <paramref name="text"/> (<c>vvvv:pppp</c>: the vendor in lower-case hexadecimal; the product in
    /// lower-case hexadecimal with <c>?</c> for any digit, or <c>*</c> alone), or null when it isn't one.
    /// </summary>
    public static UsbIdPattern? Parse(string text)
    {
        if (text.Length is not (6 or 9) || text[4] != ':' || !IsHex(text.AsSpan(0, 4)))
            return null;
        var vendor = ushort.Parse(text.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (text.Length == 6)
            return text[5] == '*' ? new UsbIdPattern(vendor, 0, 0) : null;
        ushort product = 0, mask = 0;
        for (var i = 5; i < 9; i++)
        {
            product <<= 4;
            mask <<= 4;
            var c = text[i];
            if (c == '?')
                continue;
            if (!IsHex(c))
                return null;
            product |= (ushort)(c <= '9' ? c - '0' : c - 'a' + 10);
            mask |= 0xF;
        }
        return new UsbIdPattern(vendor, product, mask);
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!IsHex(c))
                return false;
        return true;
    }

    /// <summary>The pattern as <c>panel.json</c> writes it.</summary>
    public override string ToString()
    {
        if (Mask == 0)
            return $"{VendorId:x4}:*";
        Span<char> product = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var shift = 12 - 4 * i;
            product[i] = ((Mask >> shift) & 0xF) == 0 ? '?' : "0123456789abcdef"[(Product >> shift) & 0xF];
        }
        return $"{VendorId:x4}:{product}";
    }
}

/// <summary>
/// A driver whose family names, beyond its matches, the USB ids its vendor's panels may have (<c>panel.json</c>'s
/// <c>detect</c>; BeadaPanel's built in): a device one of them names that no driver's matches do is listed as detected,
/// never opened (<see cref="PanelDetection"/>).
/// </summary>
public interface IDetectsPanels
{
    IReadOnlyList<UsbIdPattern> Detect { get; }
}

/// <summary>
/// A USB device that looks like a panel of <see cref="Family"/> (one of its <c>detect</c> patterns names it, no driver's
/// matches do), as the OS's cached descriptors give it: its release (<see cref="BcdDevice"/>) and its interfaces.
/// </summary>
public sealed record DetectedDevice(string Family, UsbDeviceId Usb, ushort BcdDevice, IReadOnlyList<UsbInterfaceDescriptor> Interfaces);

/// <summary>
/// "Detected, not supported" (docs/2.0/panels.md): which listed USB devices are answered as detected. Read from the OS's
/// cached descriptors only (<see cref="IUsbBackend.Describe"/>): such a device is never opened, written or claimed.
/// </summary>
public static class PanelDetection
{
    /// <summary>The most patterns a family's <c>detect</c> has.</summary>
    public const int MaxPatterns = 64;

    /// <summary>The most detected devices answered.</summary>
    public const int MaxDetected = 64;

    /// <summary>The most interfaces answered for a detected device.</summary>
    public const int MaxInterfaces = 32;

    /// <summary>The most endpoints answered for an interface (USB's 15 in and 15 out beyond endpoint 0).</summary>
    public const int MaxEndpoints = 30;

    /// <summary>
    /// Vendors never answered as detected, whatever a family declares: <c>1a86</c> (QinHeng, the CH340 serial cables) and
    /// <c>1d6b</c> and <c>0525</c> (Linux's own ids and its USB gadget's: root hubs and gadgets would show).
    /// </summary>
    public static readonly IReadOnlySet<ushort> LeftOutVendors = new HashSet<ushort> { 0x1a86, 0x1d6b, 0x0525 };

    private const byte HubClass = 0x09;
    private const byte HidClass = 0x03;
    private const byte BootSubClass = 0x01;

    /// <summary>
    /// Whether a device so described is one never answered as detected: a hub (its device class or an interface's), or one
    /// with a boot keyboard or boot mouse interface; every alternate setting of every interface counts, not only the first
    /// (<see cref="UsbInterfaceDescriptor.LaterSettings"/>).
    /// </summary>
    public static bool IsHubOrInput(UsbDescriptor descriptor) =>
        descriptor.DeviceClass == HubClass
        || descriptor.Interfaces.Any(i => IsHubOrInput(i.Class, i.SubClass, i.Protocol)
            || i.LaterSettings.Any(s => IsHubOrInput(s.Class, s.SubClass, s.Protocol)));

    private static bool IsHubOrInput(byte @class, byte subClass, byte protocol) =>
        @class == HubClass || (@class == HidClass && subClass == BootSubClass && protocol is 1 or 2);

    /// <summary>
    /// The family of the first driver, in their order, whose <c>detect</c> names <paramref name="usb"/>, when none of
    /// <paramref name="drivers"/>' matches names it and its vendor isn't left out; else null.
    /// </summary>
    public static string? FamilyOf(UsbDeviceId usb, IReadOnlyList<Sdk.IPanelDriver> drivers)
    {
        if (LeftOutVendors.Contains(usb.VendorId)
            || drivers.Any(d => d.Matches.Any(m => m.VendorId == usb.VendorId && m.ProductId == usb.ProductId)))
            return null;
        return drivers.FirstOrDefault(d => d is IDetectsPanels detects && detects.Detect.Any(p => p.Names(usb.VendorId, usb.ProductId)))?.Family;
    }

    /// <summary>
    /// The device as detected, its interfaces (each its first alternate setting) and their endpoints within
    /// <see cref="MaxInterfaces"/> and <see cref="MaxEndpoints"/>; null when it is a hub or a boot keyboard or mouse
    /// (<see cref="IsHubOrInput"/>, over all its interfaces and every alternate setting of each).
    /// </summary>
    public static DetectedDevice? Of(string family, UsbDeviceId usb, UsbDescriptor descriptor) =>
        IsHubOrInput(descriptor) ? null : new DetectedDevice(family, usb, descriptor.BcdDevice,
            [.. descriptor.Interfaces.Take(MaxInterfaces).Select(i => i.Endpoints.Count <= MaxEndpoints ? i : i with { Endpoints = [.. i.Endpoints.Take(MaxEndpoints)] })]);
}
