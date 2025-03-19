using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Drawing;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class SensorDisplayItem : TextDisplayItem, ISensorItem, IPluginSensorItem
    {
        private string _sensorName = string.Empty;
        public string SensorName
        {
            get { return _sensorName; }
            set
            {
                SetProperty(ref _sensorName, value);
            }
        }

        private Enums.SensorType _sensorType = Enums.SensorType.HwInfo;
        public Enums.SensorType SensorType
        {
            get { return _sensorType; }
            set
            {
                SetProperty(ref _sensorType, value);
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

        private double _threshold1 = 0;
        public double Threshold1
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

        private double _threshold2 = 0;
        public double Threshold2
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

        private string _unit = string.Empty;
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

        [ObservableProperty]
        private bool _divisionToggle = false;

        public SensorDisplayItem()
        {
            SensorName = string.Empty;
        }

        public SensorDisplayItem(string name) : base(name)
        {
            SensorName = name;
        }

        public SensorDisplayItem(string name, string libreSensorId) : base(name)
        {
            SensorName = name;
            SensorType = Enums.SensorType.Libre;
            LibreSensorId = libreSensorId;
        }

        public SensorDisplayItem(string name, uint id, uint instance, uint entryId) : base(name)
        {
            SensorName = name;
            SensorType = Enums.SensorType.HwInfo;
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }

        public SensorReading? GetValue()
        {
            return SensorType switch
            {
                Enums.SensorType.HwInfo => SensorReader.ReadHwInfoSensor(Id, Instance, EntryId),
                Enums.SensorType.Libre => SensorReader.ReadLibreSensor(LibreSensorId),
                Enums.SensorType.Plugin => SensorReader.ReadPluginSensor(PluginSensorId),
                _ => null,
            };
        }

        public override (string, string) EvaluateTextAndColor()
        {
            var value = GetValue();

            if (value.HasValue)
            {
                return (EvaluateText(value.Value), EvaluateColor(value.Value));
            }

            return ("-", Color);
        }

        public override string EvaluateColor()
        {
            var value = GetValue();

            if (value.HasValue)
            {
                return EvaluateColor(value.Value);
            }

            return Color;
        }

        private string EvaluateColor(SensorReading sensorReading)
        {
            if (Threshold1 > 0 || Threshold2 > 0)
            {
                double sensorReadingValue;

                switch (ValueType)
                {
                    case SensorValueType.MIN:
                        sensorReadingValue = sensorReading.ValueMin;
                        break;
                    case SensorValueType.MAX:
                        sensorReadingValue = sensorReading.ValueMax;
                        break;
                    case SensorValueType.AVERAGE:
                        sensorReadingValue = sensorReading.ValueAvg;
                        break;
                    default:
                        sensorReadingValue = sensorReading.ValueNow;
                        break;
                }

                if (DivisionToggle)
                {
                    if(MultiplicationModifier != 0)
                    {
                        sensorReadingValue = sensorReadingValue / MultiplicationModifier + AdditionModifier;
                    } else {
                        sensorReadingValue = sensorReadingValue + AdditionModifier;
                    }
                } else
                {
                    sensorReadingValue = sensorReadingValue * MultiplicationModifier + AdditionModifier;
                }


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
            var sensorReading = GetValue();

            if (sensorReading.HasValue)
            {
                return EvaluateText(sensorReading.Value);
            }

            return "-";
        }

        private string EvaluateText(SensorReading sensorReading)
        {
            string? value;
            // new string sensor handling
            if (!string.IsNullOrEmpty(sensorReading.ValueText))
            {
                value = sensorReading.ValueText;
            }
            else
            {

                double sensorReadingValue;

                switch (ValueType)
                {
                    case SensorValueType.MIN:
                        sensorReadingValue = sensorReading.ValueMin;
                        break;
                    case SensorValueType.MAX:
                        sensorReadingValue = sensorReading.ValueMax;
                        break;
                    case SensorValueType.AVERAGE:
                        sensorReadingValue = sensorReading.ValueAvg;
                        break;
                    default:
                        sensorReadingValue = sensorReading.ValueNow;
                        break;
                }

                if (DivisionToggle)
                {
                    if (MultiplicationModifier != 0)
                    {
                        sensorReadingValue = sensorReadingValue / MultiplicationModifier + AdditionModifier;
                    }
                    else
                    {
                        sensorReadingValue = sensorReadingValue + AdditionModifier;
                    }
                }
                else
                {
                    sensorReadingValue = sensorReadingValue * MultiplicationModifier + AdditionModifier;
                }

                if (AbsoluteAddition)
                {
                    sensorReadingValue = Math.Abs(sensorReadingValue);
                }


                if (OverridePrecision)
                {
                    switch (Precision)
                    {
                        case 1:
                            value = string.Format("{0:0.0}", sensorReadingValue);
                            break;
                        case 2:
                            value = string.Format("{0:0.00}", sensorReadingValue);
                            break;
                        case 3:
                            value = string.Format("{0:0.000}", sensorReadingValue);
                            break;
                        default:
                            value = string.Format("{0:0}", Math.Floor(sensorReadingValue));
                            break;
                    }
                }
                else
                {
                    switch (sensorReading.Unit.ToLower())
                    {
                        case "gb":
                            value = string.Format("{0:0.0}", sensorReadingValue);
                            break;
                        case "kb/s":
                        case "mb/s":
                        case "mbar/min":
                        case "mbar":
                            value = string.Format("{0:0.00}", sensorReadingValue);
                            break;
                        case "v":
                            value = string.Format("{0:0.000}", sensorReadingValue);
                            break;
                        default:
                            value = string.Format("{0:0}", sensorReadingValue);
                            break;
                    }
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
                    value += sensorReading.Unit;
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
