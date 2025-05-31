using SkiaSharp.Views.Desktop;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using InfoPanel.Utils;
using InfoPanel.Models;
using System.Drawing;
using System.Windows.Media.Media3D;
using static System.Net.Mime.MediaTypeNames;
using System.IO;
using InfoPanel.Drawing;

namespace InfoPanel.Views
{
    /// <summary>
    /// Interaction logic for SkiaDisplayWindow.xaml
    /// </summary>
    public partial class SkiaDisplayWindow : Window
    {
        private readonly Timer _timer;
        private readonly Stopwatch _stopwatch = new();
        private readonly FpsCounter fpsCounter = new(200);

        private Profile _profile = ConfigModel.Instance.Profiles.First();

        public SkiaDisplayWindow()
        {
            InitializeComponent();

            Width = _profile.Width;
            Height = _profile.Height;

            // Set up a timer to refresh the drawing
            _timer = new(1000/60.0); // Refresh every 100ms (10 times per second)
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Start();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Invalidate the SKElement on the UI thread
            Dispatcher.Invoke(() => skElement.InvalidateVisual());
        }

        private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            _stopwatch.Restart();
            var canvas = e.Surface.Canvas;

            // Clear the canvas
            canvas.Clear(SKColors.Transparent);

            RenderProfile(canvas);
            //Draw random shapes
            //for (int i = 0; i < 10; i++)
            //{
            //    DrawRandomShape(canvas, width, height);
            //}
            //Trace.WriteLine($"Drawing took {_stopwatch.ElapsedMilliseconds}ms");

            fpsCounter.Update();
            //Trace.WriteLine($"FPS: {fpsCounter.FramesPerSecond}");
        }

        private void RenderProfile(SKCanvas canvas)
        {
            var profile = ConfigModel.Instance.Profiles.First();
            SkiaGraphics skiaGraphics = new(canvas, 1.33f);
            PanelDraw.Run(profile, skiaGraphics);

        }


        private readonly Random _random = new Random();
        private void DrawRandomShape(SKCanvas canvas, int width, int height)
        {
            using (var paint = new SKPaint())
            {
                paint.Color = new SKColor(
                    (byte)_random.Next(256),
                    (byte)_random.Next(256),
                    (byte)_random.Next(256),
                    (byte)_random.Next(256)); // Random color with random alpha
                paint.IsAntialias = true;

                // Randomly choose to draw a circle or rectangle
                if (_random.Next(2) == 0)
                {
                    // Draw a circle
                    float radius = _random.Next(10, 100);
                    float x = _random.Next((int)radius, width - (int)radius);
                    float y = _random.Next((int)radius, height - (int)radius);
                    canvas.DrawCircle(x, y, radius, paint);
                }
                else
                {
                    // Draw a rectangle
                    float rectWidth = _random.Next(20, 100);
                    float rectHeight = _random.Next(20, 100);
                    float x = _random.Next(0, width - (int)rectWidth);
                    float y = _random.Next(0, height - (int)rectHeight);
                    canvas.DrawRect(x, y, rectWidth, rectHeight, paint);
                }
            }
        }
    }
}
