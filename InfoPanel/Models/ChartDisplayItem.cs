using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Enums;
using SkiaSharp;
using System;

namespace InfoPanel.Models
{
    [Serializable]
    public abstract class ChartDisplayItem : DisplayItem, ISensorItem
    {
        private string _sensorName = String.Empty;
        public string SensorName
        {
            get { return _sensorName; }
            set
            {
                SetProperty(ref _sensorName, value);
            }
        }

        private SensorType _sensorIdType = SensorType.HwInfo;
        public SensorType SensorType
        {
            get { return _sensorIdType; }
            set
            {
                SetProperty(ref _sensorIdType, value);
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

        private string _libreSensorId = string.Empty;
        public string LibreSensorId
        {
            get { return _libreSensorId; }
            set
            {
                SetProperty(ref _libreSensorId, value);
            }
        }

        private string _pluginSensorId = string.Empty;
        public string PluginSensorId
        {
            get { return _pluginSensorId; }
            set
            {
                SetProperty(ref _pluginSensorId, value);
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

        private int _minValue = 0;
        public int MinValue
        {
            get { return _minValue; }
            set
            {
                SetProperty(ref _minValue, value);
            }
        }

        private int _maxValue = 100;
        public int MaxValue
        {
            get { return _maxValue; }
            set
            {
                SetProperty(ref _maxValue, value);
            }
        }

        private bool _autoValue = false;
        public bool AutoValue
        {
            get { return _autoValue; }
            set
            {
                SetProperty(ref _autoValue, value);
            }
        }

        private int _width = 400;
        public int Width
        {
            get { return _width; }
            set
            {
                if (value <= 0)
                {
                    return;
                }

                SetProperty(ref _width, value);
            }
        }

        private int _height = 50;
        public int Height
        {
            get { return _height; }
            set
            {
                if (value <= 0)
                {
                    return;
                }
                SetProperty(ref _height, value);
            }
        }

        private bool _flipX = false;

        public bool FlipX
        {
            get { return _flipX; }
            set
            {
                SetProperty(ref _flipX, value);
            }
        }

        private bool _frame = true;
        public bool Frame
        {
            get { return _frame; }
            set
            {
                SetProperty(ref _frame, value);
            }
        }

        private string _frameColor = "#000000";
        public string FrameColor
        {
            get { return _frameColor; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _frameColor, value);
                }
                catch
                { }
            }
        }

        private bool _background = true;
        public bool Background
        {
            get { return _background; }
            set
            {
                SetProperty(ref _background, value);
            }
        }

        private string _backgroundColor = "#FFFFFF";
        public string BackgroundColor
        {
            get { return _backgroundColor; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _backgroundColor, value);
                }
                catch
                { }
            }
        }

        private string _color = "#808080";
        public string Color
        {
            get { return _color; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _color, value);
                }
                catch
                { }
            }

    }

        public ChartDisplayItem() { }

        public ChartDisplayItem(string name) : base(name)
        {
            SensorName = name;
        }

        public ChartDisplayItem(string name, string libreSensorId) : base(name)
        {
            SensorName = name;
            SensorType = SensorType.Libre;
            LibreSensorId = libreSensorId;
        }

        public ChartDisplayItem(string name, UInt32 id, UInt32 instance, UInt32 entryId)
        {
            SensorName = name;
            SensorType = SensorType.HwInfo;
            Name = name;
            SensorName = name;
            Id = id;
            Instance = instance;
            EntryId = entryId;
            
        }

        public SensorReading? GetValue()
        {
            return SensorType switch
            {
                SensorType.HwInfo => SensorReader.ReadHwInfoSensor(Id, Instance, EntryId),
                SensorType.Libre => SensorReader.ReadLibreSensor(LibreSensorId),
                SensorType.Plugin => SensorReader.ReadPluginSensor(PluginSensorId),
                _ => null,
            };
        }

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

        public override SKSize EvaluateSize()
        {
            return new SKSize(Width, Height);
        }
        public override SKRect EvaluateBounds()
        {
            var size = EvaluateSize();
            return new SKRect(X, Y, X + size.Width, Y + size.Height);
        }

        public override void SetProfileGuid(Guid profileGuid)
        {
            ProfileGuid = profileGuid;
        }
    }

    public class GraphDisplayItem : ChartDisplayItem
    {
        public enum GraphType
        {
            LINE, HISTOGRAM
        }

        private GraphType _type = GraphType.LINE;
        public GraphType Type
        {
            get { return _type; }
            set
            {
                SetProperty(ref _type, value);
            }
        }

        private int _thickness = 2;
        public int Thickness
        {
            get { return _thickness; }
            set
            {
                if (value <= 0)
                {
                    return;
                }
                SetProperty(ref _thickness, value);
            }
        }

        private int _step = 4;
        public int Step
        {
            get { return _step; }
            set
            {
                if (value <= 0)
                {
                    return;
                }
                SetProperty(ref _step, value);
            }
        }

        private bool _fill = true;
        public bool Fill
        {
            get { return _fill; }
            set
            {
                SetProperty(ref _fill, value);
            }
        }

        private string _fillColor = "#3C888DFF";
        public string FillColor
        {
            get { return _fillColor; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _fillColor, value);
                }
                catch
                { }
            }
        }

        public GraphDisplayItem()
        {
            Name = "Graph";
        }

        public GraphDisplayItem(string name, GraphType type) : base(name)
        {
            Type = type;
        }

        public GraphDisplayItem(string name, GraphType type, string libreSensorId) : base(name, libreSensorId)
        {
            Type = type;
        }

        public GraphDisplayItem(string name, GraphType type, UInt32 id, UInt32 instance, UInt32 entryId) : base(name, id, instance, entryId)
        {
            Type = type;
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
    }

    public partial class BarDisplayItem : ChartDisplayItem
    {

        [ObservableProperty]
        private int _cornerRadius = 0;

        private bool _gradient = true;
        public bool Gradient
        {
            get { return _gradient; }
            set
            {
                SetProperty(ref _gradient, value);
            }
        }

        private string _gradientColor = "#3B3B3B";
        public string GradientColor
        {
            get { return _gradientColor; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _gradientColor, value);
                }
                catch
                { }
            }
        }

        public BarDisplayItem()
        {
            Name = "Bar";
        }

        public BarDisplayItem(string name) : base(name)
        { }

        public BarDisplayItem(string name, string libreSensorId) : base(name, libreSensorId)
        { }

        public BarDisplayItem(string name, UInt32 id, UInt32 instance, UInt32 entryId) : base(name, id, instance, entryId)
        { }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
    }

    public class DonutDisplayItem : ChartDisplayItem
    {
        public int Radius
        {
            get { return Width / 2; }
            set
            {
                Width = value * 2;
                Height = value * 2;
            }
        }

        private int _thickness = 10;
        public int Thickness
        {
            get { return _thickness; }
            set
            {
                if (value <= 0)
                {
                    return;
                }
                SetProperty(ref _thickness, value);
            }
        }

        private int _span = 360;

        public int Span
        {
            get { return _span; }
            set
            {
                if (value < 1 || value > 360)
                {
                    return;
                }
                SetProperty(ref _span, value);
            }
        }

        private int _rotation = 90;
        public int Rotation
        {
            get { return _rotation; }
            set
            {
                if (value < 0 || value > 360)
                {
                    return;
                }
                SetProperty(ref _rotation, value);
            }
        }

        public DonutDisplayItem()
        {
            Name = "Donut";
        }

        public DonutDisplayItem(string name) : base(name)
        {
            Frame = false;
            BackgroundColor = "#FFDCDCDC";
            Width = 100; Height = 100;
        }

        public DonutDisplayItem(string name, string libreSensorId) : base(name, libreSensorId)
        {
            Frame = false;
            BackgroundColor = "#FFDCDCDC";
            Width = 100; Height = 100;
        }

        public DonutDisplayItem(string name, UInt32 id, UInt32 instance, UInt32 entryId) : base(name, id, instance, entryId)
        {
            Frame = false;
            BackgroundColor = "#FFDCDCDC";
            Width = 100; Height = 100; 
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
    }
}
