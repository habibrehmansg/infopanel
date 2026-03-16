using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Enums;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class GaugeDisplayItem : DisplayItem, ISensorItem
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

        private SensorType _sensorType = SensorType.HwInfo;
        public SensorType SensorType
        {
            get { return _sensorType; }
            set
            {
                SetProperty(ref _sensorType, value);
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

        private double _minValue = 0.0;
        public double MinValue
        {
            get { return _minValue; }
            set
            {
                SetProperty(ref _minValue, value);
            }
        }

        private double _maxValue = 100.0;
        public double MaxValue
        {
            get { return _maxValue; }
            set
            {
                SetProperty(ref _maxValue, value);
            }
        }

        private int _scale = 100;
        public int Scale
        {
            get { return _scale; }
            set
            {
                SetProperty(ref _scale, value);
            }
        }

        /// <summary>
        /// Animation speed in index-units per second. 0 = instant (no smoothing). Typical: 5–20.
        /// </summary>
        private double _animationSpeed = 0;
        public double AnimationSpeed
        {
            get { return _animationSpeed; }
            set
            {
                SetProperty(ref _animationSpeed, value);
            }
        }

        [ObservableProperty]
        private int _width = 0;

        [ObservableProperty]
        private int _height = 0;


        private ObservableCollection<ImageDisplayItem> _images = [];

        public ObservableCollection<ImageDisplayItem> Images
        {
            get { return _images; }
            set
            {
                SetProperty(ref _images, value);
            }
        }

        private bool forward = true;
        private int counter = 0;

        public ImageDisplayItem? DisplayImage
        {
            get
            {
                if (_images.Count == 0)
                {
                    return null;
                }

                if (counter >= _images.Count || counter < 0)
                {
                    counter = 0;
                }

                if (counter >= _images.Count - 1)
                {
                    forward = false;
                }
                else if (counter <= 0)
                {
                    forward = true;
                }

                var result = _images.ElementAt(counter);
                if (forward)
                {
                    counter++;
                }
                else
                {
                    counter--;
                }

                return result;
            }
        }

        public void TriggerDisplayImageChange()
        {
            OnPropertyChanged(nameof(DisplayImage));
        }

        public GaugeDisplayItem()
        {
            Name = "Gauge";
        }

        public GaugeDisplayItem(string name, Profile profile) : base(name, profile)
        {
            SensorName = name;
        }

        public GaugeDisplayItem(string name, Profile profile, string libreSensorId) : base(name, profile)
        {
            SensorName = name;
            SensorType = SensorType.Libre;
            LibreSensorId = libreSensorId;
        }

        public GaugeDisplayItem(string name, Profile profile, UInt32 id, UInt32 instance, UInt32 entryId) : base(name, profile)
        {
            SensorName = name;
            SensorType = SensorType.HwInfo;
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }

        public SensorReading? GetValue()
        {
            return SensorType switch
            {
                SensorType.HwInfo => SensorReader.ReadHwInfoSensor(HwInfoRemoteIndex, Id, Instance, EntryId),
                SensorType.Libre => SensorReader.ReadLibreSensor(LibreSensorId),
                SensorType.Plugin => SensorReader.ReadPluginSensor(PluginSensorId),
                _ => null,
            };
        }

        private double currentImageIndex = 0;
        private DateTime _lastGaugeUpdate = DateTime.MinValue;

        /// <summary>
        /// Returns frame A (floor), optional frame B (ceil), and blend factor for crossfade.
        /// Updates currentImageIndex internally with smoothing logic.
        /// </summary>
        public void EvaluateImageFrame(out ImageDisplayItem? imageA, out ImageDisplayItem? imageB, out float blend)
        {
            imageA = null;
            imageB = null;
            blend = 0f;

            if (_images.Count == 0) return;
            if (_images.Count == 1)
            {
                imageA = Images[0];
                imageA.Scale = _scale;
                return;
            }

            var sensorReading = GetValue();
            if (!sensorReading.HasValue)
            {
                imageA = Images[0];
                imageA.Scale = _scale;
                return;
            }

            var step = 100.0 / (_images.Count - 1);
            var value = sensorReading.Value.ValueNow;
            value = ((value - _minValue) / (_maxValue - _minValue)) * 100;
            var targetIndex = (int)Math.Round(value / step);
            targetIndex = Math.Clamp(targetIndex, 0, Images.Count - 1);

            var now = DateTime.UtcNow;
            // First evaluation: use a nominal delta (1/60s) so smoothing runs instead of snapping (log evidence: deltaSeconds=0 on first frame caused usedSmoothing=false).
            double deltaSeconds;
            if (_lastGaugeUpdate == DateTime.MinValue)
            {
                deltaSeconds = _animationSpeed > 0 ? 1.0 / 60.0 : 0;
                _lastGaugeUpdate = now;
            }
            else
            {
                deltaSeconds = (now - _lastGaugeUpdate).TotalSeconds;
                _lastGaugeUpdate = now;
            }

            if (_animationSpeed > 0 && deltaSeconds > 0)
            {
                var maxStep = _animationSpeed * deltaSeconds;
                var diff = targetIndex - currentImageIndex;
                if (Math.Abs(diff) <= maxStep)
                    currentImageIndex = targetIndex;
                else
                    currentImageIndex += Math.Sign(diff) * maxStep;
                currentImageIndex = Math.Clamp(currentImageIndex, 0, Images.Count - 1);
            }
            else
            {
                currentImageIndex = targetIndex;
            }

            var idxFloor = (int)Math.Floor(currentImageIndex);
            var idxCeil = (int)Math.Ceiling(currentImageIndex);
            idxFloor = Math.Clamp(idxFloor, 0, Images.Count - 1);
            idxCeil = Math.Clamp(idxCeil, 0, Images.Count - 1);

            imageA = Images[idxFloor];
            imageA.Scale = _scale;

            if (idxCeil != idxFloor)
            {
                imageB = Images[idxCeil];
                imageB.Scale = _scale;
                blend = (float)(currentImageIndex - idxFloor);
            }
        }

        public ImageDisplayItem? CurrentImage
        {
            get
            {
                if(_images.Count > 0)
                {
                    currentImageIndex = Math.Clamp(currentImageIndex, 0, Images.Count - 1);
                    var imageDisplayItem = Images[(int)Math.Round(currentImageIndex)];
                    imageDisplayItem.Scale = _scale;
                    return imageDisplayItem;
                }

                return null;
            }
        }

        public override SKRect EvaluateBounds()
        {
            var size = EvaluateSize();
            return new SKRect(X, Y, X + size.Width, Y + size.Height);
        }

        public override SKSize EvaluateSize()
        {
            if(Width != 0 && Height != 0)
            {
                return new SKSize(Width, Height);
            }

            var result = new SKSize(0, 0);

            if(CurrentImage != null)
            {
                return CurrentImage.EvaluateSize();
            }

            return result;
        }

        public override string EvaluateText()
        {
            return Name;
        }

        public override string EvaluateColor()
        {
            return "#000000";
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (Name, "#000000");
        }

        public override void SetProfile(Profile profile)
        {
            base.SetProfile(profile);

            foreach (var imageDisplayItem in Images)
            {
                imageDisplayItem.SetProfile(profile);
                imageDisplayItem.PersistentCache = true; // Ensure gauge images never expire
            }
        }

        public override object Clone()
        {
            var clone = (GaugeDisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            clone.currentImageIndex = 0;
            clone._lastGaugeUpdate = DateTime.MinValue;

            clone.Images = new ObservableCollection<ImageDisplayItem>();

            foreach(var imageDisplayItem in Images)
            {
                var cloneImage = (ImageDisplayItem) imageDisplayItem.Clone();
                cloneImage.Guid = Guid.NewGuid();
                cloneImage.PersistentCache = true; // Ensure gauge images never expire
                clone.Images.Add(cloneImage);
            }

            return clone;
        }
    }
}
