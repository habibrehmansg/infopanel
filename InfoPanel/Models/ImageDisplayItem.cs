using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;

namespace InfoPanel.Models
{
    [Serializable]
    public class ImageDisplayItem : DisplayItem
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

        private int _scale = 100;
        public int Scale
        {
            get { return _scale; }
            set
            {
                SetProperty(ref _scale, value);
            }
        }

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

                if (!value.StartsWith("#"))
                {
                    value = "#" + value;
                }

                try
                {
                    ColorTranslator.FromHtml(value);
                    SetProperty(ref _layerColor, value);
                }
                catch
                { }
            }
        }

        //for serialisation only
        private ImageDisplayItem()
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

        public override SizeF EvaluateSize()
        {
            var result = new SizeF(0, 0);

            if (CalculatedPath != null)
            {
                Cache.GetLocalImage(CalculatedPath)?.Access(image =>
                {
                    result.Width = (int)(image.Width * Scale / 100.0f);
                    result.Height = (int)(image.Height * Scale / 100.0f);
                });
            }

            return result;
        }

        public override Rect EvaluateBounds()
        {
            var size = EvaluateSize();
            return new Rect(X, Y, size.Width, size.Height);
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }
    }
}
