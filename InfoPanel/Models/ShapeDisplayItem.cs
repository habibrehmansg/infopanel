using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Xml.Serialization;

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
        private int _frameThickness = 5;

        [ObservableProperty]
        private string _frameColor = "#FFFF0000";

        [ObservableProperty]
        private bool _showFill = true;

        [ObservableProperty]
        private string _fillColor = "#FFFF0000";

        [ObservableProperty]
        private bool _showGradient = true;

        [ObservableProperty]
        private string _gradientColor = "#FF00FF00";

        [ObservableProperty]
        private string _gradientColor2 = "#FF0000FF";

        [ObservableProperty]
        private GradientType _gradientType = GradientType.Linear;

        [ObservableProperty]
        private int _gradientAngle = 0;

        [ObservableProperty]
        private int _gradientAnimationSpeed = 300; // milliseconds

        [XmlIgnore]
        private readonly Stopwatch _animationTimer = Stopwatch.StartNew();

        public ShapeDisplayItem()
        {
            // Default constructor
        }

        public ShapeDisplayItem(string name) : base(name)
        {
            Name = name;
        }

        public int GetGradientAnimationOffset()
        {
            if (GradientAnimationSpeed == 0)
                return GradientAngle;

            double degreesPerSecond = GradientAnimationSpeed;

            // Calculate the animated angle based on time
            double elapsedSeconds = _animationTimer.Elapsed.TotalSeconds;
            double animationOffset = elapsedSeconds * degreesPerSecond;

            // If GradientAngle is negative, rotate in opposite direction
            if (GradientAngle < 0)
            {
                animationOffset = -animationOffset;
            }

            // Add the animation offset to the base GradientAngle
            int animatedAngle = (int)(animationOffset) % 360;

            // Ensure the result is positive
            if (animatedAngle < 0)
                animatedAngle += 360;

            return animatedAngle;
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
