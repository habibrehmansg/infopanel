using System;

namespace InfoPanel.Models
{
    [Serializable]
    public class SensorImageDisplayItem : ImageDisplayItem, ISensorItem
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

        private double _threshold2 = 100;
        public double Threshold2
        {
            get { return _threshold2; }
            set
            {
                SetProperty(ref _threshold2, value);
            }
        }

        public SensorImageDisplayItem(): base()
        { }

        public SensorImageDisplayItem(string name, Guid profileGuid, uint id, uint instance, uint entryId) : base(name, profileGuid)
        {
            SensorName = name;
            SensorType = SensorType.HwInfo;
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }

        public SensorImageDisplayItem(string name, Guid profileGuid, string libreSensorId) : base(name, profileGuid)
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
                _ => null,
            };
        }

        public bool ShouldShow()
        {
            var sensorReading = GetValue();

            if (sensorReading.HasValue)
            {
                double value = 0;
                switch (ValueType)
                {
                    case SensorValueType.MIN:
                        value = sensorReading.Value.ValueMin;
                        break;
                    case SensorValueType.MAX:
                        value = sensorReading.Value.ValueMax;
                        break;
                    case SensorValueType.AVERAGE:
                        value = sensorReading.Value.ValueAvg;
                        break;
                    case SensorValueType.NOW:
                        value = sensorReading.Value.ValueNow;
                        break;
                }

                if(value >= Threshold1 && value <= Threshold2)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
