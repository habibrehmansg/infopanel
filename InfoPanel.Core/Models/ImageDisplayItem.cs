using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using InfoPanel.Extensions;
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
            }
        }

        private bool _relativePath = false;

        public bool RelativePath
        {
            get { return _relativePath; }
            set
            {
                SetProperty(ref _relativePath, value);
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
                if (string.IsNullOrEmpty(value) 
                    || value.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) 
                    || value.StartsWith("rtsps://"))
                {
                    SetProperty(ref _rtspUrl, value);
                }
            }
        }

        private string? _httpUrl;

        public string? HttpUrl
        {
            get { return _httpUrl; }
            set
            {
                if (string.IsNullOrEmpty(value)
                     || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("https://"))
                {
                    SetProperty(ref _httpUrl, value);
                }
            }
        }

        /// <summary>
        /// The image source to load: the RTSP or HTTP URL, or the file path. A relative path resolves
        /// inside the profile's assets folder, with <c>\</c> and <c>/</c> both read as separators, and gives null
        /// if it points outside it.
        /// </summary>
        public virtual string? ResolveSource(RenderContext ctx)
        {
            if (Type == ImageType.RTSP)
            {
                return RtspUrl;
            }

            if (Type == ImageType.URL)
            {
                return HttpUrl;
            }

            if (string.IsNullOrEmpty(FilePath))
            {
                return null;
            }

            if (RelativePath)
            {
                if (Profile is null)
                {
                    return null;
                }

                // 1.x on Windows stored relative paths with '\'; read either separator on every OS.
                var relative = FilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var assetsDirectory = ctx.Assets.GetProfileAssetsDirectory(Profile.Guid);
                var fullPath = Path.GetFullPath(Path.Combine(assetsDirectory, relative));

                return fullPath.IsSubdirectoryOf(assetsDirectory) ? fullPath : null;
            }

            return FilePath;
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

        public override SKSize EvaluateSize(RenderContext ctx) => EvaluateSize(ctx, Scale);

        /// <summary>
        /// The size at <paramref name="scale"/> percent instead of <see cref="Scale"/>: a gauge sizes its frames at
        /// its own scale without writing it into them (rendering never changes the model).
        /// </summary>
        public virtual SKSize EvaluateSize(RenderContext ctx, int scale)
        {
            var result = new SKSize(Width, Height);

            if (ResolveSource(ctx) is { Length: > 0 } source)
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
                result.Width *= scale / 100.0f;
            }

            if (result.Height != 0)
            {
                result.Height *= scale / 100.0f;
            }

            return result;
        }

        public override SKRect EvaluateBounds(RenderContext ctx)
        {
            var size = EvaluateSize(ctx);
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
