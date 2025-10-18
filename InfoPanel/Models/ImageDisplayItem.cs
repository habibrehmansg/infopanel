﻿using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;
using System;
using System.IO;

namespace InfoPanel.Models
{
    [Serializable]
    public partial class ImageDisplayItem : DisplayItem
    {
        public enum ImageType
        {
            FILE, URL, RTSP
        }

        private ImageType _type = ImageType.FILE;
        public ImageType Type
        {
            get { return _type; }
            set
            {
                SetProperty(ref _type, value);
                OnPropertyChanged(nameof(CalculatedPath));
            }
        }

        public bool ReadOnly
        {
            get { return false; }
            set { /* Do nothing, as this is always writable */ }
        }

        [ObservableProperty]
        private bool _showPanel = false;

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

        [ObservableProperty]
        private int _volume = 0;

        private string? _rtspUrl;

        public string? RtspUrl
        {
            get { return _rtspUrl; }
            set
            {
                var previousValue = _rtspUrl;
                if (string.IsNullOrEmpty(value) 
                    || value.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) 
                    || value.StartsWith("rtsps://"))
                {
                    SetProperty(ref _rtspUrl, value);
                    OnPropertyChanged(nameof(CalculatedPath));
                    if (!string.IsNullOrEmpty(previousValue))
                    {
                        InfoPanel.Cache.InvalidateImage(previousValue);
                    }
                }
            }
        }

        private string? _httpUrl;

        public string? HttpUrl
        {
            get { return _httpUrl; }
            set
            {
                var previousValue = _httpUrl;
                if (string.IsNullOrEmpty(value)
                     || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("https://"))
                {
                    SetProperty(ref _httpUrl, value);
                    OnPropertyChanged(nameof(CalculatedPath));

                    if (!string.IsNullOrEmpty(previousValue))
                    {
                        InfoPanel.Cache.InvalidateImage(previousValue);
                    }
                }
            }
        }

        public string? CalculatedPath
        {
            get
            {
                if (Type == ImageType.RTSP)
                {
                    return RtspUrl;
                }

                if (Type == ImageType.URL)
                {
                    return HttpUrl;
                }

                if (RelativePath)
                {
                    if (FilePath != null)
                    {
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "InfoPanel", "assets", ProfileGuid.ToString(), FilePath);
                    }
                    else
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

        private bool _persistentCache = false;
        [System.Xml.Serialization.XmlIgnore]
        public bool PersistentCache
        {
            get { return _persistentCache; }
            set
            {
                SetProperty(ref _persistentCache, value);
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

        public ImageDisplayItem(string name, Profile profile) : base(name, profile)
        {
        }

        public ImageDisplayItem(string name, Profile profile, string filePath, bool relativePath) : base(name, profile)
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
                var cachedImage = InfoPanel.Cache.GetLocalImage(this, false);
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
    }
}
