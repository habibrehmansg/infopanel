using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Drawing;
using System.Linq;
using System.Windows;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class TextDisplayItem : DisplayItem
    {
        private string _font = System.Windows.Media.Fonts.SystemFontFamilies.First().ToString();
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

                try
                {
                    ColorTranslator.FromHtml(value);
                    SetProperty(ref _color, value);
                }
                catch
                { }
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
        private int _width = 0;

        [ObservableProperty]
        private int _height = 0;

        public TextDisplayItem()
        {
        }

        public TextDisplayItem(string name) : base(name) { }


        public override string EvaluateText()
        {
            return Name;
        }

        public override string EvaluateColor()
        {
            return Color;
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (Name, Color);
        }

        public override SizeF EvaluateSize()
        {
            using Bitmap bitmap = new(1, 1);
            using Graphics g = Graphics.FromImage(bitmap);
            using Font font = new(Font, FontSize);
            var text = EvaluateText();
            var sizeF = g.MeasureString(text, font, 0, StringFormat.GenericTypographic);

            if(Width != 0)
            {
                sizeF.Width = Width;
            }

            return sizeF;
        }

        public override Rect EvaluateBounds()
        {
            var size = EvaluateSize();
            if (RightAlign && Width == 0)
            {
                return new Rect(X - size.Width, Y, size.Width, size.Height);
            }
            else
            {
                return new Rect(X, Y, size.Width, size.Height);
            }
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
        private string _format = "hh:mm:ss tt";
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

        public ClockDisplayItem(string name) : base(name) { }


        public override string EvaluateText()
        {
            if (Uppercase)
            {
                return DateTime.Now.ToString(_format).ToUpper();
            }
            else
            {
                return DateTime.Now.ToString(_format);
            }
        }
        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), Color);
        }
    }

    [Serializable]
    public class CalendarDisplayItem : TextDisplayItem
    {
        private string _format = "dd/MM/yyyy";
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

        public CalendarDisplayItem(string name) : base(name) { }

        public override string EvaluateText()
        {
            if (Uppercase)
            {
                return DateTime.Today.ToString(_format).ToUpper();
            }
            else
            {
                return DateTime.Today.ToString(_format);
            }
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), Color);
        }
    }
}
