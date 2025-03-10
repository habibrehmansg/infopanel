using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using InfoPanel.Views.Components;
using Sentry;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;

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

        public override SizeF EvaluateSize()
        {
            using Bitmap bitmap = new(1, 1);
            using Graphics g = Graphics.FromImage(bitmap);
            using Font font = new(Font, FontSize);
            var measuredSize = g.MeasureString("A", font, 0, StringFormat.GenericTypographic);

            if (GetValue() is SensorReading sensorReading && sensorReading.ValueTable is DataTable table)
            {
                var formatParts = TableFormat.Split('|');
                var sizeF = new SizeF(0, measuredSize.Height * (MaxRows + (ShowHeader ? 1: 0)));

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

        public override Rect EvaluateBounds()
        {
            var size = EvaluateSize();
            if (GetValue() is SensorReading sensorReading && sensorReading.ValueTable is not null)
            {
                return new Rect(X, Y, size.Width, size.Height);
            }else
            {
                if (RightAlign)
                {
                    return new Rect(X - size.Width, Y, size.Width, size.Height);
                }
                else
                {
                    return new Rect(X, Y, size.Width, size.Height);
                }
            }
        }

    }
}
