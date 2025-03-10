using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using unvell.D2DLib;
using static System.Net.Mime.MediaTypeNames;
using Profile = InfoPanel.Models.Profile;

namespace InfoPanel.Views.Common
{
    /// <summary>
    /// Interaction logic for DisplayWindow.xaml
    /// </summary>
    public partial class DisplayWindow : D2DWindow
    {
        public Profile Profile { get; }
        public bool Direct2DMode { get; }
        private readonly DispatcherTimer ResizeTimer;
        public WriteableBitmap? WriteableBitmap;

        private MediaTimeline? mediaTimeline;
        private MediaClock? mediaClock;

        public DisplayWindow(Profile profile) : base(profile.Direct2DMode)
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;

            Profile = profile;
            Width = Profile.Width;
            Height = Profile.Height;
            Direct2DMode = profile.Direct2DMode;
            ShowFps = profile.Direct2DModeFps;

            InitializeComponent();

            Topmost = profile.Topmost;

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

            WriteableBitmap = new WriteableBitmap(profile.Width, profile.Height, 96,
                  96, System.Windows.Media.PixelFormats.Bgra32, null);
            Image.Source = WriteableBitmap;

            ResizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            ResizeTimer.Tick += ResizeTimer_Tick;

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }

        protected override void OnRender(D2DGraphics d2dGraphics)
        {
            base.OnRender(d2dGraphics);

            using var g = new AcceleratedGraphics(d2dGraphics, this.Handle, Profile.Direct2DFontScale, Profile.Direct2DTextXOffset, Profile.Direct2DTextYOffset);
            PanelDraw.Run(Profile, g);
        }


        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            SetWindowPositionRelativeToScreen();
        }

        public void Fullscreen()
        {
            var screen = GetCurrentScreen();
            Profile.WindowX = 0;
            Profile.WindowY = 0;
            Profile.Width = screen.Bounds.Width;
            Profile.Height = screen.Bounds.Height;
        }

        private void ResizeTimer_Tick(object? sender, EventArgs e)
        {
            ResizeTimer.Stop();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var screen = GetCurrentScreen();
                var positionRelativeToScreen = GetWindowPositionRelativeToScreen(screen);

                Profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
                Profile.WindowX = (int)positionRelativeToScreen.X;
                Profile.WindowY = (int)positionRelativeToScreen.Y;

                Profile.Width = (int)Width;
                Profile.Height = (int)Height;
            });
        }

        private void DisplayWindow_Closed(object? sender, EventArgs e)
        {
            Profile.PropertyChanged -= Profile_PropertyChanged;
            ResizeTimer.Stop();
            ResizeTimer.Tick -= ResizeTimer_Tick;
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

        private async void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Profile.Width))
            {
                Width = Profile.Width;
                WriteableBitmap = new WriteableBitmap(Profile.Width, Profile.Height, 96,
                  96, System.Windows.Media.PixelFormats.Bgra32, null);
                Image.Source = WriteableBitmap;
            }
            else if (e.PropertyName == nameof(Profile.Height))
            {
                Height = Profile.Height;
                WriteableBitmap = new WriteableBitmap(Profile.Width, Profile.Height, 96,
                  96, System.Windows.Media.PixelFormats.Bgra32, null);
                Image.Source = WriteableBitmap;

            }
            else if (e.PropertyName == nameof(Profile.TargetWindow) || e.PropertyName == nameof(Profile.WindowX)
                || e.PropertyName == nameof(Profile.WindowY) || e.PropertyName == nameof(Profile.StrictWindowMatching))
            {
                SetWindowPositionRelativeToScreen();
            }
            else if (e.PropertyName == nameof(Profile.Topmost))
            {
                Topmost = Profile.Topmost;
            }
            else if (e.PropertyName == nameof(Profile.Resize))
            {
                if (Profile.Resize)
                {
                    ResizeMode = ResizeMode.CanResize;
                }
                else
                {
                    ResizeMode = ResizeMode.NoResize;
                }
            }
            else if (e.PropertyName == nameof(Profile.Direct2DModeFps))
            {
                ShowFps = Profile.Direct2DModeFps;
            }
            else if (e.PropertyName == nameof(Profile.VideoBackgroundFilePath) || e.PropertyName == nameof(Profile.VideoBackgroundRotation))
            {
                if (!Profile.Direct2DMode && Profile.VideoBackgroundFilePath is string filePath)
                {
                    var videoFilePath = FileUtil.GetRelativeAssetPath(Profile, filePath);
                    await LoadVideoBackground(videoFilePath);
                } else
                {
                    StopVideoBackground();
                }
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ResizeTimer.IsEnabled)
            {
                ResizeTimer.Stop();
            }

            ResizeTimer.Start();
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

        public Screen GetCurrentScreen()
        {
            // Get the current position and size of the window
            var window = this;
            var left = (int)window.Left;
            var top = (int)window.Top;
            var width = (int)window.ActualWidth;
            var height = (int)window.ActualHeight;

            // Get the position and size of each monitor
            var screens = Screen.AllScreens;

            // Check if the window is fully contained within a monitor
            foreach (var screen in screens)
            {
                if (screen.Bounds.Contains(new Rectangle(left, top, width, height)))
                {
                    // The window is fully contained within the bounds of the screen
                    // Use this screen as the target screen
                    return screen;
                }
            }

            // Check if the window overlaps with a monitor
            foreach (var screen in screens)
            {
                if (screen.WorkingArea.IntersectsWith(new Rectangle(left, top, width, height)))
                {
                    // The window overlaps with the screen
                    // Use this screen as the target screen
                    return screen;
                }
            }

            // The window is outside the bounds of all monitors
            // Use the primary screen as the default target screen
            return Screen.PrimaryScreen;
        }

        public System.Windows.Point GetWindowPositionRelativeToScreen(Screen screen)
        {
            // Get the position of the window relative to the screen in device-independent units
            var windowPositionRelativeToScreen = GetWindowPositionRelativeToScreen();

            // Get the position of the screen in device-independent units
            var source = PresentationSource.FromVisual(this);
            var transform = source.CompositionTarget.TransformFromDevice;
            var screenPosition = new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
            var screenPositionRelativeToDeviceIndependentUnits = transform.Transform(screenPosition);

            var dpi = VisualTreeHelper.GetDpi(this);

            // Subtract the position of the screen from the position of the window in device-independent units
            var windowPositionRelativeToDeviceIndependentUnits = new System.Windows.Point(
                (windowPositionRelativeToScreen.X - screenPositionRelativeToDeviceIndependentUnits.X) * dpi.DpiScaleX,
                (windowPositionRelativeToScreen.Y - screenPositionRelativeToDeviceIndependentUnits.Y) * dpi.DpiScaleY);

            return windowPositionRelativeToDeviceIndependentUnits;
        }

        public System.Windows.Point GetWindowPositionRelativeToScreen()
        {
            // Get the PresentationSource for the window
            var source = PresentationSource.FromVisual(this);

            if (source != null)
            {
                // Get the transformation matrix that maps device-independent units to device-dependent pixels
                var transform = source.CompositionTarget.TransformFromDevice;

                // Get the position of the window in device-independent units
                var windowPosition = new System.Windows.Point(this.Left, this.Top);

                // Convert the window position to device-dependent pixels relative to the screen
                var screenPosition = transform.Transform(windowPosition);

                return screenPosition;
            }
            else
            {
                return new System.Windows.Point(0, 0);
            }
        }

        private void SetWindowPositionRelativeToScreen()
        {
            Screen? screen = Screen.AllScreens.FirstOrDefault(s =>
                s.Bounds.X == Profile.TargetWindow?.X
                && s.Bounds.Y == Profile.TargetWindow?.Y
                && s.Bounds.Width == Profile.TargetWindow?.Width
                && s.Bounds.Height == Profile.TargetWindow?.Height
            );

            if (Profile.StrictWindowMatching && screen == null)
            {
                if (this.Visibility == Visibility.Visible)
                {
                    this.Hide();
                }

                return;
            }
            else
            {
                if (this.Visibility != Visibility.Visible)
                {
                    this.Show();
                }
            }

            screen ??= Screen.AllScreens.FirstOrDefault(s =>
                s.Bounds.Width == Profile.TargetWindow?.Width
                && s.Bounds.Height == Profile.TargetWindow?.Height,
                Screen.PrimaryScreen
                );

            // Get the position of the screen in device-independent units
            var source = PresentationSource.FromVisual(this);
            var transform = source.CompositionTarget.TransformFromDevice;
            var screenPosition = new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
            var relativeScreenPosition = transform.Transform(screenPosition);

            // Calculate the window position in device-independent units relative to the screen
            var relativePosition = new System.Windows.Point(relativeScreenPosition.X + Profile.WindowX, relativeScreenPosition.Y + Profile.WindowY);

            // Set the window position to the calculated position
            this.Left = relativePosition.X;
            this.Top = relativePosition.Y;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (!dragStart && SharedModel.Instance.SelectedVisibleItems != null)
                {
                    var inSelectionBounds = false;
                    foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                    {
                        var evaluatedSize = displayItem.EvaluateSize();
                        Rect bounds = displayItem.EvaluateBounds();

                        //if (displayItem is TextDisplayItem textDisplayItem && displayItem is not TableSensorDisplayItem && textDisplayItem.RightAlign)
                        //{
                        //    bounds = new Rect(textDisplayItem.X - evaluatedSize.Width, textDisplayItem.Y, evaluatedSize.Width, evaluatedSize.Height);
                        //}
                        //else
                        //{
                        //    bounds = new Rect(displayItem.X, displayItem.Y, evaluatedSize.Width, evaluatedSize.Height);
                        //}

                        if (bounds.Contains(e.GetPosition(this)))
                        {
                            inSelectionBounds = true;
                            break;
                        }
                    }

                    if (!inSelectionBounds)
                    {
                        SharedModel.Instance.SelectedItem = null;
                    }
                }

                if (SharedModel.Instance.SelectedVisibleItems == null || !SharedModel.Instance.SelectedVisibleItems.Any())
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

                        var screen = GetCurrentScreen();
                        var positionRelativeToScreen = GetWindowPositionRelativeToScreen(screen);

                        Profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
                        Profile.WindowX = (int)positionRelativeToScreen.X;
                        Profile.WindowY = (int)positionRelativeToScreen.Y;
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
                Rect clickedItemBounds = new Rect(0, 0, 0, 0);

                var displayItems = SharedModel.Instance.DisplayItems?.ToList();

                if (displayItems != null)
                {
                    foreach (var item in displayItems)
                    {
                        if (item.Hidden)
                        {
                            continue;
                        }

                        var itemBounds = item.EvaluateBounds();

                        if (itemBounds.Width >= Profile.Width && itemBounds.Height >= Profile.Height && itemBounds.X == 0 && itemBounds.Y == 0)
                        {
                            continue;
                        }

                        if (itemBounds.Contains(e.GetPosition(this)))
                        {
                            clickedItem = item;
                            clickedItemBounds = itemBounds;
                        }
                    }

                    if (clickedItem != null)
                    {
                        if (SharedModel.Instance.SelectedItem == null || !Keyboard.IsKeyDown(Key.LeftCtrl))
                        {
                            SharedModel.Instance.SelectedItem = clickedItem;

                            if (!Keyboard.IsKeyDown(Key.LeftCtrl))
                            {
                                SharedModel.Instance.SelectedItems?.ForEach(item =>
                                {
                                    if (item != clickedItem)
                                    {
                                        item.Selected = false;
                                    }
                                });
                            }
                        }

                        clickedItem.Selected = true;
                    }
                    else
                    {
                        SharedModel.Instance.SelectedItem = null;
                        SharedModel.Instance.SelectedItems?.ForEach(item =>
                        {
                            item.Selected = false;
                        });
                    }
                }
            }
        }

        bool dragStart = false;
        System.Windows.Point startPosition = new System.Windows.Point();
        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (dragStart)
            {
                var SelectedItem = SharedModel.Instance.SelectedItem;

                if (SelectedItem == null)
                {
                    dragStart = false;
                    return;
                }

                var gridSize = SharedModel.Instance.MoveValue;

                var currentPosition = e.GetPosition((UIElement)sender);

                if (SharedModel.Instance.SelectedVisibleItems != null)
                {
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

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Variable to hold the handle for the form
            var helper = new WindowInteropHelper(this).Handle;
            //Performing some magic to hide the form from Alt+Tab
            SetWindowLong(helper, GWL_EX_STYLE, (GetWindowLong(helper, GWL_EX_STYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

            SetWindowPositionRelativeToScreen();

            SizeChanged += Window_SizeChanged;

            VideoBackground.MediaOpened += VideoBackground_MediaOpened;

            if (!Profile.Direct2DMode && Profile.VideoBackgroundFilePath is string filePath)
            {
                var videoFilePath = FileUtil.GetRelativeAssetPath(Profile, filePath);
                await LoadVideoBackground(videoFilePath);
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

        private async Task LoadVideoBackground(string filePath)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    VideoBackground.Visibility = Visibility.Visible;

                    //stop existing video if any
                    mediaClock?.Controller.Stop();

                    // Load media asynchronously
                   mediaTimeline = new MediaTimeline(new Uri(filePath, UriKind.Absolute))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };

                    mediaClock = mediaTimeline.CreateClock();
                    VideoBackground.Clock = mediaClock;

                    mediaClock.Controller.Begin();
                });
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

