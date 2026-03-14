using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml.Linq;

namespace InfoPanel
{
    public partial class SharedModel
    {
        /// <summary>
        /// Import from AIDA64 SensorPanel or RemoteSensor LCD format.
        /// </summary>
        public static async Task ImportSensorPanel(string importPath)
        {
            if (!File.Exists(importPath))
                return;

            var lines = File.ReadAllLines(importPath, Encoding.GetEncoding("iso-8859-1"));
            if (lines.Length < 2)
            {
                Serilog.Log.Warning("ImportSensorPanel: Invalid file format (too few lines)");
                return;
            }

            int page = 0;
            var items = new List<Dictionary<string, string>>();
            var importBaseName = Path.GetFileNameWithoutExtension(importPath);

            var openTagRegex = new Regex(@"<LCDPAGE(\d+)>", RegexOptions.Compiled);
            var closeTagRegex = new Regex(@"</LCDPAGE(\d+)>", RegexOptions.Compiled);

            for (var i = 0; i < lines.Length; i++)
            {
                var openMatch = openTagRegex.Match(lines[i]);
                if (openMatch.Success)
                {
                    page = int.Parse(openMatch.Groups[1].Value);
                    continue;
                }

                var closeMatch = closeTagRegex.Match(lines[i]);
                if (closeMatch.Success)
                {
                    await ProcessSensorPanelImport($"[Import] {importBaseName} - Page {page}", items);
                    items.Clear();
                    continue;
                }

                try
                {
                    var rootElement = XElement.Parse($"<Root>{EscapeContentWithinLBL(lines[i])}</Root>");
                    var item = new Dictionary<string, string>();
                    foreach (var element in rootElement.Elements())
                        item[element.Name.LocalName] = element.Value;
                    items.Add(item);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "ImportSensorPanel: Error parsing line {Line}", i);
                }
            }

            if (items.Count > 2)
                await ProcessSensorPanelImport($"[Import] {Path.GetFileNameWithoutExtension(importPath)}", items);
        }

        private static async Task ProcessSensorPanelImport(string name, List<Dictionary<string, string>> items)
        {
            if (items.Count <= 2) return;

            var SPWIDTH = items[1].GetIntValue("SPWIDTH", 1024);
            var SPHEIGHT = items[1].GetIntValue("SPHEIGHT", 600);
            var LCDBGCOLOR = items[1].GetIntValue("LCDBGCOLOR", 0);
            var SPBGCOLOR = items[1].GetIntValue("SPBGCOLOR", LCDBGCOLOR);

            var profile = new Profile(name, SPWIDTH, SPHEIGHT)
            {
                BackgroundColor = DecimalBgrToHex(SPBGCOLOR)
            };

            using var bitmap = new SKBitmap(1, 1);
            using var graphics = SkiaGraphics.FromBitmap(bitmap, profile.FontScale);

            var displayItems = new List<DisplayItem>();

            for (var i = 2; i < items.Count; i++)
            {
                var item = items[i];
                var key = item.GetStringValue("ID", string.Empty);

                var hidden = false;
                var simple = false;
                var gauge = false;
                var graph = false;

                if (key.StartsWith('-'))
                {
                    hidden = true;
                    key = key[1..];
                }
                if (key.StartsWith("[SIMPLE]"))
                {
                    simple = true;
                    key = key[8..];
                }
                if (key.StartsWith("[GAUGE]"))
                {
                    gauge = true;
                    key = key[7..];
                }
                if (key.StartsWith("[GRAPH]"))
                {
                    graph = true;
                    key = key[7..];
                }

                var ITMX = item.GetIntValue("ITMX", 0);
                var ITMY = item.GetIntValue("ITMY", 0);
                var LBL = item.GetStringValue("LBL", key);
                var TXTBIR = item.GetStringValue("TXTBIR", string.Empty);
                var FNTNAM = item.GetStringValue("FNTNAM", "Arial");
                var WID = item.GetIntValue("WID", 0);
                var HEI = item.GetIntValue("HEI", 0);
                var TYP = item.GetStringValue("TYP", string.Empty);
                var MINVAL = item.GetIntValue("MINVAL", 0);
                var MAXVAL = item.GetIntValue("MAXVAL", 100);
                var UNT = item.GetStringValue("UNT", string.Empty);
                var SHWUNT = item.GetIntValue("SHWUNT", 1);
                var TXTSIZ = item.GetIntValue("TXTSIZ", 12);
                var LBLCOL = item.GetIntValue("LBLCOL", 0);
                var TXTCOL = item.GetIntValue("TXTCOL", LBLCOL);
                var VALCOL = item.GetIntValue("VALCOL", TXTCOL);

                var bold = false;
                var italic = false;
                var rightAlign = !simple && key != "LBL";
                if (simple && TXTBIR.Length == 3)
                {
                    if (int.TryParse(TXTBIR.AsSpan(0, 1), out var _bold)) bold = _bold == 1;
                    if (int.TryParse(TXTBIR.AsSpan(1, 1), out var _italic)) italic = _italic == 1;
                    if (int.TryParse(TXTBIR.AsSpan(2, 1), out var _right)) rightAlign = _right == 1;
                }

                if (graph && WID != 0 && HEI != 0)
                {
                    GraphDisplayItem.GraphType? graphType = TYP switch
                    {
                        "AG" or "LG" => GraphDisplayItem.GraphType.LINE,
                        "HG" => GraphDisplayItem.GraphType.HISTOGRAM,
                        _ => null
                    };
                    if (graphType.HasValue)
                    {
                        var GPHCOL = item.GetIntValue("GPHCOL", 0);
                        var BGCOL = item.GetIntValue("BGCOL", 0);
                        var FRMCOL = item.GetIntValue("FRMCOL", 0);
                        var GPHBFG = item.GetStringValue("GPHBFG", "000");
                        var background = GPHBFG.Length >= 1 && int.TryParse(GPHBFG.AsSpan(0, 1), out var _bg) && _bg == 1;
                        var frame = GPHBFG.Length >= 2 && int.TryParse(GPHBFG.AsSpan(1, 1), out var _fr) && _fr == 1;
                        var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";

                        displayItems.Add(new GraphDisplayItem(LBL, profile, graphType.Value, libreSensorId)
                        {
                            SensorName = key,
                            Width = WID,
                            Height = HEI,
                            MinValue = MINVAL,
                            MaxValue = MAXVAL,
                            AutoValue = item.GetIntValue("AUTSCL", 0) == 1,
                            Step = item.GetIntValue("GPHSTP", 1),
                            Thickness = item.GetIntValue("GPHTCK", 1),
                            Background = background,
                            Frame = frame,
                            Fill = TYP != "LG",
                            FillColor = TYP == "AG" ? $"#7F{DecimalBgrToHex(GPHCOL)[1..]}" : DecimalBgrToHex(GPHCOL),
                            Color = DecimalBgrToHex(GPHCOL),
                            BackgroundColor = DecimalBgrToHex(BGCOL),
                            FrameColor = DecimalBgrToHex(FRMCOL),
                            X = ITMX,
                            Y = ITMY,
                            Hidden = hidden
                        });
                    }
                }
                else if (gauge)
                {
                    var STAFLS = item.GetStringValue("STAFLS", string.Empty);
                    var RESIZW = item.GetIntValue("RESIZW", 0);
                    var RESIZH = item.GetIntValue("RESIZH", 0);
                    if (TYP == "Custom" && !string.IsNullOrEmpty(STAFLS))
                    {
                        var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";
                        var gaugeItem = new GaugeDisplayItem(LBL, profile, libreSensorId)
                        {
                            SensorName = key,
                            MinValue = MINVAL,
                            MaxValue = MAXVAL,
                            X = ITMX,
                            Y = ITMY,
                            Width = RESIZW,
                            Height = RESIZH,
                            Hidden = hidden
                        };
                        foreach (var image in STAFLS.Split('|'))
                            gaugeItem.Images.Add(new ImageDisplayItem(image, profile, image, true));
                        displayItems.Add(gaugeItem);
                    }
                }
                else if (key == string.Empty)
                {
                    var GAUSTAFNM = item.GetStringValue("GAUSTAFNM", string.Empty);
                    var GAUSTADAT = item.GetStringValue("GAUSTADAT", string.Empty);
                    if (!string.IsNullOrEmpty(GAUSTAFNM) && !string.IsNullOrEmpty(GAUSTADAT))
                    {
                        var data = ConvertHexStringToByteArray(GAUSTADAT);
                        await FileUtil.SaveAsset(profile, GAUSTAFNM, data);
                    }
                }
                else if (key == "IMG")
                {
                    var IMGFIL = item.GetStringValue("IMGFIL", string.Empty);
                    var IMGDAT = item.GetStringValue("IMGDAT", string.Empty);
                    var BGIMG = item.GetIntValue("BGIMG", 0);
                    var RESIZW = item.GetIntValue("RESIZW", 0);
                    var RESIZH = item.GetIntValue("RESIZH", 0);
                    if (!string.IsNullOrEmpty(IMGFIL) && !string.IsNullOrEmpty(IMGDAT))
                    {
                        var data = ConvertHexStringToByteArray(IMGDAT);
                        if (await FileUtil.SaveAsset(profile, IMGFIL, data))
                        {
                            displayItems.Add(new ImageDisplayItem(IMGFIL, profile, IMGFIL, true)
                            {
                                X = ITMX,
                                Y = ITMY,
                                Width = BGIMG == 1 ? SPWIDTH : RESIZW,
                                Height = BGIMG == 1 ? SPHEIGHT : RESIZH,
                                Hidden = hidden
                            });
                        }
                    }
                }
                else
                {
                    switch (key)
                    {
                        case "PROPERTIES":
                            break;
                        case "LBL":
                            displayItems.Add(new TextDisplayItem(LBL, profile)
                            {
                                Font = FNTNAM,
                                FontSize = TXTSIZ,
                                Color = DecimalBgrToHex(VALCOL),
                                RightAlign = rightAlign,
                                X = ITMX,
                                Y = ITMY,
                                Width = WID,
                                Hidden = hidden
                            });
                            break;
                        case "SDATE":
                            displayItems.Add(new CalendarDisplayItem(LBL, profile)
                            {
                                Font = FNTNAM,
                                FontSize = TXTSIZ,
                                Color = DecimalBgrToHex(VALCOL),
                                Bold = bold,
                                Italic = italic,
                                RightAlign = rightAlign,
                                X = ITMX,
                                Y = ITMY,
                                Width = WID,
                                Hidden = hidden
                            });
                            break;
                        case "STIME":
                        case "STIMENS":
                            displayItems.Add(new ClockDisplayItem(LBL, profile)
                            {
                                Font = FNTNAM,
                                FontSize = TXTSIZ,
                                Format = key == "STIME" ? "H:mm:ss" : "H:mm",
                                Color = DecimalBgrToHex(VALCOL),
                                Bold = bold,
                                Italic = italic,
                                RightAlign = rightAlign,
                                X = ITMX,
                                Y = ITMY,
                                Width = WID,
                                Hidden = hidden
                            });
                            break;
                        default:
                            var SHWLBL = item.GetIntValue("SHWLBL", 0);
                            if (SHWLBL == 1)
                            {
                                var LBLBIS = item.GetStringValue("LBLBIS", string.Empty);
                                if (LBLBIS.Length >= 3)
                                {
                                    if (int.TryParse(LBLBIS.AsSpan(0, 1), out var _b)) bold = _b == 1;
                                    if (int.TryParse(LBLBIS.AsSpan(1, 1), out var _i)) italic = _i == 1;
                                }
                                displayItems.Add(new TextDisplayItem(LBL, profile)
                                {
                                    Font = FNTNAM,
                                    FontSize = TXTSIZ,
                                    Color = DecimalBgrToHex(LBLCOL),
                                    Bold = bold,
                                    Italic = italic,
                                    X = ITMX,
                                    Y = ITMY,
                                    Width = WID,
                                    Hidden = hidden
                                });
                            }
                            var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";
                            var SHWVAL = item.GetIntValue("SHWVAL", 0);
                            if (simple || SHWVAL == 1)
                            {
                                var VALBIS = item.GetStringValue("VALBIS", string.Empty);
                                if (VALBIS.Length >= 3)
                                {
                                    if (int.TryParse(VALBIS.AsSpan(0, 1), out var _b)) bold = _b == 1;
                                    if (int.TryParse(VALBIS.AsSpan(1, 1), out var _i)) italic = _i == 1;
                                }
                                displayItems.Add(new SensorDisplayItem(LBL, profile, libreSensorId)
                                {
                                    SensorName = key,
                                    Font = FNTNAM,
                                    FontSize = TXTSIZ,
                                    Color = DecimalBgrToHex(VALCOL),
                                    Unit = UNT,
                                    ShowUnit = SHWUNT == 1,
                                    OverrideUnit = SHWUNT == 1,
                                    Bold = bold,
                                    Italic = italic,
                                    RightAlign = rightAlign,
                                    X = ITMX,
                                    Y = ITMY,
                                    Width = WID,
                                    Hidden = hidden
                                });
                            }
                            var SHWBAR = item.GetIntValue("SHWBAR", 0);
                            if (SHWBAR == 1)
                            {
                                var BARWID = item.GetIntValue("BARWID", 400);
                                var BARHEI = item.GetIntValue("BARHEI", 50);
                                var BARMIN = item.GetIntValue("BARMIN", 0);
                                var BARMAX = item.GetIntValue("BARMAX", 100);
                                var BARFRMCOL = item.GetIntValue("BARFRMCOL", 0);
                                var BARLIM3FGC = item.GetIntValue("BARLIM3FGC", 0);
                                var BARLIM3BGC = item.GetIntValue("BARLIM3BGC", 0);
                                var BARFS = item.GetStringValue("BARFS", "0000");
                                var BARPLC = item.GetStringValue("BARPLC", "SEP");
                                var offset = 0;
                                if (BARPLC == "SEP" && SHWVAL == 1)
                                {
                                    var size2 = graphics.MeasureString("HELLO WORLD", FNTNAM, "", TXTSIZ);
                                    offset = (int)size2.height;
                                }
                                var frame = BARFS.Length >= 1 && int.TryParse(BARFS.AsSpan(0, 1), out var _f) && _f == 1;
                                var gradient = BARFS.Length >= 3 && int.TryParse(BARFS.AsSpan(2, 1), out var _g) && _g == 1;
                                var flipX = BARFS.Length >= 4 && int.TryParse(BARFS.AsSpan(3, 1), out var _x) && _x == 1;

                                displayItems.Add(new BarDisplayItem(LBL, profile, libreSensorId)
                                {
                                    SensorName = key,
                                    Width = BARWID,
                                    Height = BARHEI,
                                    MinValue = BARMIN,
                                    MaxValue = BARMAX,
                                    Frame = frame,
                                    FrameColor = DecimalBgrToHex(BARFRMCOL),
                                    Color = DecimalBgrToHex(BARLIM3FGC),
                                    Background = true,
                                    BackgroundColor = DecimalBgrToHex(BARLIM3BGC),
                                    Gradient = gradient,
                                    GradientColor = DecimalBgrToHex(BARLIM3BGC),
                                    FlipX = flipX,
                                    X = ITMX,
                                    Y = ITMY + offset,
                                    Hidden = hidden
                                });
                            }
                            break;
                    }
                }
            }

            SaveDisplayItems(profile, displayItems);

            Dispatcher.CurrentDispatcher.Invoke(() =>
            {
                ConfigModel.Instance.AddProfile(profile);
                ConfigModel.Instance.SaveProfiles();
                Instance.SelectedProfile = profile;
            });
        }

        private static byte[] ConvertHexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even length.");
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < hex.Length; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        private static string EscapeContentWithinLBL(string xmlContent)
        {
            var pattern = @"<LBL>(.*?)</LBL>";
            return Regex.Replace(xmlContent, pattern, match =>
            {
                var innerContent = match.Groups[1].Value;
                var escapedContent = innerContent.Replace("<", "&lt;").Replace(">", "&gt;");
                return $"<LBL>{escapedContent}</LBL>";
            }, RegexOptions.Singleline);
        }

        private static string DecimalBgrToHex(int bgrValue)
        {
            if (bgrValue < 0)
                return "#000000";
            var blue = (bgrValue & 0xFF0000) >> 16;
            var green = (bgrValue & 0x00FF00) >> 8;
            var red = bgrValue & 0x0000FF;
            return $"#{red:X2}{green:X2}{blue:X2}";
        }
    }
}
