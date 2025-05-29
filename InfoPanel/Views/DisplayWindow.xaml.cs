using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Win32;
using SkiaSharp.Views.Desktop;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using unvell.D2DLib;

namespace InfoPanel.Views.Common
{
    /// <summary>
    /// Interaction logic for DisplayWindow.xaml
    /// </summary>
    public partial class DisplayWindow : D2DWindow
    {
        public Profile Profile { get; }
        public bool Direct2DMode { get; }

        private MediaTimeline? mediaTimeline;
        private MediaClock? mediaClock;

        private bool _dragMove = false;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        private bool _isUserResizing = false;
        private readonly DispatcherTimer _resizeTimer;
        private bool _isDpiChanging = false;

        public DisplayWindow(Profile profile) : base(profile.Direct2DMode)
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            Profile = profile;
            DataContext = this;

            Direct2DMode = profile.Direct2DMode;
            ShowFps = profile.ShowFps;

            InitializeComponent();

            if (profile.Resize)
            {
                ResizeMode = ResizeMode.CanResize;
            }
            else
            {
                ResizeMode = ResizeMode.NoResize;
            }

            Closed += DisplayWindow_Closed;
            Loaded += Window_Loaded;

            Profile.PropertyChanged += Profile_PropertyChanged;
            ConfigModel.Instance.Settings.PropertyChanged += Config_PropertyChanged;

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            DpiChanged += DisplayWindow_DpiChanged;
            LocationChanged += DisplayWindow_LocationChanged;
            SizeChanged += DisplayWindow_SizeChanged;

            _resizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300) // 300ms delay
            };
            _resizeTimer.Tick += OnResizeCompleted;
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

        protected override void OnRender(D2DGraphics d2dGraphics)
        {
            base.OnRender(d2dGraphics);

            using var g = new AcceleratedGraphics(d2dGraphics, this.Handle, Profile.Direct2DFontScale, Profile.Direct2DTextXOffset, Profile.Direct2DTextYOffset);
            PanelDraw.Run(Profile, g);
        }

        private System.Timers.Timer? _skiaTimer;
        private readonly FpsCounter FpsCounter = new();

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Invalidate the SKElement on the UI thread
            Dispatcher.Invoke(() => skElement.InvalidateVisual(), DispatcherPriority.Input);
        }

        private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            if (_skiaTimer == null || !_skiaTimer.Enabled)
            {
                return;
            }

            var canvas = e.Surface.Canvas;
            canvas.Clear();

            SkiaGraphics skiaGraphics = new(canvas, 1.33f);
            PanelDraw.Run(Profile, skiaGraphics);
            FpsCounter.Update();

            if (ShowFps)
            {
                skiaGraphics.FillRectangle("#64000000", e.Info.Width - 40, 0, 40, 30);
                skiaGraphics.DrawString($"{FpsCounter.FramesPerSecond}", "Arial", 14, "#FF00FF00", e.Info.Width - 40, 3, centerAlign: true, width: 40, height: 30);
            }

        }

        private void UpdateSkiaTimer()
        {
            double interval = (1000.0 / ConfigModel.Instance.Settings.TargetFrameRate) - 1;
            FpsCounter.SetMaxFrames(ConfigModel.Instance.Settings.TargetFrameRate);

            if (_skiaTimer == null)
            {
                // Initialize the timer
                _skiaTimer = new System.Timers.Timer(interval);
                _skiaTimer.Elapsed += OnTimerElapsed;
                _skiaTimer.AutoReset = true;
                _skiaTimer.Start();
            }
            else
            {
                // Just update the interval if the timer already exists
                _skiaTimer.Interval = interval;
            }
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


        private void DisplayWindow_Closed(object? sender, EventArgs e)
        {
            _resizeTimer.Stop();
            _resizeTimer.Tick -= OnResizeCompleted;

            ConfigModel.Instance.Settings.PropertyChanged -= Config_PropertyChanged;

            _skiaTimer?.Stop();
            _skiaTimer?.Dispose();

            Profile.PropertyChanged -= Profile_PropertyChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

            SharedModel.Instance.GetProfileDisplayItemsCopy(Profile).ForEach(item =>
            {
                if (item is ImageDisplayItem imageDisplayItem)
                {
                    if (Direct2DMode)
                    {
                        Cache.GetLocalImage(imageDisplayItem, false)?.DisposeD2DAssets();
                    }
                    else
                    {
                        Cache.GetLocalImage(imageDisplayItem, false)?.DisposeAssets();
                    }
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
            else if (e.PropertyName == nameof(Profile.ShowFps))
            {
                _dispatcher.BeginInvoke(() =>
                {
                    ShowFps = Profile.ShowFps;
                });
            }
            else if (e.PropertyName == nameof(Profile.VideoBackgroundFilePath) || e.PropertyName == nameof(Profile.VideoBackgroundRotation))
            {
                //_dispatcher.BeginInvoke(() =>
                //{
                //    if (!Profile.Direct2DMode && Profile.VideoBackgroundFilePath is string filePath)
                //    {
                //        var videoFilePath = FileUtil.GetRelativeAssetPath(Profile, filePath);
                //        LoadVideoBackground(videoFilePath);
                //    }
                //    else
                //    {
                //        StopVideoBackground();
                //    }
                //});
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
                    targetScreen ??= screens.FirstOrDefault(s => s.DeviceName.Equals(targetWindow.DeviceName));
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
                            App.Current.Dispatcher.BeginInvoke(() => {
                                selectedItem.Selected = false;
                            });
                        }
                    }
                }

                if (SharedModel.Instance.SelectedVisibleItems.Count == 0)
                {
                    if (Profile.Drag)
                    {
                        if (this.ResizeMode != System.Windows.ResizeMode.NoResize)
                        {
                            this.ResizeMode = System.Windows.ResizeMode.NoResize;
                            this.UpdateLayout();
                        }

                        this.DragMove();

                        if (Profile.Resize)
                        {
                            if (this.ResizeMode == System.Windows.ResizeMode.NoResize)
                            {
                                // restore resize grips
                                this.ResizeMode = System.Windows.ResizeMode.CanResizeWithGrip;
                                this.UpdateLayout();
                            }
                        }

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

                    if(clickedItem != null)
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
                        Application.Current.Dispatcher.BeginInvoke(() => {
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Variable to hold the handle for the form
            var helper = new WindowInteropHelper(this).Handle;
            //Performing some magic to hide the form from Alt+Tab
            _ = SetWindowLong(helper, GWL_EX_STYLE, (GetWindowLong(helper, GWL_EX_STYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

            SetWindowPositionRelativeToScreen();

            VideoBackground.MediaOpened += VideoBackground_MediaOpened;

            //if (!Profile.Direct2DMode && Profile.VideoBackgroundFilePath is string filePath)
            //{
            //    var videoFilePath = FileUtil.GetRelativeAssetPath(Profile, filePath);
            //    LoadVideoBackground(videoFilePath);
            //}

            if (!Profile.Direct2DMode)
            {
                UpdateSkiaTimer();
            }
        }

        private void VideoBackground_MediaOpened(object sender, RoutedEventArgs e)
        {
            double videoWidth = VideoBackground.NaturalVideoWidth;
            double videoHeight = VideoBackground.NaturalVideoHeight;

            Trace.WriteLine($"Video: {videoWidth} x {videoHeight}");

            (double angle, double centerX, double centerY) = Profile.VideoBackgroundRotation switch
            {
                Enums.Rotation.Rotate90FlipNone => (90, videoHeight / 2, videoHeight / 2),
                Enums.Rotation.Rotate180FlipNone => (180, videoWidth / 2, videoHeight / 2),
                Enums.Rotation.Rotate270FlipNone => (270, videoWidth / 2, videoWidth / 2),
                _ => (0, 0, 0)
            };

            RotateTransform rotateTransform = new()
            {
                Angle = angle,
                CenterX = centerX,
                CenterY = centerY,
            };

            Trace.WriteLine($"Transform {rotateTransform.Angle} {rotateTransform.CenterX} {rotateTransform.CenterY}");

            VideoBackground.RenderTransform = rotateTransform;
        }

        private void LoadVideoBackground(string filePath)
        {
            try
            {
                VideoBackground.Visibility = Visibility.Visible;

                //stop existing video if any
                mediaClock?.Controller.Stop();

                mediaTimeline = new MediaTimeline(new Uri(filePath, UriKind.Absolute))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };

                mediaClock = mediaTimeline.CreateClock();
                VideoBackground.Clock = mediaClock;

                mediaClock.Controller.Begin();
            }
            catch (Exception ex)
            {
                // Handle exceptions
                Console.WriteLine("Error loading media: " + ex.Message);
            }
        }

        private void StopVideoBackground()
        {
            mediaClock?.Controller.Stop();
            VideoBackground.Clock = null;
            VideoBackground.Source = null;
            VideoBackground.Visibility = Visibility.Hidden;
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

