using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using SkiaSharp;
using System;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class TextDisplayItem : DisplayItem
    {
        private string _font = FontDefaults.Family;
        public string Font
        {
            get { return _font; }
            set
            {
                if (value != null)
                {
                    SetProperty(ref _font, value);
                }
            }
        }

        [ObservableProperty]
        private string _fontStyle = string.Empty;

        private int _fontSize = 20;
        public int FontSize
        {
            get { return _fontSize; }
            set
            {
                if (value <= 0)
                {
                    return;
                }

                SetProperty(ref _fontSize, value);
            }
        }

        private bool _bold = false;

        public bool Bold
        {
            get { return _bold; }
            set
            {
                SetProperty(ref _bold, value);
            }
        }

        private bool _italic = false;

        public bool Italic
        {
            get { return _italic; }
            set
            {
                SetProperty(ref _italic, value);
            }
        }

        private bool _underline = false;

        public bool Underline
        {
            get { return _underline; }
            set
            {
                SetProperty(ref _underline, value);
            }
        }

        private bool _strikeout = false;

        public bool Strikeout
        {
            get { return _strikeout; }
            set
            {
                SetProperty(ref _strikeout, value);
            }
        }

        private string _color = "#000000";
        public string Color
        {
            get { return _color; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith("#"))
                {
                    value = "#" + value;
                }

                SetProperty(ref _color, value);
            }
        }

        [ObservableProperty]
        private bool _rightAlign = false;

        [ObservableProperty]
        private bool _centerAlign = false;

        private bool _uppercase = false;

        public bool Uppercase
        {
            get { return _uppercase; }
            set
            {
                SetProperty(ref _uppercase, value);
            }
        }

        [ObservableProperty]
        private bool _wrap = true;

        [ObservableProperty]
        private bool _ellipsis = true;

        [ObservableProperty]
        private int _width = 0;

        [ObservableProperty]
        private int _height = 0;

        [ObservableProperty]
        private bool _marquee = false;

        [ObservableProperty]
        private int _marqueeSpeed = 50;

        [ObservableProperty]
        private int _marqueeSpacing = 40;

        [ObservableProperty]
        private bool _glowEnabled = false;

        private int _glowRadius = 8;
        public int GlowRadius
        {
            get => _glowRadius;
            set => SetProperty(ref _glowRadius, Math.Clamp(value, 2, 20));
        }

        [ObservableProperty]
        private string _glowColor = "#000000";

        /// <summary>Skia blend mode when compositing the glow layer (e.g. SrcOver, Screen, Plus). Default SrcOver = normal.</summary>
        [ObservableProperty]
        private string _glowBlendMode = "SrcOver";

        public TextDisplayItem()
        {
        }

        public TextDisplayItem(string name, Profile profile) : base(name, profile) { }


        public override string EvaluateText(RenderContext ctx)
        {
            return Name;
        }

        public override string EvaluateColor(RenderContext ctx)
        {
            return Color;
        }

        public override (string, string) EvaluateTextAndColor(RenderContext ctx)
        {
            return (Name, Color);
        }

        public override SKSize EvaluateSize(RenderContext ctx)
        {
            var skiaGraphics = SkiaGraphics.FromEmpty(Profile.FontScale);
            var text = EvaluateText(ctx);
            var (width, height) = skiaGraphics.MeasureString(text, Font, FontStyle, FontSize, Bold, Italic, Underline, Strikeout, Wrap, Ellipsis, Width);
            return new SKSize(width, height);
        }

        public override SKRect EvaluateBounds(RenderContext ctx)
        {
            var size = EvaluateSize(ctx);
            int rectX = Width == 0 && RightAlign ? (int)(X - size.Width) : X;
            var rect = new SKRect(rectX, Y, rectX + size.Width, Y + size.Height);
            if (GlowEnabled && GlowRadius > 0)
            {
                rect.Inflate(GlowRadius, GlowRadius);
            }
            return rect;
        }
        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
    }

    [Serializable]
    public class ClockDisplayItem : TextDisplayItem
    {
        private string _format = DefaultFormat;
        public string Format
        {
            get { return _format; }
            set
            {
                try
                {
                    DateTime.Now.ToString(value);
                    SetProperty(ref _format, value);
                }
                catch { }
            }
        }

        public ClockDisplayItem()
        {
        }

        public ClockDisplayItem(string name, Profile profile) : base(name, profile) { }


        public override string EvaluateText(RenderContext ctx)
        {
            var text = FormatDateTime(ToLocalDateTime(ctx.Now), _format, DefaultFormat);
            return Uppercase ? text.ToUpper() : text;
        }

        internal const string DefaultFormat = "hh:mm:ss tt";

        /// <summary>
        /// <paramref name="now"/> as the <see cref="DateTime"/> 1.x formatted (<c>DateTime.Now</c>):
        /// local kind when its offset is the machine's, so formats like "U" and "K" behave the same.
        /// </summary>
        internal static DateTime ToLocalDateTime(DateTimeOffset now)
        {
            return now.Offset == TimeZoneInfo.Local.GetUtcOffset(now.UtcDateTime)
                ? DateTime.SpecifyKind(now.DateTime, DateTimeKind.Local)
                : now.DateTime;
        }

        /// <summary>
        /// Formats with <paramref name="format"/>, or with <paramref name="fallback"/> if that throws.
        /// 1.x never rendered an invalid format: its setter kept the previous one.
        /// </summary>
        internal static string FormatDateTime(DateTime value, string format, string fallback)
        {
            try
            {
                return value.ToString(format);
            }
            catch (FormatException)
            {
                return value.ToString(fallback);
            }
        }
        public override (string, string) EvaluateTextAndColor(RenderContext ctx)
        {
            return (EvaluateText(ctx), Color);
        }
    }

    [Serializable]
    public class CalendarDisplayItem : TextDisplayItem
    {
        private string _format = DefaultFormat;
        public string Format
        {
            get { return _format; }
            set
            {
                DateTime.Today.ToString(value);
                SetProperty(ref _format, value);
            }
        }

        public CalendarDisplayItem()
        {
        }

        public CalendarDisplayItem(string name, Profile profile) : base(name, profile) { }

        public override string EvaluateText(RenderContext ctx)
        {
            // Midnight of ctx.Now's day, like 1.x DateTime.Today.
            var today = ClockDisplayItem.ToLocalDateTime(ctx.Now).Date;
            var text = ClockDisplayItem.FormatDateTime(today, _format, DefaultFormat);
            return Uppercase ? text.ToUpper() : text;
        }

        internal const string DefaultFormat = "dd/MM/yyyy";

        public override (string, string) EvaluateTextAndColor(RenderContext ctx)
        {
            return (EvaluateText(ctx), Color);
        }
    }
}
