using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;
using System;

namespace InfoPanel.Models
{
    public partial class ShapeDisplayItem : DisplayItem
    {
        public enum ShapeType
        {
            // Basic Shapes (4-sided)
            Rectangle,
            Capsule,
            Trapezoid,
            Parallelogram,

            // Circular Shapes
            Ellipse,

            // Polygons (by number of sides)
            Triangle,
            Pentagon,
            Hexagon,
            Octagon,

            // Symbols/Special Shapes
            Star,
            Plus,
            Arrow,
        }

        [ObservableProperty]
        private ShapeType _type = ShapeType.Rectangle;

        [ObservableProperty]
        private int _width = 100;

        [ObservableProperty]
        private int _height = 100;

        [ObservableProperty]
        private int _cornerRadius = 25;

        [ObservableProperty]
        private bool _showFrame = true;

        [ObservableProperty]
        private string _frameColor = "#000000";

        [ObservableProperty]
        private bool _showFill = true;

        [ObservableProperty]
        private string _fillColor = "#FFBF00FF";

        [ObservableProperty]
        private bool _showGradient = true;

        [ObservableProperty]
        private string _gradientColor = "#FF00FFFF";

        [ObservableProperty]
        private int _gradientAngle = 0;

        public ShapeDisplayItem()
        {
            // Default constructor
        }

        public ShapeDisplayItem(string name) : base(name)
        {
            Name = name;
        }

        public override object Clone()
        {
            var clone = (DisplayItem)MemberwiseClone();
            clone.Guid = Guid.NewGuid();
            return clone;
        }

        public override SKRect EvaluateBounds()
        {
            return new SKRect(X, Y, X + Width, Y + Height);
        }

        public override string EvaluateColor()
        {
            return "#000000"; // Default color for shapes, can be overridden
        }

        public override SKSize EvaluateSize()
        {
            return new SKSize(Width, Height); // Default size for shapes, can be overridden
        }

        public override string EvaluateText()
        {
            return Name;
        }

        public override (string, string) EvaluateTextAndColor()
        {
            return (EvaluateText(), EvaluateColor());
        }

        public override void SetProfileGuid(Guid profileGuid)
        {
            this.ProfileGuid = profileGuid;
        }
    }
}
