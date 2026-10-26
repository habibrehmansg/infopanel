using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace InfoPanel.Models
{
    public partial class GuideDisplayItem : DisplayItem
    {
        /// <summary>Click/snap tolerance perpendicular to the guide line, in profile pixels.</summary>
        private const float HitTestPadding = 10f;

        [ObservableProperty]
        private int _length = 200;

        [ObservableProperty]
        private string _color = "#FF00FFFF";

        public GuideDisplayItem()
        {
        }

        public GuideDisplayItem(string name, Profile profile) : base(name, profile)
        {
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = System.Guid.NewGuid();
            return clone;
        }

        public override SKRect EvaluateBounds()
        {
            return new SKRect(X - Length / 2f, Y - HitTestPadding / 2f, X + Length / 2f, Y + HitTestPadding / 2f);
        }

        public override string EvaluateColor()
        {
            return Color;
        }

        public override SKSize EvaluateSize()
        {
            return new SKSize(Length, HitTestPadding);
        }

        public override string EvaluateText()
        {
            return Name;
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), EvaluateColor());
        }
    }
}
