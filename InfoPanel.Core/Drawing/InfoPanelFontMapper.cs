using SkiaSharp;
using Topten.RichTextKit;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// RichTextKit font mapper that resolves <see cref="FontRegistry"/> families (bundled Inter, user fonts)
    /// before the system's. Set on every <see cref="TextBlock"/> the renderer creates, so the process-wide
    /// <see cref="FontMapper.Default"/> is left alone.
    /// </summary>
    internal sealed class InfoPanelFontMapper : FontMapper
    {
        public static readonly InfoPanelFontMapper Instance = new();

        private InfoPanelFontMapper()
        {
        }

        public override SKTypeface TypefaceFromStyle(IStyle style, bool ignoreFontVariants)
        {
            if (FontRegistry.TryGet(style.FontFamily, style.FontWeight, style.FontItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, out var typeface))
            {
                return typeface;
            }

            return base.TypefaceFromStyle(style, ignoreFontVariants);
        }
    }
}
