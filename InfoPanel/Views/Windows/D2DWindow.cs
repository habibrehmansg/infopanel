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

        internal readonly bool D2DDraw;

        private float _width;

        public D2DWindow(bool d2dDraw)
        {
            D2DDraw = d2dDraw;

            if (D2DDraw)
            {
                AllowsTransparency = false;
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
            }

            this._width = (float)this.Width;
            this.SizeChanged += D2DWindow_SizeChanged;
        }

        private void D2DWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            lock (_syncObj)
            {
                this._width = (float)this.Width;
                this.Device?.Resize();
            }
        }

        private readonly object _syncObj = new();
        private volatile bool _isProcessing = false; // Flag to prevent overlapping

        protected void D2DRender()
        {
            if (_isProcessing)
                return;

            lock (_syncObj)
            {
                if (this.Graphics == null)
                {
                    return;
                }

                _isProcessing = true;

                try
                {
                    this.Graphics.BeginRender(D2DColor.Transparent);
                    this.OnRender(this.Graphics);
                    this.Graphics.EndRender();
                }
                finally
                {
                    _isProcessing = false;
                }
            }
        }

        protected virtual void OnRender(D2DGraphics d2dGraphics)
        {
          
        }
    }
}
