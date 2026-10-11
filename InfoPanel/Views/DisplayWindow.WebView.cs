using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace InfoPanel.Views.Common
{
    /// <summary>
    /// WebView2 mode for DisplayWindow: when <see cref="Models.Profile.WebViewEnabled"/> is set,
    /// the panel hosts a live web page instead of rendering the profile's display items.
    /// </summary>
    public partial class DisplayWindow
    {
        private static readonly string WebViewUserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "WebView2");

        private WebView2? _webView;
        private TextBlock? _webViewMessage;

        public bool WebView { get; }

        // Stops a browser that crashes on start from being relaunched forever.
        private const int MaxWebViewRestarts = 3;
        private int _webViewRestarts;
        private DateTime _lastWebViewRestart;
        // A browser that ran this long before crashing was stable: start the restart count afresh,
        // so occasional crashes on a panel that runs for days never add up to giving up.
        private static readonly TimeSpan WebViewStableAfter = TimeSpan.FromMinutes(10);
        // After giving up, try again this much later: the panel usually runs unattended.
        private static readonly TimeSpan WebViewRetryAfter = TimeSpan.FromMinutes(5);
        private System.Windows.Threading.DispatcherTimer? _webViewRetryTimer;

        private System.Windows.Threading.DispatcherTimer? _webViewRefreshTimer;

        /// <summary>
        /// The browser core, or null while it is starting or after its browser process crashed:
        /// once the browser is gone, reading <see cref="WebView2.CoreWebView2"/> throws until the
        /// control is recreated, and a profile change or refresh can arrive in between.
        /// </summary>
        private CoreWebView2? WebViewCore
        {
            get
            {
                try
                {
                    return _webView?.CoreWebView2;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        private void InjectWebView(Panel container)
        {
            var webView = new WebView2
            {
                // Default is next to the executable, which is not writable under Program Files.
                CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = WebViewUserDataFolder },
                DefaultBackgroundColor = ToOpaqueDrawingColor(Profile.BackgroundColor),
                // A disabled HwndHost passes mouse input to the panel window, so drag/resize keep working.
                IsEnabled = Profile.WebViewInteractive,
            };
            // Fill the window; the window itself is sized in physical pixels by MaintainPixelSize().
            webView.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = container });
            webView.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = container });

            var message = new TextBlock
            {
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            message.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = container });

            container.Children.Add(webView);
            container.Children.Add(message);

            _webView = webView;
            _webViewMessage = message;

            ApplyWebViewBackground();
        }

        private async Task InitializeWebViewAsync()
        {
            if (_webView == null) return;

            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                Logger.Error(ex, "WebView2 Runtime not found for profile {ProfileName}", Profile.Name);
                ShowWebViewMessage("Microsoft Edge WebView2 Runtime is not installed.\nDownload it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }
            catch (Exception ex)
            {
                // Closing the window while the browser is still starting disposes the control; that's not an error.
                if (_webView == null) return;

                Logger.Error(ex, "Failed to initialize WebView2 for profile {ProfileName}", Profile.Name);
                ShowWebViewMessage($"WebView2 failed to start: {ex.Message}");
                return;
            }

            // The window may have been closed while the browser was starting.
            if (WebViewCore is not CoreWebView2 core) return;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            // Keep links that open a new window inside the panel instead of spawning browser windows.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.ProcessFailed += (_, e) =>
            {
                Logger.Warning("WebView2 process failed ({Kind}) for profile {ProfileName}", e.ProcessFailedKind, Profile.Name);

                switch (e.ProcessFailedKind)
                {
                    // The control is permanently unusable once its browser process is gone; build a new one.
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        _dispatcher.BeginInvoke(RecreateWebView);
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        // A new renderer may not keep the cache setting, so apply it again before reloading.
                        _dispatcher.BeginInvoke(async () =>
                        {
                            await ApplyWebViewCacheAsync();
                            NavigateWebView();
                        });
                        break;
                }
            };

            ApplyWebViewInteractive();
            await ApplyWebViewCacheAsync();
            NavigateWebView();
            UpdateWebViewAutoRefresh();
        }

        /// <summary>
        /// Turns the browser cache off (or back on) for this panel's page only, so polled data and
        /// assets that are replaced in place are always fetched fresh.
        /// </summary>
        private async Task ApplyWebViewCacheAsync()
        {
            if (WebViewCore is not CoreWebView2 core) return;

            try
            {
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled",
                    Profile.WebViewDisableCache ? "{\"cacheDisabled\":true}" : "{\"cacheDisabled\":false}");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to apply web page cache setting for profile {ProfileName}", Profile.Name);
            }
        }

        /// <summary>Starts, changes or stops the periodic hard refresh to match the profile setting.</summary>
        private void UpdateWebViewAutoRefresh()
        {
            var seconds = Profile.WebViewAutoRefreshSeconds;
            if (seconds <= 0 || _webView == null)
            {
                _webViewRefreshTimer?.Stop();
                return;
            }

            if (_webViewRefreshTimer == null)
            {
                _webViewRefreshTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _dispatcher);
                _webViewRefreshTimer.Tick += (_, _) =>
                {
                    // Don't reset the page while the user may be working in it.
                    if (!Profile.WebViewInteractive) RefreshWebView();
                };
            }

            _webViewRefreshTimer.Interval = TimeSpan.FromSeconds(seconds);
            _webViewRefreshTimer.Start();
        }

        private void NavigateWebView()
        {
            if (WebViewCore is not CoreWebView2 core) return;

            var url = Profile.WebViewUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowWebViewMessage("No URL set. Enter one in the profile's web page settings.");
                return;
            }

            try
            {
                core.Navigate(NormalizeWebViewUrl(url));
                HideWebViewMessage();
            }
            catch (ArgumentException ex)
            {
                Logger.Warning(ex, "Invalid web page URL {Url} for profile {ProfileName}", url, Profile.Name);
                ShowWebViewMessage($"Invalid URL: {url}");
            }
        }

        /// <summary>Hard refresh: reloads the current page bypassing the cache.</summary>
        public void RefreshWebView()
        {
            _dispatcher.BeginInvoke(async () =>
            {
                if (_webView == null) return;

                if (WebViewCore is not CoreWebView2 core)
                {
                    // Only start over once recovery has given up; otherwise the browser is still starting.
                    if (_webViewRestarts > MaxWebViewRestarts)
                    {
                        _webViewRestarts = 0;
                        RecreateWebView();
                    }
                    return;
                }

                // A message is showing (no URL or an invalid one), so there's no page to reload yet.
                if (_webViewMessage?.Visibility == Visibility.Visible)
                {
                    NavigateWebView();
                    return;
                }

                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Hard reload failed for profile {ProfileName}; falling back to a normal reload", Profile.Name);
                    core.Reload();
                }
            });
        }

        private void OnWebViewProfilePropertyChanged(string? propertyName)
        {
            switch (propertyName)
            {
                case nameof(Profile.WebViewUrl):
                    _dispatcher.BeginInvoke(NavigateWebView);
                    break;
                case nameof(Profile.WebViewInteractive):
                    _dispatcher.BeginInvoke(ApplyWebViewInteractive);
                    break;
                case nameof(Profile.BackgroundColor):
                    _dispatcher.BeginInvoke(ApplyWebViewBackground);
                    break;
                case nameof(Profile.WebViewDisableCache):
                    _dispatcher.BeginInvoke(ApplyWebViewCacheAsync);
                    break;
                case nameof(Profile.WebViewAutoRefreshSeconds):
                    _dispatcher.BeginInvoke(UpdateWebViewAutoRefresh);
                    break;
            }
        }

        /// <summary>
        /// Display-only mode disables the WebView2 host window: Windows then routes mouse input to the
        /// panel window, so the existing drag and resize behaviour keeps working.
        /// </summary>
        private void ApplyWebViewInteractive()
        {
            if (_webView == null) return;

            _webView.IsEnabled = Profile.WebViewInteractive;
            // HwndHost normally forwards IsEnabled to the native window; set it explicitly as well.
            if (_webView.Handle != IntPtr.Zero)
            {
                EnableWindow(_webView.Handle, Profile.WebViewInteractive);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        private void ApplyWebViewBackground()
        {
            var color = ToOpaqueDrawingColor(Profile.BackgroundColor);
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));

            if (_webView != null) _webView.DefaultBackgroundColor = color;
            if (_webViewMessage != null)
            {
                // Pick black or white text depending on background brightness.
                var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
                _webViewMessage.Foreground = luminance > 128 ? Brushes.Black : Brushes.White;
            }
        }

        private void ShowWebViewMessage(string text)
        {
            if (_webViewMessage == null) return;
            _webViewMessage.Text = text;
            _webViewMessage.Visibility = Visibility.Visible;
            // The WebView2 HWND always paints over WPF content, so hide it while showing a message.
            if (_webView != null) _webView.Visibility = Visibility.Collapsed;
        }

        private void HideWebViewMessage()
        {
            if (_webViewMessage != null) _webViewMessage.Visibility = Visibility.Collapsed;
            if (_webView != null) _webView.Visibility = Visibility.Visible;
        }

        private void RecreateWebView()
        {
            // Null means the window is closing.
            if (_webView == null || FindName("SkiaContainer") is not Panel container) return;

            DisposeWebView();
            container.Children.Clear();
            InjectWebView(container);

            if (DateTime.UtcNow - _lastWebViewRestart > WebViewStableAfter) _webViewRestarts = 0;
            _lastWebViewRestart = DateTime.UtcNow;

            if (++_webViewRestarts > MaxWebViewRestarts)
            {
                Logger.Error("WebView2 browser exited {Count} times for profile {ProfileName}; retrying in {Delay}",
                    _webViewRestarts, Profile.Name, WebViewRetryAfter);
                ShowWebViewMessage($"The web page keeps crashing. Retrying in {WebViewRetryAfter.TotalMinutes:0} minutes.");
                ScheduleWebViewRetry();
                return;
            }

            _ = InitializeWebViewAsync();
        }

        /// <summary>One more attempt after giving up, with a fresh restart count.</summary>
        private void ScheduleWebViewRetry()
        {
            if (_webViewRetryTimer == null)
            {
                _webViewRetryTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _dispatcher)
                {
                    Interval = WebViewRetryAfter,
                };
                _webViewRetryTimer.Tick += (_, _) =>
                {
                    _webViewRetryTimer.Stop();
                    _webViewRestarts = 0;
                    _lastWebViewRestart = DateTime.MinValue;
                    RecreateWebView();
                };
            }

            _webViewRetryTimer.Stop();
            _webViewRetryTimer.Start();
        }

        private void DisposeWebView()
        {
            _webViewRefreshTimer?.Stop();
            _webViewRetryTimer?.Stop();
            if (_webView == null) return;

            BindingOperations.ClearBinding(_webView, WidthProperty);
            BindingOperations.ClearBinding(_webView, HeightProperty);
            // Releases the Edge browser processes immediately rather than at GC.
            _webView.Dispose();
            _webView = null;
            _webViewMessage = null;
        }

        /// <summary>
        /// Accepts full URLs, local file paths and bare hosts ("example.com", "localhost:8080").
        /// </summary>
        internal static string NormalizeWebViewUrl(string url)
        {
            url = url.Trim();

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                    || uri.Scheme == Uri.UriSchemeFile || uri.Scheme == "about" || uri.Scheme == "data"))
            {
                return uri.AbsoluteUri;
            }

            return "https://" + url;
        }

        /// <summary>WebView2 only accepts fully opaque or fully transparent background colours; the panel window is opaque.</summary>
        private static System.Drawing.Color ToOpaqueDrawingColor(string? hex)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex) && ColorConverter.ConvertFromString(hex) is System.Windows.Media.Color c)
                {
                    return System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
                }
            }
            catch (FormatException)
            {
            }

            return System.Drawing.Color.Black;
        }
    }
}
