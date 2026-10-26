using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Sensors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Editing;

/// <summary>
/// Example screens for a data folder with no profiles: a port of the web client's <c>src/model/seed.ts</c> with the
/// C# kinds and property names, bound to the mock sensors (<c>plugin:/infopanel.mock/...</c>) and set in Inter.
/// </summary>
public static class ExampleProfiles
{
    private const string Amber = "#FFFFB000";
    private const string Muted = "#FF8A8A8A";
    private const string Card = "#FF141414";
    private const string CardEdge = "#FF262626";
    private const string Track = "#FF2A2A2A";
    private const string Plot = "#FF1B1B1B";

    private enum Align { Left, Center, Right }

    /// <summary>The 1920×480 "Wide bar": GPU, clock, game and system cards, as the web client's seed lays them out.</summary>
    public static (Profile Profile, List<DisplayItem> Items) WideBar()
    {
        var p = new Profile("Wide bar", 1920, 480)
        {
            BackgroundColor = "#FF000000",
            Font = FontDefaults.Family,
            FontSize = Pt(26, 1.33f),
            Color = "#FFFFFFFF",
        };
        int pt(double px) => Pt(px, p.FontScale);

        TextDisplayItem Text(string name, int x, int y, int? fontSize = null, string? color = null, bool bold = false, int width = 0, Align align = Align.Left)
        {
            var item = new TextDisplayItem(name, p) { X = x, Y = y, Font = p.Font, FontSize = fontSize ?? p.FontSize, Color = color ?? p.Color, Bold = bold };
            Layout(item, width, align);
            return item;
        }

        TextDisplayItem Label(string value, int x, int y, int? fontSize = null, int width = 0, Align align = Align.Left) =>
            Text(value, x, y, fontSize ?? pt(13), Muted, bold: true, width, align);

        SensorDisplayItem Sensor(string mockId, int x, int y, int fontSize, bool bold = false, int width = 0, Align align = Align.Left,
            bool showUnit = true, int? precision = null, bool thousands = false, bool showName = false)
        {
            var item = new SensorDisplayItem(Def(mockId).Name, p)
            {
                X = x, Y = y, Font = p.Font, FontSize = fontSize, Color = p.Color, Bold = bold,
                ShowUnit = showUnit, ShowThousandsSeparator = thousands, ShowName = showName,
            };
            if (precision is int digits)
            {
                item.OverridePrecision = true;
                item.Precision = digits;
            }

            Layout(item, width, align);
            Bind(item, mockId);
            return item;
        }

        ShapeDisplayItem CardShape(string name, int x) => new(name, p)
        {
            X = x, Y = 24, Width = x == 648 ? 624 : 600, Height = 432, CornerRadius = 16, ShowGradient = false,
            FillColor = Card, FrameThickness = 2, FrameColor = CardEdge,
        };

        BarDisplayItem Bar(string name, string mockId, int x, int y, int width) => Bind(new BarDisplayItem(name, p)
        {
            X = x, Y = y, Width = width, Height = 14, CornerRadius = 7, Color = Amber, GradientColor = "#FF7A5400",
            BackgroundColor = Track, Frame = false,
        }, mockId);

        GraphDisplayItem Histogram(string name, string mockId, int x, string color) => Bind(
            new GraphDisplayItem(name, p, GraphDisplayItem.GraphType.HISTOGRAM)
            {
                X = x, Y = 340, Width = 268, Height = 92, AutoValue = true, Color = color, BackgroundColor = Plot, Frame = false,
            }, mockId);

        static GroupDisplayItem Group(string name, IEnumerable<DisplayItem> children, bool isExpanded = true, bool hidden = false)
        {
            var group = new GroupDisplayItem { Name = name, IsExpanded = isExpanded, Hidden = hidden };
            foreach (var child in children)
                group.DisplayItems.Add(child);
            return group;
        }

        (string Id, string Caption, int Max)[] donuts =
        [
            ("gpu.load", "LOAD %", 100),
            ("gpu.temp", "TEMP °C", 100),
            ("gpu.power", "POWER W", 450),
            ("gpu.fan1", "FAN RPM", 3000),
        ];
        var gpu = Group("GPU",
        [
            CardShape("GPU card", 24),
            Label("GPU · RTX 4090", 48, 44, pt(20)),
            .. donuts.SelectMany((d, i) =>
            {
                var x = 48 + i * 140;
                return new DisplayItem[]
                {
                    Bind(new DonutDisplayItem($"{d.Caption.Split(' ')[0]} ring", p)
                    {
                        X = x, Y = 92, Width = 120, Height = 120, Thickness = 12, Span = 270, Rotation = 225, MaxValue = d.Max,
                        Color = Amber, BackgroundColor = Track,
                    }, d.Id),
                    Sensor(d.Id, x, 134, pt(32), bold: true, width: 120, align: Align.Center, showUnit: false, precision: 0),
                    Label(d.Caption, x, 190, width: 120, align: Align.Center),
                };
            }),
            Bind(new GraphDisplayItem("GPU load history", p, GraphDisplayItem.GraphType.LINE)
            {
                X = 48, Y = 250, Width = 552, Height = 120, Color = Amber, FillColor = "#33FFB000", BackgroundColor = Plot, Frame = false,
            }, "gpu.load"),
            Label("GPU LOAD", 48, 382),
            Label("VRAM", 48, 412),
            Bar("VRAM bar", "gpu.memload", 110, 412, 490),
        ]);

        var clockText = new ClockDisplayItem("Clock", p)
        {
            X = 648, Y = 84, Font = p.Font, FontSize = pt(120), Bold = true, Color = Amber, Format = "HH:mm:ss",
        };
        Layout(clockText, 624, Align.Center);
        var date = new CalendarDisplayItem("Date", p)
        {
            X = 648, Y = 236, Font = p.Font, FontSize = pt(28), Color = "#FFCFCFCF", Format = "dddd, d MMMM yyyy",
        };
        Layout(date, 624, Align.Center);
        var clock = Group("Clock", [CardShape("Clock card", 648), clockText, date]);

        (string Id, string Caption)[] stats = [("fps", "FPS"), ("fps.low1", "1% LOW"), ("frametime", "FRAME TIME ms")];
        var game = Group("Game",
            stats.SelectMany((s, i) =>
            {
                var x = 672 + i * 192;
                return new DisplayItem[]
                {
                    Sensor(s.Id, x, 304, pt(48), bold: true, width: 192, align: Align.Center, showUnit: false),
                    Label(s.Caption, x, 368, width: 192, align: Align.Center),
                };
            }),
            isExpanded: false);

        var cpuTemp = Sensor("cpu.temp", 1320, 72, pt(72), bold: true, precision: 0);
        cpuTemp.Threshold1 = 80;
        cpuTemp.Threshold1Color = Amber;
        cpuTemp.Threshold2 = 90;
        cpuTemp.Threshold2Color = "#FFEF4444";

        (string Id, string Caption)[] rows = [("cpu.load", "CPU LOAD"), ("mem.load", "RAM"), ("drive.activity", "DISK")];
        var system = Group("System",
        [
            CardShape("System card", 1296),
            Label("SYSTEM", 1320, 44, pt(20)),
            cpuTemp,
            Label("CPU TEMP", 1320, 160),
            Sensor("cpu.power", 1600, 72, pt(72), bold: true, precision: 0),
            Label("CPU POWER", 1600, 160),
            .. rows.SelectMany((r, i) =>
            {
                var y = 200 + i * 38;
                return new DisplayItem[]
                {
                    Label(r.Caption, 1320, y),
                    Bar($"{r.Caption} bar", r.Id, 1430, y + 1, 330),
                    Sensor(r.Id, 1776, y - 2, pt(16), width: 96, align: Align.Right, precision: 0),
                };
            }),
            Label("DOWN", 1320, 320),
            Sensor("net.down", 1320, 318, pt(14), width: 268, align: Align.Right, precision: 0, thousands: true),
            Histogram("Download", "net.down", 1320, "#FF4C8DFF"),
            Label("UP", 1604, 320),
            Sensor("net.up", 1604, 318, pt(14), width: 268, align: Align.Right, precision: 0, thousands: true),
            Histogram("Upload", "net.up", 1604, "#FF22C55E"),
        ], isExpanded: false);

        var fans = Group("Fans",
            new[] { "mb.cpufan", "mb.pump", "mb.fan2" }.Select((id, i) => (DisplayItem)Sensor(id, 1320 + i * 190, 440, pt(14), showName: true)),
            isExpanded: false, hidden: true);

        List<DisplayItem> items = [gpu, clock, game, system, fans];
        foreach (var item in items)
            item.SetProfile(p);
        return (p, items);
    }

    /// <summary>The web client's <c>pt</c>: the seed was laid out in pixels and InfoPanel scales font sizes by the font scale (JS rounding).</summary>
    private static int Pt(double px, float fontScale) => (int)Math.Floor(px / fontScale + 0.5);

    private static MockSensorDef Def(string mockId) =>
        MockSensorCatalog.ById(mockId) ?? throw new ArgumentException($"No mock sensor '{mockId}'.", nameof(mockId));

    /// <summary>Binds the item to a mock sensor with <see cref="SensorKeys.ToBindingProps"/>, as the API does.</summary>
    private static T Bind<T>(T item, string mockId) where T : DisplayItem
    {
        var def = Def(mockId);
        foreach (var (name, value) in SensorKeys.ToBindingProps(def.Key, def.Name))
            item.GetType().GetProperty(name)!.SetValue(item, value);
        return item;
    }

    private static void Layout(TextDisplayItem item, int width, Align align)
    {
        item.Width = width;
        item.CenterAlign = align == Align.Center;
        item.RightAlign = align == Align.Right;
    }
}
