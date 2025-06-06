using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfoPanel.Views.Common
{
    /// <summary>
    /// Interaction logic for DisplayWindow.xaml
    /// </summary>
    public partial class DisplayWindow
    {
        SKElement? _sKElement;
        SKGLElement? _skGlElement;

        public Profile Profile { get; }
        public bool Direct2DMode { get; }

        private bool _dragMove = false;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        private bool _isUserResizing = false;
        private readonly DispatcherTimer _resizeTimer;
        private bool _isDpiChanging = false;

        private Timer? _renderTimer;
        private readonly FpsCounter FpsCounter = new();

        public DisplayWindow(Profile profile)
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            Profile = profile;
            DataContext = this;

            Direct2DMode = profile.Direct2DMode;

            InitializeComponent();
            InjectSkiaElement();

            if (profile.Resize)
            {
                ResizeMode = ResizeMode.CanResize;
            }
            else
            {
                ResizeMode = ResizeMode.NoResize;
            }

            Loaded += Window_Loaded;
            Closing += DisplayWindow_Closing;

            Profile.PropertyChanged += Profile_PropertyChanged;
            ConfigModel.Instance.Settings.PropertyChanged += Config_PropertyChanged;

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            DpiChanged += DisplayWindow_DpiChanged;
            LocationChanged += DisplayWindow_LocationChanged;
            SizeChanged += DisplayWindow_SizeChanged;

            _resizeTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _resizeTimer.Tick += OnResizeCompleted;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Variable to hold the handle for the form
            var helper = new WindowInteropHelper(this).Handle;
            //Performing some magic to hide the form from Alt+Tab
            _ = SetWindowLong(helper, GWL_EX_STYLE, (GetWindowLong(helper, GWL_EX_STYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

            SetWindowPositionRelativeToScreen();

            UpdateSkiaTimer();
        }

        private void UpdateSkiaTimer()
        {
            double interval = (1000.0 / ConfigModel.Instance.Settings.TargetFrameRate) - 1;
            FpsCounter.SetMaxFrames(ConfigModel.Instance.Settings.TargetFrameRate);

            if (_renderTimer == null)
            {
                // Initialize the timer
                _renderTimer = new System.Timers.Timer(interval);
                _renderTimer.Elapsed += OnTimerElapsed;
                _renderTimer.AutoReset = true;
                _renderTimer.Start();
            }
            else
            {
                // Just update the interval if the timer already exists
                _renderTimer.Interval = interval;
            }
        }

        private void Clear(DisplayItem displayItem)
        {
            if (displayItem is GroupDisplayItem group)
            {
                foreach (var groupItem in group.DisplayItems)
                {
                    Clear(groupItem);
                }
            }
            else if (displayItem is GaugeDisplayItem gauge)
            {
                foreach (var gaugeItem in gauge.Images)
                {
                    Clear(gaugeItem);
                }
            }
            else if (displayItem is ImageDisplayItem image)
            {
                Cache.GetLocalImage(image, false)?.DisposeD2DAssets();
            }
        }

        private void DisplayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _resizeTimer.Stop();
            _resizeTimer.Tick -= OnResizeCompleted;

            ConfigModel.Instance.Settings.PropertyChanged -= Config_PropertyChanged;

            if (_renderTimer != null)
            {
                _renderTimer.Stop();
                _renderTimer.Dispose();
                _renderTimer.Elapsed -= OnTimerElapsed;
            }

            Profile.PropertyChanged -= Profile_PropertyChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

            DpiChanged -= DisplayWindow_DpiChanged;
            LocationChanged -= DisplayWindow_LocationChanged;
            SizeChanged -= DisplayWindow_SizeChanged;

            if (Direct2DMode)
            {
                foreach (var item in SharedModel.Instance.GetProfileDisplayItemsCopy(Profile))
                {
                    Clear(item);
                }
            }

            if (_skGlElement != null && _skGlElement.GRContext is GRContext grContext)
            {
                grContext.PurgeResources();
                grContext.Flush();
                grContext.Submit(true);

                grContext.GetResourceCacheUsage(out var maxResources, out var maxResourceBytes);
                Trace.WriteLine($"Closing {maxResources} items, {maxResourceBytes / 1024 / 1024}mb");

                _skGlElement.PaintSurface -= SkGlElement_PaintSurface;

                BindingOperations.ClearBinding(_skGlElement, WidthProperty);
                BindingOperations.ClearBinding(_skGlElement, HeightProperty);

                _skGlElement = null;

                Trace.WriteLine("Disposed _skGLElement");
            }
        }

        private void InjectSkiaElement()
        {
            var container = FindName("SkiaContainer") as Panel;
            if (container == null) return;

            container.Children.Clear();

            if (Direct2DMode)
            {
                AllowsTransparency = false;
                var skGlElement = new SKGLElement
                {
                    Name = "skGlElement",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                skGlElement.SetBinding(WidthProperty, new Binding("Profile.Width") { Mode = BindingMode.OneWay });
                skGlElement.SetBinding(HeightProperty, new Binding("Profile.Height") { Mode = BindingMode.OneWay });
                skGlElement.PaintSurface += SkGlElement_PaintSurface;
                container.Children.Add(skGlElement);

                _skGlElement = skGlElement;
            }
            else
            {
                AllowsTransparency = true;
                var skElement = new SKElement
                {
                    Name = "skElement",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                skElement.SetBinding(WidthProperty, new Binding("Profile.Width") { Mode = BindingMode.OneWay });
                skElement.SetBinding(HeightProperty, new Binding("Profile.Height") { Mode = BindingMode.OneWay });
                skElement.PaintSurface += SkElement_PaintSurface;
                container.Children.Add(skElement);

                _sKElement = skElement;
            }
        }

        private void DisplayWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Only track user-initiated size changes, not our DPI corrections
            if (!_isDpiChanging)
            {
                _isUserResizing = true;

                // Restart the timer - user is still resizing
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        }

        private void DisplayWindow_DpiChanged(object sender, DpiChangedEventArgs e)
        {
            _isDpiChanging = true;
            MaintainPixelSize();
            _isDpiChanging = false;
        }

        private void DisplayWindow_LocationChanged(object? sender, EventArgs e)
        {
            _isDpiChanging = true;
            MaintainPixelSize();
            _isDpiChanging = false;
        }

        private void OnResizeCompleted(object? sender, EventArgs e)
        {
            _resizeTimer.Stop();

            if (_isUserResizing)
            {
                _isUserResizing = false;
                UpdateModelWithNewSize();
            }
        }

        private void MaintainPixelSize()
        {
            // Convert desired pixel size to WPF units for current DPI
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var dpiX = source.CompositionTarget.TransformToDevice.M11;
                var dpiY = source.CompositionTarget.TransformToDevice.M22;

                // Set size in WPF units that will result in exact pixel dimensions
                this.Width = Profile.Width / dpiX;
                this.Height = Profile.Height / dpiY;
            }
        }

        private void UpdateModelWithNewSize()
        {
            // Convert current WPF size back to pixels
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var dpiX = source.CompositionTarget.TransformToDevice.M11;
                var dpiY = source.CompositionTarget.TransformToDevice.M22;

                var newPixelWidth = this.ActualWidth * dpiX;
                var newPixelHeight = this.ActualHeight * dpiY;

                Profile.Width = (int)newPixelWidth;
                Profile.Height = (int)newPixelHeight;
            }
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!Direct2DMode)
            {
                Dispatcher.Invoke(() => _sKElement?.InvalidateVisual(), DispatcherPriority.Input);
            }
            else
            {
                Dispatcher.Invoke(() => _skGlElement?.InvalidateVisual(), DispatcherPriority.Input);
            }
        }

        private void SkElement_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            PaintSurface(e.Surface.Canvas);
        }

        private void SkGlElement_PaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
        {
            PaintSurface(e.Surface.Canvas);
        }

        private void PaintSurface(SKCanvas canvas)
        {
            if (_renderTimer == null || !_renderTimer.Enabled)
            {
                return;
            }

            canvas.Clear();

            SkiaGraphics skiaGraphics = new(canvas, 1.33f);
            PanelDraw.Run(Profile, skiaGraphics, cacheHint: $"DISPLAY-{Profile.Guid}", fpsCounter: FpsCounter);
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            Trace.WriteLine("SystemEvents_DisplaySettingsChanged");
            _dispatcher.BeginInvoke(() =>
            {
                SetWindowPositionRelativeToScreen();
            });
        }

        public void Fullscreen()
        {
            _dispatcher.BeginInvoke(() =>
            {
                var screen = ScreenHelper.GetWindowScreen(this);
                if (screen != null)
                {
                    Profile.WindowX = 0;
                    Profile.WindowY = 0;
                    Profile.Width = screen.Bounds.Width;
                    Profile.Height = screen.Bounds.Height;
                }
            });
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConfigModel.Instance.Settings.TargetFrameRate))
            {
                UpdateSkiaTimer();
            }
        }

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {

            if (e.PropertyName == nameof(Profile.TargetWindow) || e.PropertyName == nameof(Profile.WindowX)
                                 || e.PropertyName == nameof(Profile.WindowY) || e.PropertyName == nameof(Profile.StrictWindowMatching))
            {
                if (!_dragMove)
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        SetWindowPositionRelativeToScreen();
                    });
                }
            }
            else if (e.PropertyName == nameof(Profile.Resize))
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (Profile.Resize)
                    {
                        ResizeMode = ResizeMode.CanResize;
                    }
                    else
                    {
                        ResizeMode = ResizeMode.NoResize;
                    }
                });
            }
        }


        private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (SharedModel.Instance.SelectedVisibleItems != null)
            {
                foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                {
                    switch (e.Key)
                    {
                        case Key.Up:
                            displayItem.Y -= SharedModel.Instance.MoveValue;
                            break;
                        case Key.Down:
                            displayItem.Y += SharedModel.Instance.MoveValue;
                            break;
                        case Key.Left:
                            displayItem.X -= SharedModel.Instance.MoveValue;
                            break;
                        case Key.Right:
                            displayItem.X += SharedModel.Instance.MoveValue;
                            break;
                    }
                }
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            dragStart = false;
        }

        private void SetWindowPositionRelativeToScreen()
        {
            var screens = ScreenHelper.GetAllMonitors();
            MonitorInfo? targetScreen = null;

            if (Profile.TargetWindow is TargetWindow targetWindow)
            {
                targetScreen ??= screens.FirstOrDefault(s => s.DeviceName == targetWindow.DeviceName && s.Bounds.Width == targetWindow.Width && s.Bounds.Height == targetWindow.Height);

                if (!Profile.StrictWindowMatching)
                {
                    targetScreen ??= screens.FirstOrDefault(s => s.DeviceName == targetWindow.DeviceName);
                    targetScreen ??= screens.FirstOrDefault(s => s.Bounds.Width == targetWindow.Width && s.Bounds.Height == targetWindow.Height);
                }
            }

            if (!Profile.StrictWindowMatching)
            {
                targetScreen ??= screens.First();
            }

            if (targetScreen != null)
            {
                if (!this.IsVisible)
                {
                    this.Show();
                }

                var x = targetScreen.Bounds.Left + Profile.WindowX;
                var y = targetScreen.Bounds.Top + Profile.WindowY;

                Trace.WriteLine($"SetWindowPositionRelativeToScreen targetScreen={targetScreen}");
                Trace.WriteLine($"SetWindowPositionRelativeToScreen targetScreen={targetScreen.DeviceName} x={x} y={y}");
                ScreenHelper.MoveWindowPhysical(this, x, y);
            }
            else if (this.IsVisible)
            {
                this.Hide();
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (!dragStart)
                {
                    var inSelectionBounds = false;
                    foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                    {
                        if (displayItem.ContainsPoint(e.GetPosition(this)))
                        {
                            inSelectionBounds = true;
                            break;
                        }
                    }

                    if (!inSelectionBounds)
                    {
                        foreach (var selectedItem in SharedModel.Instance.SelectedVisibleItems)
                        {
                            App.Current.Dispatcher.Invoke(() =>
                            {
                                selectedItem.Selected = false;
                            });
                        }
                    }
                }

                if (SharedModel.Instance.SelectedVisibleItems.Count == 0)
                {
                    if (Profile.Drag)
                    {
                        this.DragMove();

                        var screen = ScreenHelper.GetWindowScreen(this);

                        if (screen != null)
                        {
                            var position = ScreenHelper.GetWindowPositionPhysical(this);
                            var relativePosition = ScreenHelper.GetWindowRelativePosition(screen, position);

                            Trace.WriteLine($"SetPosition screen={screen}");
                            Trace.WriteLine($"SetPosition screen={screen.DeviceName} position={position} relativePosition={relativePosition}");

                            _dragMove = true;

                            try
                            {
                                Profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.DeviceName);
                                Profile.WindowX = (int)relativePosition.X;
                                Profile.WindowY = (int)relativePosition.Y;
                            }
                            finally
                            {
                                _dragMove = false;
                            }
                        }
                    }
                }
                else
                {
                    startPosition = e.GetPosition((UIElement)sender);

                    foreach (var item in SharedModel.Instance.SelectedVisibleItems)
                    {
                        item.MouseOffset = new System.Windows.Point(startPosition.X - item.X, startPosition.Y - item.Y);
                    }

                    dragStart = true;
                }
            }
            else if (e.ChangedButton == MouseButton.Middle)
            {

                var selectedProfile = SharedModel.Instance.SelectedProfile;

                if (selectedProfile != Profile)
                {
                    return;
                }

                DisplayItem? clickedItem = null;

                var displayItems = SharedModel.Instance.DisplayItems.ToList();
                displayItems.Reverse();

                foreach (var item in displayItems)
                {
                    if (item.Hidden)
                    {
                        continue;
                    }

                    if (item is GroupDisplayItem groupDisplayItem)
                    {
                        foreach (var groupItem in groupDisplayItem.DisplayItems)
                        {
                            if (groupItem.Hidden)
                            {
                                continue;
                            }

                            if (groupItem.ContainsPoint(e.GetPosition(this)))
                            {
                                clickedItem = groupItem;
                                break;
                            }
                        }

                        continue;
                    }

                    if (clickedItem != null)
                    {
                        break;
                    }

                    if (item.ContainsPoint(e.GetPosition(this)))
                    {
                        clickedItem = item;
                        break;
                    }
                }

                if (clickedItem != null)
                {

                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.LeftShift))
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            SharedModel.Instance.SelectedItem = clickedItem;
                        });
                    }

                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        clickedItem.Selected = true;
                    });
                }
                else
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        SharedModel.Instance.SelectedItem = null;
                    });
                }
            }
        }

        bool dragStart = false;
        System.Windows.Point startPosition = new System.Windows.Point();
        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (dragStart)
            {

                if (SharedModel.Instance.SelectedVisibleItems.Count == 0)
                {
                    dragStart = false;
                    return;
                }

                var gridSize = SharedModel.Instance.MoveValue;

                var currentPosition = e.GetPosition((UIElement)sender);

                foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                {
                    if (displayItem.Selected)
                    {
                        int x = (int)(currentPosition.X - displayItem.MouseOffset.X);
                        int y = (int)(currentPosition.Y - displayItem.MouseOffset.Y);

                        x = (int)(Math.Round((double)x / gridSize) * gridSize);
                        y = (int)(Math.Round((double)y / gridSize) * gridSize);

                        displayItem.X = x;
                        displayItem.Y = y;
                    }
                }

                startPosition = currentPosition;
            }
        }

        private void MenuItemSavePosition_Click(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
        }

        private void MenuItemConfig_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.ShowDesign(Profile);
            }
        }

        private void MenuItemClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        //     [DllImport("user32.dll", SetLastError = true)]
        //     public static extern IntPtr FindWindowEx(IntPtr hP, IntPtr hC, string sC,
        //string sW);

        //     [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        //     [return: MarshalAs(UnmanagedType.Bool)]
        //     public static extern bool EnumWindows(EnumedWindow lpEnumFunc, ArrayList
        //     lParam);

        //     public delegate bool EnumedWindow(IntPtr handleWindow, ArrayList handles);

        //     public static bool GetWindowHandle(IntPtr windowHandle, ArrayList
        //     windowHandles)
        //     {
        //         windowHandles.Add(windowHandle);
        //         return true;
        //     }

        //     private void SetAsDesktopChild()
        //     {
        //         ArrayList windowHandles = new ArrayList();
        //         EnumedWindow callBackPtr = GetWindowHandle;
        //         EnumWindows(callBackPtr, windowHandles);

        //         foreach (IntPtr windowHandle in windowHandles)
        //         {
        //             IntPtr hNextWin = FindWindowEx(windowHandle, IntPtr.Zero,
        //             "SHELLDLL_DefView", null);
        //             if (hNextWin != IntPtr.Zero)
        //             {
        //                 var interop = new WindowInteropHelper(this);
        //                 interop.EnsureHandle();
        //                 interop.Owner = hNextWin;
        //             }
        //         }
        //     }


        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EX_STYLE = -20;
        private const int WS_EX_APPWINDOW = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
    }
}

