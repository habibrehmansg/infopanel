using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Sensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Data;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Renders a profile and measures where its items are. The only entry point to Core rendering:
    /// everything a frame depends on comes from the constructor arguments, the profile, the items and
    /// the <see cref="ProfileRenderState"/>, so the same inputs give the same pixels.
    /// </summary>
    public sealed class ProfileRenderer(ISensorReader sensors, ISensorHistory history, IImageSource images, IAssetLocator assets, TimeProvider time)
    {
        /// <summary>
        /// Draws <paramref name="items"/> (the profile's items, in draw order) onto <paramref name="canvas"/>,
        /// scaled by <paramref name="scale"/> (1 = profile pixels). The canvas is first cleared to the profile's
        /// background colour, whole and ignoring its matrix, when that colour parses, as the 1.x panel output did;
        /// otherwise it is left as it is. Does not advance <see cref="ProfileRenderState.Tick"/>.
        /// </summary>
        public void Draw(SKCanvas canvas, Profile profile, IReadOnlyList<DisplayItem> items, ProfileRenderState state, RenderOptions options, float scale)
        {
            var ctx = CreateContext(state, options);

            canvas.Save();
            try
            {
                canvas.Scale(scale);

                // Not disposed: disposing it would dispose the caller's canvas.
                var g = new SkiaGraphics(canvas, profile.FontScale);
                PanelDraw.Run(profile, items, g, ctx);
            }
            finally
            {
                canvas.Restore();
            }
        }

        /// <summary>
        /// Where each visible item is drawn, in profile coordinates and draw order (a group comes before
        /// its children). Hidden items and everything inside a hidden group are left out. A sensor image
        /// outside its thresholds is included at the size it shows at, so the editor can still select it.
        /// </summary>
        public IReadOnlyList<ItemBounds> MeasureBounds(Profile profile, IReadOnlyList<DisplayItem> items, ProfileRenderState state)
        {
            var ctx = CreateContext(state, RenderOptions.Default);
            var g = SkiaGraphics.FromEmpty(profile.FontScale);
            var result = new List<ItemBounds>();

            foreach (var item in items)
            {
                if (!item.Hidden)
                {
                    MeasureSafely(g, ctx, item, result);
                }
            }

            return result;
        }

        private RenderContext CreateContext(ProfileRenderState state, RenderOptions options)
        {
            var tick = state.Tick;
            var (now, elapsed) = state.GetFrameTime(tick, time);
            var inputs = state.GetTickInputs(tick, sensors, history);

            return new RenderContext
            {
                Sensors = inputs,
                History = inputs,
                Images = images,
                Assets = assets,
                State = state.Items,
                Tick = tick,
                Now = now,
                Elapsed = elapsed,
                Options = options,
                Fps = state.Fps,
            };
        }

        /// <summary>
        /// <see cref="Measure"/>, but an item that throws is left out (with what it added), logged once, and the
        /// others are still measured, as <see cref="PanelDraw"/> skips an item that fails to draw.
        /// </summary>
        private static SKRect? MeasureSafely(SkiaGraphics g, RenderContext ctx, DisplayItem item, List<ItemBounds> result)
        {
            var count = result.Count;
            try
            {
                var extent = Measure(g, ctx, item, result);
                ctx.State.ClearFailure(item.Guid, "measure");
                return extent;
            }
            catch (Exception ex)
            {
                result.RemoveRange(count, result.Count - count);
                PanelDraw.LogFailure(ctx, item, ex, "measure");
                return null;
            }
        }

        /// <summary>
        /// Adds the bounds of <paramref name="item"/> (and, for a group, its visible children) to
        /// <paramref name="result"/>, and returns its axis-aligned extent, or null if it has none.
        /// Each case mirrors what <see cref="PanelDraw"/> draws for that kind.
        /// </summary>
        private static SKRect? Measure(SkiaGraphics g, RenderContext ctx, DisplayItem item, List<ItemBounds> result)
        {
            switch (item)
            {
                case GroupDisplayItem group:
                    {
                        var index = result.Count;
                        result.Add(default);

                        SKRect? union = null;
                        foreach (var child in group.DisplayItemsCopy)
                        {
                            if (child.Hidden || MeasureSafely(g, ctx, child, result) is not SKRect extent)
                            {
                                continue;
                            }

                            union = union is SKRect u ? SKRect.Union(u, extent) : extent;
                        }

                        if (union is not SKRect rect)
                        {
                            // No visible children: nothing is drawn, so there is nothing to select.
                            result.RemoveAt(index);
                            return null;
                        }

                        result[index] = new ItemBounds(group.Guid, rect.Left, rect.Top, rect.Width, rect.Height, 0, IsGroup: true);
                        return rect;
                    }
                case TableSensorDisplayItem table when table.GetValue(ctx.Sensors) is SensorReading { ValueTable: DataTable dataTable }:
                    return Add(result, item.Guid, MeasureTable(g, table, dataTable), 0, default);
                case TextDisplayItem text:
                    {
                        var (rect, rotation, pivot) = MeasureText(g, ctx, text);
                        return Add(result, item.Guid, rect, rotation, pivot);
                    }
                case ImageDisplayItem image:
                    {
                        var source = image.ResolveSource(ctx);
                        var cachedImage = string.IsNullOrEmpty(source) ? null : ctx.Images.Get(source, image.PersistentCache);

                        var size = image.EvaluateSize(ctx);
                        if (cachedImage == null)
                        {
                            size = ImagePlaceholder.Resolve(size);
                        }

                        return AddImageRect(result, item, (int)size.Width, (int)size.Height, vector: cachedImage?.IsVector == true);
                    }
                case GaugeDisplayItem gauge:
                    {
                        gauge.EvaluateImageFrame(ctx, out var imageA, out _, out _);

                        var source = imageA?.ResolveSource(ctx);
                        var cachedA = string.IsNullOrEmpty(source) ? null : ctx.Images.Get(source, imageA!.PersistentCache);

                        if (cachedA != null)
                        {
                            var width = gauge.Width == 0 ? cachedA.Width : gauge.Width;
                            var height = gauge.Height == 0 ? cachedA.Height : gauge.Height;

                            return AddImageRect(result, item,
                                (int)Math.Floor(width * gauge.Scale / 100.0f),
                                (int)Math.Floor(height * gauge.Scale / 100.0f),
                                vector: cachedA.IsVector, flipX: gauge.FlipX);
                        }

                        var size = ImagePlaceholder.Resolve(new SKSize(gauge.Width * gauge.Scale / 100.0f, gauge.Height * gauge.Scale / 100.0f));
                        return AddImageRect(result, item, (int)Math.Floor(size.Width), (int)Math.Floor(size.Height), flipX: gauge.FlipX);
                    }
                case ChartDisplayItem chart:
                    // Drawn as a Width × Height bitmap rotated about its centre; a donut's Rotation turns the whole bitmap.
                    return AddImageRect(result, item, chart.Width, chart.Height, flipX: chart.FlipX);
                case ShapeDisplayItem shape:
                    return AddImageRect(result, item, shape.Width, shape.Height);
                default:
                    return null;
            }
        }

        /// <summary>
        /// An item drawn as a <paramref name="width"/> × <paramref name="height"/> box at its X/Y, as
        /// <see cref="SkiaGraphics.DrawImage(SKImage, int, int, int, int, int, int, int, bool, bool, float)"/> or, for an SVG
        /// (<paramref name="vector"/>), <see cref="Extensions.CanvasExtensions.DrawPicture"/> draws it:
        /// <list type="bullet">
        /// <item>A raster rotates about the integer centre (x + width / 2, y + height / 2); an SVG about
        /// (int)(x + width / 2f), (int)(y + width / 2f), a 1.x quirk that uses Width for both.</item>
        /// <item><paramref name="flipX"/> mirrors after rotating: a raster about the vertical line through its integer
        /// centre (which moves an odd-width box 1 px left), an SVG about its true centre. A mirrored rotation turns the
        /// other way about the mirrored pivot, so the drawn box is the mirrored box rotated by -Rotation.</item>
        /// </list>
        /// </summary>
        private static SKRect AddImageRect(List<ItemBounds> result, DisplayItem item, int width, int height, bool vector = false, bool flipX = false)
        {
            float x = item.X;
            float rotation = item.Rotation;

            var pivot = vector
                ? new SKPoint((int)(item.X + width / 2f), (int)(item.Y + width / 2f))
                : new SKPoint(item.X + width / 2, item.Y + height / 2);

            if (flipX)
            {
                // Mirror line: the true centre for an SVG, the integer centre for a raster.
                var mirrorX = vector ? item.X + width / 2f : item.X + width / 2;
                x = 2 * mirrorX - item.X - width;
                pivot.X = 2 * mirrorX - pivot.X;
                rotation = rotation == 0 ? 0 : -rotation;
            }

            return Add(result, item.Guid, SKRect.Create(x, item.Y, width, height), rotation, pivot);
        }

        /// <summary>
        /// Text as <see cref="PanelDraw"/> draws it: the measured block (Width wide when Width is set; to the left
        /// of X when right-aligned with no Width), grown by the glow's reach; a marquee that scrolls is its clip box,
        /// unrotated. Returns the rotation pivot <see cref="SkiaGraphics.DrawString"/> uses, which is not always
        /// the block's centre.
        /// </summary>
        private static (SKRect Rect, float Rotation, SKPoint Pivot) MeasureText(SkiaGraphics g, RenderContext ctx, TextDisplayItem text)
        {
            var value = text.EvaluateText(ctx);

            if (text.Marquee && text.Width > 0)
            {
                var (textWidth, textHeight) = g.MeasureString(value, text.Font, text.FontStyle, text.FontSize,
                    text.Bold, text.Italic, text.Underline, text.Strikeout, false, false, 0, 0);

                if (textWidth > text.Width)
                {
                    var clipHeight = text.Height > 0 ? text.Height : (int)textHeight;
                    return (SKRect.Create(text.X, text.Y, text.Width, clipHeight), 0, default);
                }
            }

            var (width, height) = g.MeasureString(value, text.Font, text.FontStyle, text.FontSize,
                text.Bold, text.Italic, text.Underline, text.Strikeout, text.Wrap, text.Ellipsis, text.Width, text.Height);

            var left = text.Width == 0 && text.RightAlign ? text.X - width : text.X;
            var rect = SKRect.Create(left, text.Y, width, height);

            if (text.GlowEnabled)
            {
                var glow = SkiaGraphics.GlowExtent(text.GlowRadius);
                rect.Inflate(glow, glow);
            }

            if (string.IsNullOrEmpty(value) || text.Rotation == 0)
            {
                return (rect, 0, default);
            }

            var pivot = text.Width > 0 && text.Height > 0
                ? new SKPoint(text.X + text.Width / 2, text.Y + text.Height / 2)
                : new SKPoint(
                    text.X + (text.Width > 0 ? text.Width / 2 : (int)(width / 2)),
                    text.Y + (text.Height > 0 ? text.Height / 2 : (int)(height / 2)));

            return (rect, text.Rotation, pivot);
        }

        /// <summary>
        /// The table's grid, its columns (each its format length + 10 wide) by MaxRows rows (+ header) of one line,
        /// as in 1.x, grown to every cell <see cref="PanelDraw.LayoutTable"/> lays out as drawn: a cell taller than a
        /// line (embedded newlines, or wrapping within its width) and the glow's reach.
        /// </summary>
        private static SKRect MeasureTable(SkiaGraphics g, TableSensorDisplayItem table, DataTable dataTable)
        {
            var (_, lineHeight) = g.MeasureString("A", table.Font, table.FontStyle, table.FontSize,
                table.Bold, table.Italic, table.Underline, table.Strikeout);

            var width = 0;
            foreach (var part in table.TableFormat.Split('|'))
            {
                var split = part.Split(':');
                if (split.Length == 2
                    && int.TryParse(split[0], out var column) && column >= 0 && column < dataTable.Columns.Count
                    && int.TryParse(split[1], out var length))
                {
                    width += length + 10;
                }
            }

            var bounds = SKRect.Create(table.X, table.Y, width, lineHeight * (table.MaxRows + (table.ShowHeader ? 1 : 0)));
            var glow = table.GlowEnabled ? SkiaGraphics.GlowExtent(table.GlowRadius) : 0;

            foreach (var cell in PanelDraw.LayoutTable(g, table, table.X, table.Y, table.FontSize, dataTable))
            {
                if (string.IsNullOrEmpty(cell.Text))
                {
                    continue;   // DrawString draws nothing
                }

                var (_, cellHeight) = g.MeasureString(cell.Text, table.Font, table.FontStyle, table.FontSize,
                    table.Bold, table.Italic, table.Underline, table.Strikeout, table.Wrap, table.Ellipsis, cell.Width, 0);

                var rect = SKRect.Create(cell.X, cell.Y, cell.Width, cellHeight);
                rect.Inflate(glow, glow);
                bounds = SKRect.Union(bounds, rect);
            }

            return bounds;
        }

        /// <summary>
        /// Adds <paramref name="rect"/> rotated by <paramref name="rotation"/> degrees about <paramref name="pivot"/>,
        /// expressed as the same rectangle rotated about its own centre (moved where the pivot rotation puts that centre),
        /// and returns the axis-aligned extent of the rotated rectangle.
        /// </summary>
        private static SKRect Add(List<ItemBounds> result, Guid id, SKRect rect, float rotation, SKPoint pivot)
        {
            var x = rect.Left;
            var y = rect.Top;
            var width = rect.Width;
            var height = rect.Height;

            if (rotation != 0)
            {
                // Where rotating about the pivot moves the centre (zero when the pivot is the centre).
                var dx = (double)x + width / 2.0 - pivot.X;
                var dy = (double)y + height / 2.0 - pivot.Y;
                var radians = rotation * Math.PI / 180;
                var (sin, cos) = Math.SinCos(radians);
                x += (float)(dx * cos - dy * sin - dx);
                y += (float)(dx * sin + dy * cos - dy);
            }

            result.Add(new ItemBounds(id, x, y, width, height, rotation, IsGroup: false));
            rect = SKRect.Create(x, y, width, height);

            if (rotation == 0)
            {
                return rect;
            }

            var matrix = SKMatrix.CreateRotationDegrees(rotation, rect.MidX, rect.MidY);
            return matrix.MapRect(rect);
        }
    }
}
