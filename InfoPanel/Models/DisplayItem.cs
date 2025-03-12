using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Xml.Serialization;

namespace InfoPanel.Models;

[Serializable]
public abstract class DisplayItem : ObservableObject, ICloneable
{
    [XmlIgnore]
    public Guid Guid { get; set; } = Guid.NewGuid();

    [XmlIgnore]
    public Guid ProfileGuid { get; set; }

    private bool _selected;

    [XmlIgnore]
    public bool Selected
    {
        get { return _selected; }
        set
        {
            SetProperty(ref _selected, value);
        }
    }

    [XmlIgnore]
    public System.Windows.Point MouseOffset { get; set; }

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

    protected DisplayItem(string name)
    {
        _name = name;
    }

    protected DisplayItem(string name, Guid profileGuid)
    {
        _name = name;
        ProfileGuid = profileGuid;
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

    public abstract string EvaluateText();

    public abstract string EvaluateColor();

    public abstract (string, string) EvaluateTextAndColor();

    public abstract SizeF EvaluateSize();

    public abstract Rect EvaluateBounds();

    public abstract object Clone();
    
}




