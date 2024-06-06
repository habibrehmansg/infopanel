using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Linq;
using System.Windows;

namespace InfoPanel.Models
{
    [Serializable]
    public class TextDisplayItem : DisplayItem
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
        public bool RightAlign { get; set; } = false;

        private bool _uppercase = false;

        public bool Uppercase
        {
            get { return _uppercase; }
            set
            {
                SetProperty(ref _uppercase, value);
            }
        }

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
            using (Bitmap bitmap = new Bitmap(1, 1))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    using (Font font = new Font(Font, FontSize))
                    {
                        var text = EvaluateText();
                        return g.MeasureString(text, font);
                    }
                }
            }
        }

        public override Rect EvaluateBounds()
        {
            var size = EvaluateSize();
            if (RightAlign)
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

    [Serializable]
    public class SensorDisplayItem : TextDisplayItem
    {
        public enum SensorValueType
        {
            NOW, MIN, MAX, AVERAGE
        }

        private string _sensorName = String.Empty;
        public string SensorName
        {
            get { return _sensorName; }
            set
            {
                SetProperty(ref _sensorName, value);
            }
        }

        private UInt32 _id;
        public UInt32 Id
        {
            get { return _id; }
            set
            {
                SetProperty(ref _id, value);
            }
        }

        private UInt32 _instance;
        public UInt32 Instance
        {
            get { return _instance; }
            set
            {
                SetProperty(ref _instance, value);
            }
        }

        private UInt32 _entryId;
        public UInt32 EntryId
        {
            get { return _entryId; }
            set
            {
                SetProperty(ref _entryId, value);
            }
        }

        public SensorValueType _valueType = SensorValueType.NOW;
        public SensorValueType ValueType
        {
            get { return _valueType; }
            set
            {
                SetProperty(ref _valueType, value);
            }
        }

        private int _threshold1 = 0;
        public int Threshold1
        {
            get { return _threshold1; }
            set
            {
                SetProperty(ref _threshold1, value);
            }
        }

        private string _threshold1Color = "#000000";
        public string Threshold1Color
        {
            get { return _threshold1Color; }
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
                    SetProperty(ref _threshold1Color, value);
                }
                catch
                { }
            }
        }

        private int _threshold2 = 0;
        public int Threshold2
        {
            get { return _threshold2; }
            set
            {
                SetProperty(ref _threshold2, value);
            }
        }

        private string _threshold2Color = "#000000";
        public string Threshold2Color
        {
            get { return _threshold2Color; }
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
                    SetProperty(ref _threshold2Color, value);
                }
                catch
                { }
            }
        }

        private bool _showName = false;
        public bool ShowName
        {
            get { return _showName; }
            set
            {
                SetProperty(ref _showName, value);
            }
        }

        private string _unit = String.Empty;
        public string Unit
        {
            get { return _unit; }
            set
            {
                SetProperty(ref _unit, value);
            }
        }

        private bool _overrideUnit = false;
        public bool OverrideUnit
        {
            get { return _overrideUnit; }
            set
            {
                SetProperty(ref _overrideUnit, value);
            }
        }

        private bool _showUnit = true;
        public bool ShowUnit
        {
            get { return _showUnit; }
            set
            {
                SetProperty(ref _showUnit, value);
            }
        }

        private bool _overridePrecision = false;
        public bool OverridePrecision
        {
            get { return _overridePrecision; }
            set
            {
                SetProperty(ref _overridePrecision, value);
            }
        }

        private int _precision = 0;
        public int Precision
        {
            get { return _precision; }
            set
            {
                SetProperty(ref _precision, value);
            }
        }

        private double _additionModifier = 0;
        public double AdditionModifier
        {
            get { return _additionModifier; }
            set
            {
                SetProperty(ref _additionModifier, value);
            }
        }

        private bool _absoluteAddition = true;
        public bool AbsoluteAddition
        {
            get { return _absoluteAddition; }
            set
            {
                SetProperty(ref _absoluteAddition, value);
            }
        }

        private double _multiplicationModifier = 1.00;
        public double MultiplicationModifier
        {
            get { return _multiplicationModifier; }
            set
            {
                SetProperty(ref _multiplicationModifier, value);
            }
        }

        public SensorDisplayItem()
        {
            SensorName = String.Empty;
            RightAlign = true;
        }

        public SensorDisplayItem(string name, UInt32 id, UInt32 instance, UInt32 entryId) : base(name)
        {
            SensorName = name;
            Id = id;
            Instance = instance;
            EntryId = entryId;
            RightAlign = true;
        }

        public override (string, string) EvaluateTextAndColor()
        {
            if (HWHash.SENSORHASH.TryGetValue((Id, Instance, EntryId), out HWHash.HWINFO_HASH hash))
            {
                return (EvaluateText(hash), EvaluateColor(hash));
            }

            return ("-", Color);
        }

        public override string EvaluateColor()
        {
            if (HWHash.SENSORHASH.TryGetValue((Id, Instance, EntryId), out HWHash.HWINFO_HASH hash))
            {
                return EvaluateColor(hash);
            }

            return Color;
        }

        private string EvaluateColor(HWHash.HWINFO_HASH hash)
        {
            if (Threshold1 > 0 || Threshold2 > 0)
            {
                double sensorReadingValue;

                switch (ValueType)
                {
                    case SensorValueType.MIN:
                        sensorReadingValue = hash.ValueMin;
                        break;
                    case SensorValueType.MAX:
                        sensorReadingValue = hash.ValueMax;
                        break;
                    case SensorValueType.AVERAGE:
                        sensorReadingValue = hash.ValueAvg;
                        break;
                    default:
                        sensorReadingValue = hash.ValueNow;
                        break;
                }

                sensorReadingValue = sensorReadingValue * MultiplicationModifier + AdditionModifier;

                if (AbsoluteAddition)
                {
                    sensorReadingValue = Math.Abs(sensorReadingValue);
                }

                if (Threshold2 > 0 && sensorReadingValue >= Threshold2)
                {
                    return Threshold2Color;
                }
                else if (Threshold1 > 0 && sensorReadingValue >= Threshold1)
                {
                    return Threshold1Color;
                }
            }
            return Color;
        }

        public override string EvaluateText()
        {
            var value = "-";

            if (HWHash.SENSORHASH.TryGetValue((Id, Instance, EntryId), out HWHash.HWINFO_HASH hash))
            {
                value = EvaluateText(hash);
            }

            return value;
        }

        private string EvaluateText(HWHash.HWINFO_HASH hash)
        {
            var value = String.Empty;

            double sensorReadingValue;

            switch (ValueType)
            {
                case SensorValueType.MIN:
                    sensorReadingValue = hash.ValueMin;
                    break;
                case SensorValueType.MAX:
                    sensorReadingValue = hash.ValueMax;
                    break;
                case SensorValueType.AVERAGE:
                    sensorReadingValue = hash.ValueAvg;
                    break;
                default:
                    sensorReadingValue = hash.ValueNow;
                    break;
            }

            sensorReadingValue = sensorReadingValue * MultiplicationModifier + AdditionModifier;

            if (AbsoluteAddition)
            {
                sensorReadingValue = Math.Abs(sensorReadingValue);
            }

            if (OverridePrecision)
            {
                switch (Precision)
                {
                    case 1:
                        value = String.Format("{0:0.0}", sensorReadingValue);
                        break;
                    case 2:
                        value = String.Format("{0:0.00}", sensorReadingValue);
                        break;
                    case 3:
                        value = String.Format("{0:0.000}", sensorReadingValue);
                        break;
                    default:
                        value = String.Format("{0:0}", Math.Floor(sensorReadingValue));
                        break;
                }
            }
            else
            {
                switch (hash.Unit.ToLower())
                {
                    case "kb/s":
                    case "mb/s":
                    case "mbar/min":
                    case "mbar":
                        value = String.Format("{0:0.00}", sensorReadingValue);
                        break;
                    case "v":
                        value = String.Format("{0:0.000}", sensorReadingValue);
                        break;
                    default:
                        value = String.Format("{0:0}", sensorReadingValue);
                        break;
                }
            }


            if (ShowUnit)
            {
                if (OverrideUnit)
                {
                    value += Unit;
                }
                else
                {
                    value += hash.Unit;
                }
            }

            if (ShowName)
            {
                value = Name + " " + value;
            }

            return value;

        }
    }
}
