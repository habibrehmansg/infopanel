using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;
using System;
using System.Data;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class TableSensorDisplayItem: TextDisplayItem, IPluginSensorItem
    {
        [ObservableProperty]
        private string _sensorName = string.Empty;

        [ObservableProperty]
        private Enums.SensorType _sensorType = Enums.SensorType.Plugin;

        [ObservableProperty]
        public SensorValueType _valueType = SensorValueType.NOW;

        [ObservableProperty]
        private string _pluginSensorId = string.Empty;

        [ObservableProperty]
        private int _maxRows = 10;

        [ObservableProperty]
        private bool _showHeader = true;

        [ObservableProperty]
        private string _tableFormat = "0:10|1:10|2:10|3:10|4:10|5:10";

        public TableSensorDisplayItem()
        {
            SensorName = string.Empty;
        }

        public TableSensorDisplayItem(string name) : base(name)
        {
            SensorName = name;
        }

        public TableSensorDisplayItem(string name, string pluginSensorId) : base(name)
        {
            SensorName = name;
            SensorType = Enums.SensorType.Plugin;
            PluginSensorId = pluginSensorId;
        }

        public SensorReading? GetValue()
        {
            return SensorReader.ReadPluginSensor(PluginSensorId);
        }

        public override SKSize EvaluateSize()
        {
            SKFontStyleWeight weight = Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            SKFontStyleWidth widthStyle = SKFontStyleWidth.Normal;
            SKFontStyleSlant slant = Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

            using var fontStyle = new SKFontStyle(weight, widthStyle, slant);
            using var typeface = SKTypeface.FromFamilyName(Font, fontStyle);
            using var font = new SKFont(typeface, size: FontSize * 1.33f);

            var metrics = font.Metrics;
            var text = "A";

            float width = font.MeasureText(text);
            float height = metrics.Descent - metrics.Ascent;

            if (GetValue() is SensorReading sensorReading && sensorReading.ValueTable is DataTable table)
            {
                var formatParts = TableFormat.Split('|');
                var sizeF = new SKSize(0, height * (MaxRows + (ShowHeader ? 1: 0)));

                for (int i = 0; i < formatParts.Length; i++)
                {
                    var split = formatParts[i].Split(':');
                    if (split.Length == 2)
                    {
                        if (int.TryParse(split[0], out var column) && column < table.Columns.Count && int.TryParse(split[1], out var length))
                        {
                            sizeF.Width += length + 10;
                        }
                    }
                }
                return sizeF;    
            }

            return base.EvaluateSize();
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), Color);
        }

        public override string EvaluateText()
        {
            return "Invalid sensor";
        }

        public override SKRect EvaluateBounds()
        {
            var size = EvaluateSize();
            if (GetValue() is SensorReading sensorReading && sensorReading.ValueTable is not null)
            {
                return new SKRect(X, Y, X + size.Width, Y + size.Height);
            }
            else
            {
                int rectX = Width == 0 && RightAlign ? (int)(X - size.Width) : X;
                return new SKRect(rectX, Y, rectX + size.Width, Y + size.Height);
            }
        }

    }
}
