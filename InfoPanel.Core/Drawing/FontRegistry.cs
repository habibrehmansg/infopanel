using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Fonts the renderer resolves before the system's: the bundled Inter faces and any font files the
    /// host registers (for example from <c>{data}/fonts</c>). Process-wide and thread-safe.
    /// The bundled faces are registered on first use, before anything else, so a font the host
    /// registers later with the same family and style replaces them.
    /// </summary>
    public static class FontRegistry
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(FontRegistry));

        private static readonly string[] EmbeddedFaces = ["Inter-Regular.ttf", "Inter-Bold.ttf", "Inter-Italic.ttf", "Inter-BoldItalic.ttf"];
        private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];

        private static readonly Lock _lock = new();
        private static readonly Dictionary<string, List<SKTypeface>> _families = new(StringComparer.OrdinalIgnoreCase);
        private static bool _embeddedRegistered;

        /// <summary>
        /// Registers the bundled Inter faces (Regular, Bold, Italic, Bold Italic). Idempotent; the
        /// registry also does this on first use, so calling it only moves the cost to start-up.
        /// </summary>
        public static void RegisterEmbedded()
        {
            lock (_lock)
            {
                EnsureEmbedded();
            }
        }

        /// <summary>
        /// Registers every face in a font file (.ttf, .otf, or each face of a .ttc/.otc collection).
        /// </summary>
        /// <returns>The family names registered; empty if the file is not a font Skia can read.</returns>
        public static IReadOnlyList<string> RegisterFile(string path)
        {
            var faces = new List<SKTypeface>();
            var extension = Path.GetExtension(path);
            var isCollection = extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".otc", StringComparison.OrdinalIgnoreCase);

            try
            {
                using var data = SKData.Create(path);
                if (data != null)
                {
                    for (var index = 0; index < (isCollection ? 64 : 1); index++)
                    {
                        var typeface = SKTypeface.FromData(data, index);
                        if (typeface == null)
                        {
                            break;
                        }

                        faces.Add(typeface);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Font file {Path} could not be read", path);
            }

            if (faces.Count == 0)
            {
                Logger.Warning("Font file {Path} has no readable faces", path);
                return [];
            }

            lock (_lock)
            {
                EnsureEmbedded();
                return [.. faces.Select(Add).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        }

        /// <summary>
        /// Registers every font file (.ttf, .otf, .ttc, .otc) under <paramref name="directory"/>, subfolders included.
        /// A missing directory registers nothing.
        /// </summary>
        /// <returns>The family names registered.</returns>
        public static IReadOnlyList<string> RegisterDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => FontExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal);

            return [.. files.SelectMany(RegisterFile).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>
        /// The registered face of <paramref name="family"/> (case-insensitive) closest to the requested style:
        /// the same slant if there is one (italic and oblique count as the same), then the nearest weight,
        /// the heavier one on a tie.
        /// </summary>
        public static bool TryGet(string? family, int weight, SKFontStyleSlant slant, [NotNullWhen(true)] out SKTypeface? typeface)
        {
            typeface = null;

            if (string.IsNullOrEmpty(family))
            {
                return false;
            }

            lock (_lock)
            {
                EnsureEmbedded();

                if (!_families.TryGetValue(family, out var faces))
                {
                    return false;
                }

                var sloped = slant != SKFontStyleSlant.Upright;

                typeface = faces
                    .OrderBy(face => (face.FontSlant != SKFontStyleSlant.Upright) == sloped ? 0 : 1)
                    .ThenBy(face => Math.Abs(face.FontWidth - (int)SKFontStyleWidth.Normal))
                    .ThenBy(face => Math.Abs(face.FontWeight - weight))
                    .ThenByDescending(face => face.FontWeight)
                    .First();

                return true;
            }
        }

        /// <summary>The registered family names, sorted (ordinal, case-insensitive).</summary>
        public static IReadOnlyList<string> GetFamilies()
        {
            lock (_lock)
            {
                EnsureEmbedded();
                return [.. _families.Values.Select(faces => faces[0].FamilyName).Order(StringComparer.OrdinalIgnoreCase)];
            }
        }

        /// <summary>
        /// Converts a 1.x font style name ("Bold", "SemiBold Italic", "Light", …) to a weight and slant.
        /// Unknown words are ignored; no weight word means 400.
        /// </summary>
        internal static (int Weight, SKFontStyleSlant Slant) ParseStyleName(string styleName)
        {
            var name = styleName.Replace(" ", "").Replace("-", "").ToLowerInvariant();

            var slant = name.Contains("italic") ? SKFontStyleSlant.Italic
                : name.Contains("oblique") ? SKFontStyleSlant.Oblique
                : SKFontStyleSlant.Upright;

            // Longest names first, so "semibold" isn't read as "bold" and "extralight" as "light".
            (string Word, int Weight)[] weights =
            [
                ("extralight", 200), ("ultralight", 200), ("extrabold", 800), ("ultrabold", 800),
                ("semibold", 600), ("demibold", 600), ("hairline", 100), ("regular", 400), ("medium", 500),
                ("normal", 400), ("light", 300), ("heavy", 900), ("black", 900), ("thin", 100), ("bold", 700), ("book", 400),
            ];

            foreach (var (word, weight) in weights)
            {
                if (name.Contains(word))
                {
                    return (weight, slant);
                }
            }

            return (400, slant);
        }

        private static string Add(SKTypeface typeface)
        {
            var family = typeface.FamilyName;

            if (!_families.TryGetValue(family, out var faces))
            {
                faces = [];
                _families[family] = faces;
            }

            // A later face with the same style replaces the earlier one.
            faces.RemoveAll(face => face.FontWeight == typeface.FontWeight
                && face.FontWidth == typeface.FontWidth
                && face.FontSlant == typeface.FontSlant);
            faces.Add(typeface);

            return family;
        }

        private static void EnsureEmbedded()
        {
            if (_embeddedRegistered)
            {
                return;
            }

            _embeddedRegistered = true;

            var assembly = typeof(FontRegistry).Assembly;

            foreach (var file in EmbeddedFaces)
            {
                using var stream = assembly.GetManifestResourceStream($"InfoPanel.Fonts.{file}");
                if (stream == null)
                {
                    Logger.Error("Embedded font {File} is missing", file);
                    continue;
                }

                using var data = SKData.Create(stream);
                var typeface = SKTypeface.FromData(data);
                if (typeface == null)
                {
                    Logger.Error("Embedded font {File} could not be read", file);
                    continue;
                }

                Add(typeface);
            }
        }
    }
}
