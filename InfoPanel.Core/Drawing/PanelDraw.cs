using InfoPanel.Extensions;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Plugins;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;

namespace InfoPanel.Drawing
{
    internal class PanelDraw
    {
        /// <summary>
        /// Draws <paramref name="displayItems"/> (the profile's items, in draw order) onto <paramref name="g"/>.
        /// Output depends only on the arguments: the same profile, items and context give the same pixels.
        /// </summary>
        public static void Run(Profile profile, IReadOnlyList<DisplayItem> displayItems, SkiaGraphics g, RenderContext ctx, float scale = 1)
        {
            var stopwatch = Stopwatch.StartNew();

            if (SKColor.TryParse(profile.BackgroundColor, out var backgroundColor))
            {
                g.Clear(backgroundColor);
            }

            foreach (var displayItem in displayItems)
            {
                if (displayItem.Hidden) continue;
                Draw(g, ctx, scale, displayItem);
            }

            var fpsCounter = ctx.Fps;
            fpsCounter?.Update(stopwatch.ElapsedMilliseconds);

            if (profile.ShowFps && fpsCounter != null)
            {
                var renderingEngine = "CPU";

                if (g is SkiaGraphics skiaGraphics && skiaGraphics.OpenGL)
                {
                    renderingEngine = "OpenGL";
                }

                var text = $"{renderingEngine} | FPS {fpsCounter.FramesPerSecond} | {fpsCounter.FrameTime}ms";
                var font = "Consolas";
                var fontStyle = "Regular";
                var fontSize = 10;

                var rect = new SKRect(0, 0, profile.Width, 15);

                g.FillRectangle("#84000000", (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
                g.DrawString(profile.Name, font, fontStyle, fontSize, "#FF00FF00", (int)rect.Left + 5, (int)rect.Top, width: (int)rect.Width, height: (int)rect.Height);
                g.DrawString(text, font, fontStyle, fontSize, "#FF00FF00", (int)rect.Left, (int)rect.Top, width: (int)rect.Width - 5, height: (int)rect.Height, rightAlign: true);
            }
        }

        private static void Draw(SkiaGraphics g, RenderContext ctx, float scale, DisplayItem displayItem)
        {
            var x = (int)Math.Floor(displayItem.X * scale);
            var y = (int)Math.Floor(displayItem.Y * scale);

            switch (displayItem)
            {
                case GroupDisplayItem groupDisplayItem:
                    {
                        foreach (var item in groupDisplayItem.DisplayItemsCopy)
                        {
                            if (item.Hidden) continue;
                            Draw(g, ctx, scale, item);
                        }
                        break;
                    }
                case TextDisplayItem textDisplayItem:
                    {
                        (var text, var color) = textDisplayItem.EvaluateTextAndColor(ctx);

                        // Handle table display items separately
                        if (textDisplayItem is TableSensorDisplayItem tableSensorDisplayItem
                            && tableSensorDisplayItem.GetValue(ctx.Sensors) is SensorReading sensorReading
                            && sensorReading.ValueTable is DataTable table)
                        {
                            DrawTableSensorDisplay(g, tableSensorDisplayItem, x, y, scale, color, table);
                            break;
                        }

                        // Regular text display
                        if (textDisplayItem.Marquee && textDisplayItem.Width > 0)
                        {
                            DrawMarqueeText(g, ctx, textDisplayItem, text, color, x, y, scale);
                        }
                        else
                        {
                            DrawNormalText(g, textDisplayItem, text, color, x, y, scale);
                        }

                        break;
                    }
                case ImageDisplayItem imageDisplayItem:
                    {
                        if (imageDisplayItem is SensorImageDisplayItem sensorImageDisplayItem)
                        {
                            if (!sensorImageDisplayItem.ShouldShow(ctx.Sensors))
                            {
                                break;
                            }
                        }

                        var source = imageDisplayItem.ResolveSource(ctx);
                        var cachedImage = string.IsNullOrEmpty(source) ? null : ctx.Images.Get(source, imageDisplayItem.PersistentCache);

                        var size = imageDisplayItem.EvaluateSize(ctx);

                        if (cachedImage == null)
                        {
                            size = ImagePlaceholder.Resolve(size);
                        }

                        var scaledWidth = (int)size.Width;
                        var scaledHeight = (int)size.Height;

                        scaledWidth = (int)Math.Floor(scaledWidth * scale);
                        scaledHeight = (int)Math.Floor(scaledHeight * scale);

                        if (scaledWidth <= 0 || scaledHeight <= 0)
                        {
                            return;
                        }

                        if (cachedImage != null)
                        {
                            g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, ctx.Elapsed, imageDisplayItem.Rotation);

                            if (imageDisplayItem.Layer)
                            {
                                g.FillRectangle(imageDisplayItem.LayerColor, x, y, scaledWidth, scaledHeight, rotation: imageDisplayItem.Rotation);
                            }

                            if (imageDisplayItem.ShowPanel && cachedImage is IMediaPlayback media)
                            {
                                if (g is SkiaGraphics skiaGraphics)
                                {
                                    var canvas = skiaGraphics.Canvas;

                                    // Calculate progress
                                    var progress = media.Duration.TotalMilliseconds > 0
                                        ? media.CurrentTime.TotalMilliseconds / media.Duration.TotalMilliseconds
                                        : 0;

                                    // Rotation is already -180 to 180
                                    var rotation = imageDisplayItem.Rotation;

                                    // Round to nearest 90° to find which edge is at bottom
                                    var nearestQuadrant = (int)Math.Round(rotation / 90f) * 90;

                                    // Determine overlay width based on which edge is at bottom
                                    var overlayWidth = (nearestQuadrant == 90 || nearestQuadrant == -90) ? scaledHeight : scaledWidth;

                                    // Create overlay bitmap
                                    using var overlayBitmap = CreateVideoOverlay(
                                        overlayWidth,
                                        progress,
                                        media.CurrentTime,
                                        media.Duration,
                                        media.FrameRate,
                                        media.Volume,
                                        media.Status,
                                        ctx.Elapsed
                                    );

                                    // Draw overlay with rotation
                                    canvas.Save();
                                    try
                                    {
                                        var imageCenterX = x + scaledWidth / 2f;
                                        var imageCenterY = y + scaledHeight / 2f;

                                        // Apply image rotation
                                        canvas.Translate(imageCenterX, imageCenterY);
                                        canvas.RotateDegrees(rotation);

                                        // Position overlay at bottom based on quadrant
                                        var (overlayX, overlayY) = nearestQuadrant switch
                                        {
                                            0 => (-scaledWidth / 2f, scaledHeight / 2f - 40),          // Original bottom
                                            90 => (-scaledHeight / 2f, scaledWidth / 2f - 40),         // Left edge is bottom
                                            180 or -180 => (-scaledWidth / 2f, -scaledHeight / 2f),    // Top edge is bottom  
                                            -90 => (-scaledHeight / 2f, scaledWidth / 2f - 40),        // Right edge is bottom (same Y as 90!)
                                            _ => (-scaledWidth / 2f, scaledHeight / 2f - 40)
                                        };

                                        // Apply additional rotation to make overlay horizontal
                                        canvas.RotateDegrees(-nearestQuadrant);

                                        // Draw the overlay bitmap
                                        using var overlayPaint = new SKPaint { IsAntialias = true };
                                        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest);

                                        using var image = SKImage.FromBitmap(overlayBitmap);

                                        canvas.DrawImage(image, SKRect.Create(overlayX, overlayY, image.Width, image.Height), sampling, overlayPaint);

                                    }
                                    finally
                                    {
                                        canvas.Restore();
                                    }
                                }
                            }

                        }
                        else
                        {
                            using var image = SKBitmapExtensions.CreateMaterialNoImageBitmap(scaledWidth, scaledHeight);

                            if (image != null)
                            {
                                g.DrawBitmap(image, x, y, scaledWidth, scaledHeight, imageDisplayItem.Rotation);
                            }
                        }
                        break;
                    }
                case GaugeDisplayItem gaugeDisplayItem:
                    {
                        gaugeDisplayItem.EvaluateImageFrame(ctx, out var imageA, out var imageB, out var blend);

                        var cachedA = imageA != null ? GetImage(ctx, imageA) : null;

                        if (cachedA != null)
                        {
                            var scaledWidth = gaugeDisplayItem.Width;
                            var scaledHeight = gaugeDisplayItem.Height;

                            if (scaledWidth == 0) scaledWidth = cachedA.Width;
                            if (scaledHeight == 0) scaledHeight = cachedA.Height;

                            scaledWidth = (int)Math.Floor(scaledWidth * gaugeDisplayItem.Scale / 100.0f * scale);
                            scaledHeight = (int)Math.Floor(scaledHeight * gaugeDisplayItem.Scale / 100.0f * scale);

                            g.DrawImage(cachedA, x, y, scaledWidth, scaledHeight, ctx.Elapsed, gaugeDisplayItem.Rotation, 0, 0, flipX: gaugeDisplayItem.FlipX);

                            if (imageB != null && imageB != imageA && blend > 0f)
                            {
                                var cachedB = GetImage(ctx, imageB);
                                if (cachedB != null)
                                {
                                    g.DrawImage(cachedB, x, y, scaledWidth, scaledHeight, ctx.Elapsed, gaugeDisplayItem.Rotation, 0, 0, flipX: gaugeDisplayItem.FlipX, opacity: blend);
                                }
                            }
                        }
                        else
                        {
                            // No frame image: the placeholder at the gauge's size (100×100 where it is 0).
                            var size = ImagePlaceholder.Resolve(new SKSize(
                                gaugeDisplayItem.Width * gaugeDisplayItem.Scale / 100.0f,
                                gaugeDisplayItem.Height * gaugeDisplayItem.Scale / 100.0f));
                            var scaledWidth = (int)Math.Floor(size.Width * scale);
                            var scaledHeight = (int)Math.Floor(size.Height * scale);

                            if (scaledWidth > 0 && scaledHeight > 0)
                            {
                                using var placeholder = SKBitmapExtensions.CreateMaterialNoImageBitmap(scaledWidth, scaledHeight);
                                g.DrawBitmap(placeholder, x, y, scaledWidth, scaledHeight, gaugeDisplayItem.Rotation, flipX: gaugeDisplayItem.FlipX);
                            }
                        }
                        break;
                    }
                case ChartDisplayItem chartDisplayItem:
                    {
                        var width = scale == 1 ? (int)chartDisplayItem.Width : (int)Math.Floor(chartDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)chartDisplayItem.Height : (int)Math.Floor(chartDisplayItem.Height * scale);

                        using var graphBitmap = new SKBitmap(chartDisplayItem.Width, chartDisplayItem.Height);
                        using var canvas = new SKCanvas(graphBitmap);

                        using var g1 = new SkiaGraphics(canvas, g.FontScale);
                        GraphDraw.Run(chartDisplayItem, g1, ctx);

                        if (chartDisplayItem.FlipX)
                        {
                            g.DrawBitmap(graphBitmap, x, y, width, height, rotation: chartDisplayItem.Rotation, flipX: true);
                        }
                        else
                        {
                            g.DrawBitmap(graphBitmap, x, y, width, height, rotation: chartDisplayItem.Rotation);
                        }

                        break;
                    }

                case ShapeDisplayItem shapeDisplayItem:
                    {
                        var width = scale == 1 ? (int)shapeDisplayItem.Width : (int)Math.Floor(shapeDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)shapeDisplayItem.Height : (int)Math.Floor(shapeDisplayItem.Height * scale);

                        var centerX = x + width / 2;
                        var centerY = y + height / 2;

                        using var path = new SKPath();

                        switch (shapeDisplayItem.Type)
                        {
                            case ShapeDisplayItem.ShapeType.Rectangle:
                                {
                                    path.AddRect(SKRect.Create(x, y, width, height));
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Capsule:
                                {
                                    path.AddRoundRect(SKRect.Create(x, y, width, height), shapeDisplayItem.CornerRadius, shapeDisplayItem.CornerRadius);
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Ellipse:
                                {
                                    path.AddOval(SKRect.Create(x, y, width, height));
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Triangle:
                                {
                                    // Top point
                                    var topX = centerX;
                                    var topY = y;

                                    // Bottom left point
                                    var bottomLeftX = x;
                                    var bottomLeftY = y + height;

                                    // Bottom right point
                                    var bottomRightX = x + width;
                                    var bottomRightY = y + height;

                                    // Create the triangle path
                                    path.MoveTo(topX, topY);
                                    path.LineTo(bottomLeftX, bottomLeftY);
                                    path.LineTo(bottomRightX, bottomRightY);
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Star:
                                {
                                    // Star that stretches to fit the bounding box
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    // Calculate the 5 outer points with scaling
                                    var points = new SKPoint[10];
                                    for (int i = 0; i < 10; i++)
                                    {
                                        var angle = (i * 36 - 90) * Math.PI / 180;
                                        var r = (i % 2 == 0) ? 1f : 0.382f; // 1 for outer, 0.382 for inner

                                        // Apply different scaling for X and Y
                                        points[i] = new SKPoint(
                                            centerX + (float)(r * scaleX * Math.Cos(angle)),
                                            centerY + (float)(r * scaleY * Math.Sin(angle))
                                        );
                                    }

                                    // Create the path
                                    path.MoveTo(points[0]);
                                    for (int i = 1; i < 10; i++)
                                    {
                                        path.LineTo(points[i]);
                                    }
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Pentagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 5; i++)
                                    {
                                        var angle = (i * 72 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Hexagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 6; i++)
                                    {
                                        var angle = (i * 60 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Plus:
                                {
                                    var thickness = Math.Min(width, height) / 3;
                                    var horizontalY = centerY - thickness / 2;
                                    var verticalX = centerX - thickness / 2;

                                    // Create plus shape as single path
                                    path.MoveTo(verticalX, y);
                                    path.LineTo(verticalX + thickness, y);
                                    path.LineTo(verticalX + thickness, horizontalY);
                                    path.LineTo(x + width, horizontalY);
                                    path.LineTo(x + width, horizontalY + thickness);
                                    path.LineTo(verticalX + thickness, horizontalY + thickness);
                                    path.LineTo(verticalX + thickness, y + height);
                                    path.LineTo(verticalX, y + height);
                                    path.LineTo(verticalX, horizontalY + thickness);
                                    path.LineTo(x, horizontalY + thickness);
                                    path.LineTo(x, horizontalY);
                                    path.LineTo(verticalX, horizontalY);
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Arrow:
                                {
                                    var headHeight = height * 0.4f;
                                    var shaftWidth = width * 0.5f;

                                    // Arrow pointing up
                                    path.MoveTo(centerX, y);                                    // Tip
                                    path.LineTo(x + width, y + headHeight);                     // Right head
                                    path.LineTo(x + width * 0.75f, y + headHeight);
                                    path.LineTo(x + width * 0.75f, y + height);                // Right shaft bottom
                                    path.LineTo(x + width * 0.25f, y + height);                // Left shaft bottom
                                    path.LineTo(x + width * 0.25f, y + headHeight);
                                    path.LineTo(x, y + headHeight);                            // Left head
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Octagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 8; i++)
                                    {
                                        var angle = (i * 45 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Trapezoid:
                                {
                                    var topInset = width * 0.2f;

                                    path.MoveTo(x + topInset, y);                    // Top left
                                    path.LineTo(x + width - topInset, y);            // Top right
                                    path.LineTo(x + width, y + height);              // Bottom right
                                    path.LineTo(x, y + height);                      // Bottom left
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Parallelogram:
                                {
                                    var skew = width * 0.25f;

                                    path.MoveTo(x + skew, y);                        // Top left
                                    path.LineTo(x + width, y);                       // Top right
                                    path.LineTo(x + width - skew, y + height);       // Bottom right
                                    path.LineTo(x, y + height);                      // Bottom left
                                    path.Close();
                                    break;
                                }
                        }


                        if (shapeDisplayItem.Rotation != 0)
                        {
                            var matrix = SKMatrix.CreateRotationDegrees(shapeDisplayItem.Rotation, centerX, centerY);
                            path.Transform(matrix);
                        }

                        if (shapeDisplayItem.ShowFill)
                        {
                            if (SKColor.TryParse(shapeDisplayItem.FillColor, out var color))
                            {
                                if (shapeDisplayItem.ShowGradient && SKColor.TryParse(shapeDisplayItem.GradientColor, out var gradientColor) && SKColor.TryParse(shapeDisplayItem.GradientColor2, out var gradientColor2))
                                {
                                    g.FillPath(path, color, gradientColor, gradientColor2, shapeDisplayItem.GetGradientAnimationOffset(ctx.Elapsed), shapeDisplayItem.GradientType);
                                }
                                else
                                {
                                    g.FillPath(path, color);
                                }
                            }
                        }

                        if (shapeDisplayItem.ShowFrame)
                        {
                            if (SKColor.TryParse(shapeDisplayItem.FrameColor, out var color))
                            {
                                if (shapeDisplayItem.ShowGradient && SKColor.TryParse(shapeDisplayItem.GradientColor, out var gradientColor) && SKColor.TryParse(shapeDisplayItem.GradientColor2, out var gradientColor2))
                                {
                                    g.DrawPath(path, color, shapeDisplayItem.FrameThickness * scale, gradientColor, gradientColor2, shapeDisplayItem.GetGradientAnimationOffset(ctx.Elapsed), shapeDisplayItem.GradientType);
                                }
                                else
                                {
                                    g.DrawPath(path, color, shapeDisplayItem.FrameThickness * scale);
                                }
                            }
                        }

                        break;
                    }
            }
        }

        private static IRenderImage? GetImage(RenderContext ctx, ImageDisplayItem imageDisplayItem)
        {
            var source = imageDisplayItem.ResolveSource(ctx);
            return string.IsNullOrEmpty(source) ? null : ctx.Images.Get(source, imageDisplayItem.PersistentCache);
        }

        private static SKBitmap CreateVideoOverlay(float imageWidth, double progress, TimeSpan currentTime, TimeSpan duration, double frameRate, float volume, MediaPlaybackStatus playerStatus, TimeSpan elapsed)
        {
            const int overlayHeight = 40;
            var overlayBitmap = new SKBitmap((int)imageWidth, overlayHeight);
            using var overlayCanvas = new SKCanvas(overlayBitmap);

            // Draw gradient
            var gradientRect = SKRect.Create(0, 0, imageWidth, overlayHeight);
            using var gradientPaint = new SKPaint();
            gradientPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, overlayHeight),
                [SKColor.Parse("#00000000"), SKColor.Parse("#40000000"), SKColor.Parse("#90000000")],
                [0.0f, 0.6f, 1.0f],
                SKShaderTileMode.Clamp
            );
            overlayCanvas.DrawRect(gradientRect, gradientPaint);

            // Progress bar dimensions
            var progressBarHeight = 3f;
            var progressBarY = overlayHeight - 28f;
            var progressBarLeft = 12f;
            var progressBarWidth = imageWidth - 24f;

            var isLive = duration == TimeSpan.Zero;

            // Draw background progress bar
            using var backgroundPaint = new SKPaint { Color = SKColor.Parse("#4DFFFFFF"), IsAntialias = true };
            var backgroundRect = SKRect.Create(progressBarLeft, progressBarY, progressBarWidth, progressBarHeight);
            overlayCanvas.DrawRoundRect(backgroundRect, 1.5f, 1.5f, backgroundPaint);

            // Draw progress
            if (isLive || progress > 0)
            {
                var progressWidth = isLive ? progressBarWidth : (float)(progressBarWidth * Math.Min(progress, 1.0));
                var progressRect = SKRect.Create(progressBarLeft, progressBarY, progressWidth, progressBarHeight);
                
                if (isLive && playerStatus == MediaPlaybackStatus.Playing)
                {
                    // Create animated gradient for live content
                    var animationTime = (float)((long)elapsed.TotalMilliseconds % 3000) / 3000f;
                    
                    using var liveProgressPaint = new SKPaint { IsAntialias = true };
                    
                    // Create a smoother pulsing effect using sine wave
                    var pulse = (float)(Math.Sin(animationTime * Math.PI * 2) * 0.2 + 0.8);
                    
                    // YouTube Live style gradient - red to orange/yellow
                    var gradientColors = new SKColor[] {
                        SKColor.Parse("#CC0000"),  // Dark red
                        SKColor.Parse("#FF0000"),  // Red
                        SKColor.Parse("#FF3333"),  // Light red
                        SKColor.Parse("#FF6600"),  // Red-orange
                        SKColor.Parse("#FF9900"),  // Orange
                        SKColor.Parse("#FF6600"),  // Red-orange
                        SKColor.Parse("#FF3333"),  // Light red
                        SKColor.Parse("#FF0000"),  // Red
                        SKColor.Parse("#CC0000")   // Dark red
                    };
                    
                    // Fixed gradient positions for 9 colors
                    var gradientPositions = new float[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f };
                    
                    // Create a wider gradient that moves across the bar
                    var gradientWidth = progressWidth * 2f;
                    var gradientOffset = -gradientWidth + (animationTime * gradientWidth * 2f);
                    
                    liveProgressPaint.Shader = SKShader.CreateLinearGradient(
                        new SKPoint(progressBarLeft + gradientOffset, progressBarY),
                        new SKPoint(progressBarLeft + gradientOffset + gradientWidth, progressBarY),
                        gradientColors,
                        gradientPositions,
                        SKShaderTileMode.Repeat
                    );
                    
                    overlayCanvas.DrawRoundRect(progressRect, 1.5f, 1.5f, liveProgressPaint);
                }
                else
                {
                    // Standard red bar for non-live or non-playing content
                    using var progressPaint = new SKPaint { Color = SKColor.Parse("#FF0000"), IsAntialias = true };
                    overlayCanvas.DrawRoundRect(progressRect, 1.5f, 1.5f, progressPaint);
                }

                // Draw progress circle (only for non-live content)
                if (!isLive && progress <= 1.0)
                {
                    var circleX = progressBarLeft + progressWidth;
                    var circleY = progressBarY + (progressBarHeight / 2f);

                    using var circlePaint = new SKPaint { Color = SKColor.Parse("#FF0000"), IsAntialias = true };
                    overlayCanvas.DrawCircle(circleX, circleY, 5f, circlePaint);

                    using var circleBorderPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                    overlayCanvas.DrawCircle(circleX, circleY, 5f, circleBorderPaint);
                }
            }

            var percent = (int)(volume * 100);
            var icon = percent switch
            {
                0 => "🔇",
                < 33 => "🔈",
                < 66 => "🔉",
                _ => "🔊"
            };

            // Measure text widths for positioning
            using var measurePaint = new SKPaint();

            if (isLive)
            {
                // Draw LIVE indicator
                using var liveFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Symbol", SKFontStyle.Bold), 12f);
                using var livePaint = new SKPaint { Color = SKColor.Parse("#FF0000"), IsAntialias = true };

                var liveText = playerStatus == MediaPlaybackStatus.Playing ? "LIVE" : "CONNECTING..";
                overlayCanvas.DrawText(liveText, progressBarLeft, overlayHeight - 8f, liveFont, livePaint);

                // Measure the LIVE text width
                var liveTextWidth = liveFont.MeasureText(liveText);

                // Draw volume icon to the right of LIVE
                using var iconFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Emoji", SKFontStyle.Normal), 14f);
                using var iconPaint = new SKPaint { Color = SKColor.Parse("#FFFF00"), IsAntialias = true }; // Yellow color
                overlayCanvas.DrawText(icon, progressBarLeft + liveTextWidth + 10f, overlayHeight - 8f, iconFont, iconPaint);
            }
            else
            {
                // Draw time display for non-live content
                var timeDisplay = $"{currentTime:hh\\:mm\\:ss\\.fff} / {duration:hh\\:mm\\:ss\\.fff}";
                using var timeFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 12f);
                using var timePaint = new SKPaint { Color = SKColor.Parse("#CCFFFFFF"), IsAntialias = true };
                overlayCanvas.DrawText(timeDisplay, progressBarLeft, overlayHeight - 8f, timeFont, timePaint);

                // Measure the time text width
                var timeTextWidth = timeFont.MeasureText(timeDisplay);

                // Draw volume icon to the right of time
                using var iconFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Emoji", SKFontStyle.Normal), 14f);
                using var iconPaint = new SKPaint { Color = SKColor.Parse("#FFFFFF"), IsAntialias = true }; // White color
                overlayCanvas.DrawText(icon, progressBarLeft + timeTextWidth + 10f, overlayHeight - 8f, iconFont, iconPaint);
            }


            // Draw right side display
            var frameRateText = $"{frameRate:F0} FPS";
            var rightDisplayText = $"{frameRateText}";

            using var rightFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Symbol", SKFontStyle.Normal), 11f);
            using var rightPaint = new SKPaint { Color = SKColor.Parse("#CCFFFFFF"), IsAntialias = true };
            var rightTextWidth = rightFont.MeasureText(rightDisplayText);
            overlayCanvas.DrawText(rightDisplayText, progressBarLeft + progressBarWidth - rightTextWidth, overlayHeight - 8f, rightFont, rightPaint);

            return overlayBitmap;
        }

        private static void DrawTableSensorDisplay(SkiaGraphics g, TableSensorDisplayItem tableSensorDisplayItem, 
            int x, int y, float scale, string color, DataTable table)
        {
            var fontSize = (int)Math.Floor(tableSensorDisplayItem.FontSize * scale);
            var glowRadius = tableSensorDisplayItem.GlowEnabled ? tableSensorDisplayItem.GlowRadius : 0;
            var glowColor = tableSensorDisplayItem.GlowEnabled && !string.IsNullOrEmpty(tableSensorDisplayItem.GlowColor) ? tableSensorDisplayItem.GlowColor : null;
            var glowBlendMode = tableSensorDisplayItem.GlowEnabled ? tableSensorDisplayItem.GlowBlendMode : null;

            foreach (var cell in LayoutTable(g, tableSensorDisplayItem, x, y, fontSize, table))
            {
                g.DrawString(cell.Text,
                    tableSensorDisplayItem.Font, tableSensorDisplayItem.FontStyle, fontSize, color,
                    cell.X, cell.Y,
                    cell.RightAlign, tableSensorDisplayItem.CenterAlign,
                    tableSensorDisplayItem.Bold, tableSensorDisplayItem.Italic,
                    tableSensorDisplayItem.Underline, tableSensorDisplayItem.Strikeout,
                    tableSensorDisplayItem.Wrap, tableSensorDisplayItem.Ellipsis, cell.Width, 0, glowRadius, glowColor, glowBlendMode);
            }
        }

        /// <summary>A table cell as <see cref="DrawTableSensorDisplay"/> draws it: text at (X, Y), Width wide, one line apart.</summary>
        internal readonly record struct TableCell(string Text, int X, int Y, int Width, bool RightAlign);

        /// <summary>
        /// The cells of a table in draw order (per column: the header when shown, then up to MaxRows rows of
        /// <see cref="IPluginData"/>). Columns are each format length + 10 apart; rows are one "A" line apart, whatever
        /// a cell's own height. Shared by drawing and <see cref="ProfileRenderer.MeasureBounds"/>.
        /// </summary>
        internal static IEnumerable<TableCell> LayoutTable(SkiaGraphics g, TableSensorDisplayItem tableSensorDisplayItem,
            int x, int y, int fontSize, DataTable table)
        {
            var maxRows = tableSensorDisplayItem.MaxRows;
            var formatParts = tableSensorDisplayItem.TableFormat.Split('|');

            (_, float fHeight) = g.MeasureString("A", tableSensorDisplayItem.Font, tableSensorDisplayItem.FontStyle, 
                fontSize, tableSensorDisplayItem.Bold, tableSensorDisplayItem.Italic, 
                tableSensorDisplayItem.Underline, tableSensorDisplayItem.Strikeout);

            var tWidth = 0;
            for (int i = 0; i < formatParts.Length; i++)
            {
                var split = formatParts[i].Split(':');
                if (split.Length == 2 && 
                    int.TryParse(split[0], out var column) && 
                    column < table.Columns.Count && 
                    int.TryParse(split[1], out var length))
                {
                    var rightAlign = i != 0 && tableSensorDisplayItem.RightAlign;

                    if (tableSensorDisplayItem.ShowHeader)
                    {
                        yield return new TableCell(table.Columns[column].ColumnName, x + tWidth, y, length, rightAlign);
                    }

                    var rows = Math.Min(table.Rows.Count, maxRows);
                    for (int j = 0; j < rows; j++)
                    {
                        if (table.Rows[j][column] is IPluginData pluginData)
                        {
                            yield return new TableCell(pluginData.ToString(), x + tWidth,
                                (int)(y + (fHeight * (j + (tableSensorDisplayItem.ShowHeader ? 1 : 0)))), length, rightAlign);
                        }
                    }

                    tWidth += length + 10;
                }
            }
        }

        private static void DrawMarqueeText(SkiaGraphics g, RenderContext ctx, TextDisplayItem textDisplayItem, 
            string text, string color, int x, int y, float scale)
        {
            var fontSize = (int)Math.Floor(textDisplayItem.FontSize * scale);
            var drawWidth = (int)(textDisplayItem.Width * scale);
            
            // Validate marquee speed to prevent division by zero
            var scrollSpeed = Math.Max(textDisplayItem.MarqueeSpeed, 1) * 1.0;
            var padding = (int)(textDisplayItem.MarqueeSpacing * scale); // Apply scale to spacing
            
            // Measure text once
            var (textWidth, textHeight) = g.MeasureString(text, textDisplayItem.Font, textDisplayItem.FontStyle,
                fontSize, textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline,
                textDisplayItem.Strikeout, false, false, 0, 0);

            if (textWidth <= drawWidth)
            {
                // Text fits, draw normally without marquee
                DrawNormalText(g, textDisplayItem, text, color, x, y, scale);
                return;
            }

            // Calculate animation
            var currentTime = (long)ctx.Elapsed.TotalMilliseconds;
            var totalDistance = textWidth + padding;
            var duration = totalDistance / scrollSpeed * 1000; // milliseconds
            var progress = (currentTime % duration) / duration;
            var offset = (int)(progress * totalDistance);

            // Calculate height for clipping
            var clipHeight = textDisplayItem.Height > 0 
                ? (int)(textDisplayItem.Height * scale) 
                : (int)textHeight;

            // Set up clipping
            g.Canvas.Save();
            g.Canvas.ClipRect(new SKRect(x, y, x + drawWidth, y + clipHeight));

            // Draw text twice for infinite scroll
            // Note: Disable alignment for marquee as position is controlled by offset
            var glowRadius = textDisplayItem.GlowEnabled ? textDisplayItem.GlowRadius : 0;
            var glowColor = textDisplayItem.GlowEnabled && !string.IsNullOrEmpty(textDisplayItem.GlowColor) ? textDisplayItem.GlowColor : null;
            var glowBlendMode = textDisplayItem.GlowEnabled ? textDisplayItem.GlowBlendMode : null;

            g.DrawString(text, textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color, 
                x - offset, y, false, false, // Disable alignment for marquee
                textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, 
                textDisplayItem.Strikeout, false, false, 0, 0, glowRadius, glowColor, glowBlendMode);

            g.DrawString(text, textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color, 
                x - offset + (int)textWidth + padding, y, false, false, // Disable alignment for marquee
                textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, 
                textDisplayItem.Strikeout, false, false, 0, 0, glowRadius, glowColor, glowBlendMode);

            g.Canvas.Restore();
        }

        private static void DrawNormalText(SkiaGraphics g, TextDisplayItem textDisplayItem, 
            string text, string color, int x, int y, float scale)
        {
            var fontSize = (int)Math.Floor(textDisplayItem.FontSize * scale);
            var drawWidth = (int)(textDisplayItem.Width * scale);
            var glowRadius = textDisplayItem.GlowEnabled ? textDisplayItem.GlowRadius : 0;
            var glowColor = textDisplayItem.GlowEnabled && !string.IsNullOrEmpty(textDisplayItem.GlowColor) ? textDisplayItem.GlowColor : null;
            var glowBlendMode = textDisplayItem.GlowEnabled ? textDisplayItem.GlowBlendMode : null;

            var drawHeight = (int)(textDisplayItem.Height * scale);

            g.DrawString(text, textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color,
                x, y, textDisplayItem.RightAlign, textDisplayItem.CenterAlign,
                textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline,
                textDisplayItem.Strikeout, textDisplayItem.Wrap, textDisplayItem.Ellipsis, drawWidth, drawHeight, glowRadius, glowColor, glowBlendMode, textDisplayItem.Rotation);
        }

    }
}
