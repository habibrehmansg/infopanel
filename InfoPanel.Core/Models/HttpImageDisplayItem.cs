using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Sensors;
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

        private int _hwInfoRemoteIndex = -1;
        public int HwInfoRemoteIndex
        {
            get { return _hwInfoRemoteIndex; }
            set
            {
                SetProperty(ref _hwInfoRemoteIndex, value);
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

        /// <summary>
        /// Kept so the XML element stays; the URL now comes from the sensor at render time
        /// (<see cref="ResolveSource"/>), so this always serializes as null.
        /// </summary>
        public new string? HttpUrl
        {
            get { return null; }
            set { }
        }

        /// <summary>
        /// The sensor's text, if it is an absolute http(s) URL or a <c>plugin-image://</c> source;
        /// anything else (a local or UNC path, another scheme) gives null, as in 1.x.
        /// </summary>
        public override string? ResolveSource(RenderContext ctx)
        {
            var sensorReading = GetValue(ctx.Sensors);

            if (sensorReading.HasValue && sensorReading.Value.ValueText is { Length: > 0 } text && IsAllowedSource(text))
            {
                return text;
            }

            return null;
        }

        internal static bool IsAllowedSource(string text)
        {
            if (text.StartsWith("plugin-image://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && text.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase);
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

        public SensorReading? GetValue(ISensorReader sensors)
        {
            return SensorKeys.Read(this, sensors);
        }

        public override SKSize EvaluateSize(RenderContext ctx)
        {
            var result = new SKSize(Width, Height);

            if ((result.Width == 0 || result.Height == 0) && ResolveSource(ctx) is { Length: > 0 } source)
            {
                var cachedImage = ctx.Images.Get(source, PersistentCache);
                if (cachedImage != null)
                {
                    if (result.Width == 0)
                    {
                        result.Width = cachedImage.Width;
                    }

                    if (result.Height == 0)
                    {
                        result.Height = cachedImage.Height;
                    }
                }
            }

            if (result.Width != 0)
            {
                result.Width *= Scale / 100.0f;
            }

            if (result.Height != 0)
            {
                result.Height *= Scale / 100.0f;
            }

            return result;
        }
    }
}
