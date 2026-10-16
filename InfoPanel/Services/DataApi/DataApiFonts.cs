using InfoPanel.Drawing;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using Topten.RichTextKit;

namespace InfoPanel.DataApi
{
    /// <summary>A font as a profile item names it (Font, FontStyle, Bold, Italic).</summary>
    public sealed record FontRequest(string Font, string? Style, bool Bold, bool Italic);

    /// <summary>
    /// The face InfoPanel draws a font request with, on this machine. Font
    /// names resolve through DirectWrite (localized style names, legacy family
    /// names, variable fonts), so a client cannot work this out from font
    /// files alone.
    /// </summary>
    public sealed record ResolvedFont
    {
        public required FontRequest Request { get; init; }
        public required string Family { get; init; }
        public int Weight { get; init; }
        public int Width { get; init; }
        public bool Italic { get; init; }

        /// <summary>True when the drawn family is not the requested one (a fallback).</summary>
        public bool Fallback { get; init; }

        /// <summary>The face's id for /api/fonts/{id}; null when Skia exposes no file for it.</summary>
        public string? Face { get; init; }

        /// <summary>The face's index in a font collection (.ttc); 0 otherwise.</summary>
        public int TtcIndex { get; init; }
    }

    /// <summary>
    /// Font resolution for the Data API: the same steps as SkiaGraphics.DrawString
    /// (CreateTypeface, then RichTextKit's font mapper), and the font file
    /// behind the result.
    /// </summary>
    public static class DataApiFonts
    {
        public const int MaxRequests = 256;
        private const int MaxNameLength = 256;

        private sealed record FaceStyle(string Family, int Weight, int Width, bool Italic);

        // Only faces a resolve returned can be fetched.
        private static readonly ConcurrentDictionary<string, FaceStyle> Faces = new();

        public static bool IsValid(FontRequest request) =>
            request.Font is { Length: <= MaxNameLength } && (request.Style?.Length ?? 0) <= MaxNameLength;

        public static ResolvedFont Resolve(FontRequest request)
        {
            var typeface = SkiaGraphics.CreateTypeface(request.Font ?? "", request.Style ?? "", request.Bold, request.Italic);
            var drawn = Drawn(new FaceStyle(typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.IsItalic));

            string? face = null;
            int ttcIndex = 0;
            if (Read(drawn, out var bytes, out ttcIndex))
            {
                face = FaceId(bytes);
                Faces.TryAdd(face, new FaceStyle(drawn.FamilyName, drawn.FontWeight, drawn.FontWidth, drawn.IsItalic));
            }

            return new ResolvedFont
            {
                Request = request,
                Family = drawn.FamilyName,
                Weight = drawn.FontWeight,
                Width = drawn.FontWidth,
                Italic = drawn.IsItalic,
                Fallback = !string.Equals(drawn.FamilyName, request.Font, StringComparison.OrdinalIgnoreCase),
                Face = face,
                TtcIndex = ttcIndex,
            };
        }

        /// <summary>The font file of a face a resolve returned, with its media type.</summary>
        public static bool TryGetFace(string id, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = "";
            if (!Faces.TryGetValue(id, out var style)) return false;
            // Fonts can be installed or removed while InfoPanel runs: serve the
            // file only while it is still the one the id names.
            if (!Read(Drawn(style), out bytes, out _) || FaceId(bytes) != id) return false;

            contentType = Magic(bytes) switch
            {
                "ttcf" => "font/collection",
                "OTTO" => "font/otf",
                _ => "font/ttf",
            };
            return true;
        }

        /// <summary>SkiaGraphics.DrawString hands RichTextKit these four values, and it resolves them again.</summary>
        private static SKTypeface Drawn(FaceStyle style) =>
            FontMapper.Default.TypefaceFromStyle(new Style
            {
                FontFamily = style.Family,
                FontWeight = style.Weight,
                FontItalic = style.Italic,
                FontWidth = (SKFontStyleWidth)style.Width,
            }, false);

        private static bool Read(SKTypeface typeface, out byte[] bytes, out int ttcIndex)
        {
            bytes = [];
            using var stream = typeface.OpenStream(out ttcIndex);
            if (stream == null) return false;
            using var data = SKData.Create(stream);
            if (data == null || data.Size == 0) return false;
            bytes = data.ToArray();
            return true;
        }

        private static string FaceId(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes))[..16].ToLowerInvariant();

        private static string Magic(byte[] bytes) => bytes.Length >= 4 ? System.Text.Encoding.ASCII.GetString(bytes, 0, 4) : "";
    }
}
