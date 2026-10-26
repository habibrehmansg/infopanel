using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Sensors;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;

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

        private bool _flipX = false;
        public bool FlipX
        {
            get { return _flipX; }
            set
            {
                SetProperty(ref _flipX, value);
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

        public SensorReading? GetValue(ISensorReader sensors)
        {
            return SensorKeys.Read(this, sensors);
        }

        /// <summary>
        /// Returns frame A (floor), optional frame B (ceil), and blend factor for crossfade.
        /// Advances the animated frame index (a <see cref="GaugeState"/> in <c>ctx.State</c>) once per tick.
        /// </summary>
        public void EvaluateImageFrame(RenderContext ctx, out ImageDisplayItem? imageA, out ImageDisplayItem? imageB, out float blend)
        {
            imageA = null;
            imageB = null;
            blend = 0f;

            if (_images.Count == 0) return;
            if (_images.Count == 1)
            {
                imageA = Images[0];
                return;
            }

            var sensorReading = GetValue(ctx.Sensors);
            if (!sensorReading.HasValue)
            {
                imageA = Images[0];
                return;
            }

            var state = ctx.State.Get<GaugeState>(Guid);

            if (state.LastTick != ctx.Tick)
            {
                state.LastTick = ctx.Tick;

                var step = 100.0 / (_images.Count - 1);
                var value = sensorReading.Value.ValueNow;
                value = ((value - _minValue) / (_maxValue - _minValue)) * 100;
                var targetIndex = (int)Math.Round(value / step);
                targetIndex = Math.Clamp(targetIndex, 0, Images.Count - 1);

                var now = ctx.Elapsed;
                // First evaluation: use a nominal delta (1/60s) so smoothing runs instead of snapping (log evidence: deltaSeconds=0 on first frame caused usedSmoothing=false).
                double deltaSeconds;
                if (state.LastUpdate is not TimeSpan lastUpdate)
                {
                    deltaSeconds = _animationSpeed > 0 ? 1.0 / 60.0 : 0;
                }
                else
                {
                    deltaSeconds = (now - lastUpdate).TotalSeconds;
                }
                state.LastUpdate = now;

                if (_animationSpeed > 0 && deltaSeconds > 0)
                {
                    var maxStep = _animationSpeed * deltaSeconds;
                    var diff = targetIndex - state.CurrentImageIndex;
                    if (Math.Abs(diff) <= maxStep)
                        state.CurrentImageIndex = targetIndex;
                    else
                        state.CurrentImageIndex += Math.Sign(diff) * maxStep;
                    state.CurrentImageIndex = Math.Clamp(state.CurrentImageIndex, 0, Images.Count - 1);
                }
                else
                {
                    state.CurrentImageIndex = targetIndex;
                }
            }

            var currentImageIndex = Math.Clamp(state.CurrentImageIndex, 0, Images.Count - 1);

            var idxFloor = (int)Math.Floor(currentImageIndex);
            var idxCeil = (int)Math.Ceiling(currentImageIndex);
            idxFloor = Math.Clamp(idxFloor, 0, Images.Count - 1);
            idxCeil = Math.Clamp(idxCeil, 0, Images.Count - 1);

            imageA = Images[idxFloor];

            if (idxCeil != idxFloor)
            {
                imageB = Images[idxCeil];
                blend = (float)(currentImageIndex - idxFloor);
            }
        }

        public ImageDisplayItem? GetCurrentImage(RenderContext ctx)
        {
            if(_images.Count > 0)
            {
                var currentImageIndex = Math.Clamp(ctx.State.Get<GaugeState>(Guid).CurrentImageIndex, 0, Images.Count - 1);
                var imageDisplayItem = Images[(int)Math.Round(currentImageIndex)];
                return imageDisplayItem;
            }

            return null;
        }

        public override SKRect EvaluateBounds(RenderContext ctx)
        {
            var size = EvaluateSize(ctx);
            return new SKRect(X, Y, X + size.Width, Y + size.Height);
        }

        public override SKSize EvaluateSize(RenderContext ctx)
        {
            if(Width != 0 && Height != 0)
            {
                return new SKSize(Width, Height);
            }

            var result = new SKSize(0, 0);

            if(GetCurrentImage(ctx) is ImageDisplayItem currentImage)
            {
                // At the gauge's scale; the frame's own Scale is not used (1.x wrote the gauge's into it while drawing).
                return currentImage.EvaluateSize(ctx, _scale);
            }

            return result;
        }

        public override string EvaluateText(RenderContext ctx)
        {
            return Name;
        }

        public override string EvaluateColor(RenderContext ctx)
        {
            return "#000000";
        }

        public override (string, string) EvaluateTextAndColor(RenderContext ctx)
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
