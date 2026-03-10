using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Enums;
using InfoPanel.Extensions;
using SkiaSharp;
using System;

namespace InfoPanel.Models
{
    [Serializable]
    public class HttpImageDisplayItem : ImageDisplayItem, ISensorItem
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

        public new ImageType Type
        {
            get { return ImageType.URL; }
            set { /* Do nothing, as this is always URL */  }
        }

        public new bool ReadOnly
        {
            get { return true; }
        }

        public new string? HttpUrl
        {
            get { return CalculatedPath; }
            set { }
        }

        public new string? CalculatedPath
        {
            get
            {
                var sensorReading = GetValue();

                if (sensorReading.HasValue && sensorReading.Value.ValueText != null)
                {
                    return sensorReading.Value.ValueText;
                }

                return null;
            }
        }

        public HttpImageDisplayItem(): base()
        { }

        public HttpImageDisplayItem(string name, Profile profile) : base(name, profile)
        {
            SensorName = name;
        }
        public HttpImageDisplayItem(string name, Profile profile, uint id, uint instance, uint entryId) : base(name, profile)
        {
            SensorName = name;
            SensorType = SensorType.HwInfo;
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }

        public HttpImageDisplayItem(string name, Profile profile, string libreSensorId) : base(name, profile)
        {
            SensorName = name;
            SensorType = SensorType.Libre;
            LibreSensorId = libreSensorId;
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

        public override SKSize EvaluateSize()
        {
            var result = base.EvaluateSize();

            if (result.Width == 0 || result.Height == 0)
            {
                var sensorReading = GetValue();

                if (sensorReading.HasValue && sensorReading.Value.ValueText != null && sensorReading.Value.ValueText.IsUrl())
                {
                    var cachedImage = InfoPanel.Cache.GetLocalImage(this);
                    if (cachedImage != null)
                    {
                        if (result.Width == 0)
                        {
                            result.Width = cachedImage.Width * Scale / 100.0f;
                        }

                        if (result.Height == 0)
                        {
                            result.Height = cachedImage.Height * Scale / 100.0f;
                        }
                    }
                }
            }

            return result;
        }
    }
}
