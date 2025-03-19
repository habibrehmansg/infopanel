using InfoPanel.Utils;
using System;
using System.Diagnostics;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using unvell.D2DLib;

namespace InfoPanel.Views.Common
{
    public class D2DWindow : Window
    {
        internal IntPtr Handle;
        private D2DDevice? Device;
        private D2DGraphics? Graphics;

        private readonly Timer Timer = new (TimeSpan.FromMilliseconds(16));

        internal bool ShowFps = false;
        private readonly FpsCounter FpsCounter = new();

        internal readonly bool D2DDraw;

        private float _width;

        public D2DWindow(bool d2dDraw)
        {
            D2DDraw = d2dDraw;

            if (D2DDraw)
            {
                AllowsTransparency = false;
                //WindowStyle = WindowStyle.None;
                Loaded += D2DWindow_Loaded;
                Closed += D2DWindow_Closed;
            } else
            {
                AllowsTransparency = true;
            }
        }

        private void D2DWindow_Closed(object? sender, EventArgs e)
        {
            SizeChanged -= D2DWindow_SizeChanged;

            Timer.Stop();
            Timer.Elapsed -= Timer_Tick;
            Timer.Dispose();

            lock(_syncObj)
            {
                Device?.Dispose();
                Device = null;
                Graphics = null;
            }
            Trace.WriteLine("D2DWindow closed");
        }

        private void D2DWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Handle = new WindowInteropHelper(this).Handle;

            if (this.Device == null)
            {
                this.Device = D2DDevice.FromHwnd(Handle);
                this.Device.Resize();
                this.Graphics = new D2DGraphics(this.Device);
                //this.Graphics.SetDPI(96, 96);
            }

            this._width = (float)this.Width;
            this.SizeChanged += D2DWindow_SizeChanged;
            
            Timer.Elapsed += Timer_Tick;
            Timer.Start();
        }

        private void D2DWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            lock (_syncObj)
            {
                this._width = (float)this.Width;
                this.Device?.Resize();
            }
        }

        private static readonly object _syncObj = new();
        private static volatile bool _isProcessing = false; // Flag to prevent overlapping
        private void Timer_Tick(object? sender, EventArgs e)
        {
           if(!Timer.Enabled)
            {
                return;
            }

            lock (_syncObj)
            {
                if (this.Graphics == null)
                {
                    return;
                }

                if (_isProcessing)
                    return;

                _isProcessing = true;


                try
                {
                    this.Graphics.BeginRender(D2DColor.Transparent);
                    this.OnRender(this.Graphics);

                    if (ShowFps)
                    {
                        FpsCounter.Update();
                        var rect = new D2DRect(this._width - 40, 0, 40, 30);
                        this.Graphics.FillRectangle(rect, D2DColor.FromGDIColor(System.Drawing.Color.FromArgb(100, 0, 0, 0)));
                        this.Graphics.DrawTextCenter($"{FpsCounter.FramesPerSecond}", D2DColor.FromGDIColor(System.Drawing.Color.FromArgb(255, 0, 255, 0)),
                            "Arial", 18, rect);
                    }

                    this.Graphics.EndRender();
                }
                finally
                {
                    // After work is done, reset flag
                    //lock (_syncObj) { _isProcessing = false; }
                    _isProcessing = false;
                }
            }
        }

        protected virtual void OnRender(D2DGraphics d2dGraphics)
        {
          
        }
    }
}
