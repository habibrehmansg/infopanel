using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InfoPanel.Tests.Plugins;

/// <summary>Pictures for the plugin image tests: PNGs the engine takes as media, of any size, made in memory.</summary>
internal static class PluginPictures
{
    /// <summary>A 1 × 1 PNG: the test plugin's picture under its <c>image</c> mode.</summary>
    public static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>
    /// A PNG of exactly <paramref name="bytes"/> bytes (at least 57): <see cref="Pixel"/>'s signature and header, one IDAT
    /// chunk of <paramref name="seed"/> bytes, and the end chunk. The engine inspects a picture's structure, never decodes
    /// it, so it takes this as a 1 × 1 PNG; two of one size and seed are the same bytes.
    /// </summary>
    public static byte[] Png(int bytes, byte seed)
    {
        var png = new byte[bytes];
        Pixel.AsSpan(0, 33).CopyTo(png);
        var data = bytes - 57;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33), (uint)data);
        "IDAT"u8.CopyTo(png.AsSpan(37));
        png.AsSpan(41, data).Fill(seed);
        var end = bytes - 12;
        "IEND"u8.CopyTo(png.AsSpan(end + 4));
        Pixel.AsSpan(Pixel.Length - 4).CopyTo(png.AsSpan(end + 8));
        return png;
    }

    /// <summary>The reference an image sensor of the plugins source names a picture by: 16 hex digits of its SHA-256.</summary>
    public static string ReferenceOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
}
