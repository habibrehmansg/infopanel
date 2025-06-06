using InfoPanel.Enums;
using InfoPanel.Extensions;
using SkiaSharp;
using System;

namespace InfoPanel.Models
{
    [Serializable]
    public class HttpImageDisplayItem : ImageDisplayItem, ISensorItem
    {
        public bool ReadOnlyFile
        {
            get { return true; }
        }

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

        public HttpImageDisplayItem(): base()
        { }

        public HttpImageDisplayItem(string name, Guid profileGuid) : base(name, profileGuid)
        {
            SensorName = name;
        }
        public HttpImageDisplayItem(string name, Guid profileGuid, uint id, uint instance, uint entryId) : base(name, profileGuid)
        {
            SensorName = name;
            SensorType = SensorType.HwInfo;
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }

        public HttpImageDisplayItem(string name, Guid profileGuid, string libreSensorId) : base(name, profileGuid)
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
