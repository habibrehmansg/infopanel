using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;
using System;
using System.IO;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class ImageDisplayItem : DisplayItem
    {
        private string? _filePath;
        public string? FilePath
        {
            get { return _filePath; }
            set
            {
                SetProperty(ref _filePath, value);
                OnPropertyChanged(nameof(CalculatedPath));
            }
        }

        private bool _relativePath = false;

        public bool RelativePath
        {
            get { return _relativePath; }
            set
            {
                SetProperty(ref _relativePath, value);
                OnPropertyChanged(nameof(CalculatedPath));
            }
        }

        public string? CalculatedPath
        {
            get
            {
                if(RelativePath)
                {
                    if (FilePath != null)
                    {
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "InfoPanel", "assets", ProfileGuid.ToString(), FilePath);
                    } else
                    {
                        return null;
                    }
                }

                return FilePath;
            }
        }

        private bool _cache = true;
        public bool Cache
        {
            get { return _cache; }
            set
            {
                SetProperty(ref _cache, value);
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

        [ObservableProperty]
        private int _width = 0;

        [ObservableProperty]
        private int _height = 0;

        private bool _layer = false;
        public bool Layer
        {
            get { return _layer; }
            set
            {
                SetProperty(ref _layer, value);
            }
        }

        private string _layerColor = "#77FFFFFF";
        public string LayerColor
        {
            get { return _layerColor; }
            set
            {
                if (value == null)
                {
                    return;
                }

                if (!value.StartsWith('#'))
                {
                    value = "#" + value;
                }

                try
                {
                    SKColor.Parse(value);
                    SetProperty(ref _layerColor, value);
                }
                catch
                { }
            }
        }

        //for serialisation only
        public ImageDisplayItem()
        { }

        public ImageDisplayItem(string name, Guid profileGuid): base()
        {
            this.Name = name;
            this.ProfileGuid= profileGuid;
        }

        public ImageDisplayItem(string name, Guid profileGuid, string filePath, bool relativePath) : base(name, profileGuid)
        {
            this.FilePath = filePath;
            this.RelativePath = relativePath;
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

        public override SKSize EvaluateSize()
        {
            var result = new SKSize(Width, Height);

            if (CalculatedPath != null)
            {
                var cachedImage = InfoPanel.Cache.GetLocalImage(this);
                if (cachedImage != null)
                {
                    if(result.Width == 0)
                    {
                        result.Width = cachedImage.Width;
                    }

                    if (result.Height == 0)
                    {
                        result.Height = cachedImage.Height;
                    }
                }
            }

            if(result.Width != 0)
            {
                result.Width *= Scale / 100.0f;
            }

            if(result.Height != 0)
            {
                result.Height *= Scale / 100.0f;
            }

            return result;
        }

        public override SKRect EvaluateBounds()
        {
            var size = EvaluateSize();
            return new SKRect(X, Y, X + size.Width, Y + size.Height);
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
        public override void SetProfileGuid(Guid profileGuid)
        {
            ProfileGuid = profileGuid;
        }
    }
}
