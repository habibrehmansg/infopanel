using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Drawing;
using SkiaSharp;
using System;
using System.Linq;
using System.Xml.Serialization;

namespace InfoPanel.Models;

[Serializable]
public abstract partial class DisplayItem : ObservableObject, ICloneable
{
    /// <summary>
    /// The item's id. Serialized as a <c>&lt;Guid&gt;</c> element since 2.0 (1.x ignored it and ignores the element);
    /// files without it get fresh ids on load, and <c>ItemTree.EnsureUniqueIds</c> repairs empty or duplicate ones.
    /// </summary>
    public Guid Guid { get; set; } = Guid.NewGuid();

    [XmlIgnore]
    public Profile Profile { get; private set; }

    [XmlIgnore]
    public Guid ProfileGuid => Profile.Guid;

    [ObservableProperty]
    private bool _isLocked = false;

    [RelayCommand]
    private void ToggleLock()
    {
        IsLocked = !IsLocked;
    }

    private string _name;
    public string Name
    {
        get { return _name; }
        set
        {
            SetProperty(ref _name, value);
        }
    }

    public string Kind
    {
        get
        {
            switch (this)
            {
                case GroupDisplayItem:
                    return "Group";
                case SensorDisplayItem:
                    return "Sensor";
                case TableSensorDisplayItem:
                    return "Table";
                case ClockDisplayItem:
                    return "Clock";
                case CalendarDisplayItem:
                    return "Calendar";
                case HttpImageDisplayItem:
                    return "Http Image";
                case SensorImageDisplayItem:
                    return "Sensor Image";
                case ImageDisplayItem:
                    return "Image";
                case TextDisplayItem:
                    return "Text";
                case GraphDisplayItem:
                    return "Graph";
                case BarDisplayItem:
                    return "Bar";
                case GaugeDisplayItem:
                    return "Gauge";
                case DonutDisplayItem:
                    return "Donut";
                case ShapeDisplayItem:
                    return "Shape";
                default:
                    return "";
            }
        }
    }

    private bool _hidden = false;
    public bool Hidden
    {
        get
        {
            return _hidden;
        }
        set
        {
            SetProperty(ref _hidden, value);
        }
    }

    protected DisplayItem()
    {
        _name = "DisplayItem";
    }

    protected DisplayItem(string name, Profile profile)
    {
        _name = name;
        Profile = profile;
    }

    private int _x = 100;
    public int X
    {
        get { return _x; }
        set
        {
            SetProperty(ref _x, value);
        }
    }
    private int _y = 100;
    public int Y
    {
        get { return _y; }
        set
        {
            SetProperty(ref _y, value);
        }
    }

    [ObservableProperty]
    private int _rotation = 0;

    public virtual void SetProfile(Profile profile)
    {
        Profile = profile;
    }

    public abstract string EvaluateText(RenderContext ctx);

    public abstract string EvaluateColor(RenderContext ctx);

    public abstract (string, string) EvaluateTextAndColor(RenderContext ctx);

    public abstract SKSize EvaluateSize(RenderContext ctx);

    public abstract SKRect EvaluateBounds(RenderContext ctx);

    public DisplayItem[] Flatten()
    {
        if (this is GroupDisplayItem groupItem)
        {
            return [.. groupItem.DisplayItems.SelectMany(item => item.Flatten())];
        }
        else if (this is GaugeDisplayItem gaugeDisplayItem)
        {
            return [.. gaugeDisplayItem.Images];
        }

        return [this];
    }

    public abstract object Clone();

    public bool ContainsPoint(SKPoint worldPoint, RenderContext ctx)
    {
        var bounds = EvaluateBounds(ctx);
        double centerX = bounds.MidX;
        double centerY = bounds.MidY;

        // Transform world point to local coordinates (inverse rotation)
        double rotationRadians = -Rotation * Math.PI / 180.0; // Negative for inverse
        double cos = Math.Cos(rotationRadians);
        double sin = Math.Sin(rotationRadians);

        // Translate to origin
        double translatedX = worldPoint.X - centerX;
        double translatedY = worldPoint.Y - centerY;

        // Rotate
        double localX = translatedX * cos - translatedY * sin;
        double localY = translatedX * sin + translatedY * cos;

        // Check against unrotated bounds (relative to center)
        return localX >= -bounds.Width / 2.0 && localX <= bounds.Width / 2.0 &&
               localY >= -bounds.Height / 2.0 && localY <= bounds.Height / 2.0;
    }

    public override string ToString()
    {
        return Name;
    }
}




