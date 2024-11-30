using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Views.Components.Custom;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace InfoPanel.Models
{
    [Serializable]
    public class GaugeDisplayItem : DisplayItem, ISensorItem
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

        public string? DisplayImage
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

                var result = _images.ElementAt(counter).CalculatedPath;
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

        public GaugeDisplayItem(string name, string libreSensorId) : base(name)
        {
            SensorType = SensorType.Libre;
            LibreSensorId = libreSensorId;
        }

        public GaugeDisplayItem(string name, UInt32 id, UInt32 instance, UInt32 entryId) : base(name)
        {
            SensorType = SensorType.HwInfo;
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
                _ => null,
            };
        }

        private double currentImageIndex = 0;
        public ImageDisplayItem? EvaluateImage(double interpolationDelay = 1)
        {
            ImageDisplayItem? result = null;
            if (_images.Count == 1)
            {
                result = Images[0];
            }

            if (_images.Count > 1)
            {
                var sensorReading = GetValue();
                if(sensorReading.HasValue) {
                    var step = 100.0 / (_images.Count - 1);

                    var value = sensorReading.Value.ValueNow;
                    value = ((value - _minValue) / (_maxValue - _minValue)) * 100;

                    var index = (int)(value / step);

                    var intermediateIndex = Interpolate(currentImageIndex, index, interpolationDelay * 2);
                    intermediateIndex = Math.Clamp(intermediateIndex, 0, Images.Count - 1);
                    currentImageIndex = intermediateIndex;

                    result = Images[(int)Math.Round(intermediateIndex)];
                }
            }

            if(result != null)
            {
                result.Scale = _scale;
            }

            return result;
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

        private static double Interpolate(double startValue, int endValue, double position)
        {
            // Ensure position is within the range of 0 to 100
            position = Math.Clamp(position, 0, 1);

            // Handle case where start and target positions are equal
            if (startValue == endValue)
            {
                return startValue;
            }

            // Calculate the interpolated value
            double interpolatedValue = startValue + (endValue - startValue) * position;

            return interpolatedValue;
        }

        public override Rect EvaluateBounds()
        {
            var size = EvaluateSize();
            return new Rect(X, Y, size.Width, size.Height);
        }

        public override SizeF EvaluateSize()
        {
            var result = new SizeF(0, 0);

            if(CurrentImage != null)
            {
                Cache.GetLocalImage(CurrentImage)?.Access(image =>
                {
                    result.Width = (int)(image.Width * Scale / 100.0f);
                    result.Height = (int)(image.Height * Scale / 100.0f);
                });
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

        public override object Clone()
        {
            var clone = (GaugeDisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();

            clone.Images = new ObservableCollection<ImageDisplayItem>();

            foreach(var imageDisplayItem in Images)
            {
                var cloneImage = (ImageDisplayItem) imageDisplayItem.Clone();
                cloneImage.Guid = Guid.NewGuid();
                clone.Images.Add(cloneImage);
            }

            return clone;
        }
    }
}
