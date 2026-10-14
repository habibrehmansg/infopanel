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

        // All panels live on the DisplayWindowThread, so these statics are only touched from that thread.
        // Web page panels share one environment, created on first use. It is dropped when the last panel
        // closes or its browser process dies, so a new panel never starts on a browser that is going away.
        private static Task<CoreWebView2Environment>? _webViewEnvironment;
        private static int _webViewCount;
        private string? _lastWebViewInitError;

        private WebView2? _webView;
        private TextBlock? _webViewMessage;
        // The core whose events are hooked, kept so they can be unhooked before the control is disposed.
        private CoreWebView2? _hookedWebViewCore;
        private Task<CoreWebView2Environment>? _webViewEnvironmentInUse;
        // Whether the DevTools Network domain is on; only the disable cache option needs it.
        private bool _webViewNetworkEnabled;

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

        // WebView2 reports a stuck renderer every few seconds. Only reload once it has been stuck this long,
        // so a busy system or a slow script isn't punished; a longer gap between reports means a new stall.
        private static readonly TimeSpan WebViewUnresponsiveReloadAfter = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan WebViewUnresponsiveGap = TimeSpan.FromSeconds(15);
        private DateTime _webViewUnresponsiveSince;
        private DateTime _lastWebViewUnresponsive;

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
            // The environment, and with it the user data folder, is passed in by InitializeWebViewAsync.
            var webView = new WebView2
            {
                DefaultBackgroundColor = ToOpaqueDrawingColor(Profile.BackgroundColor),
                // A disabled HwndHost passes mouse input to the panel window, so drag/resize keep working.
                IsEnabled = Profile.WebViewInteractive,
            };
            // Fill the window; the window itself is sized in physical pixels by MaintainPixelSize().
            webView.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = container });
            webView.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = container });
            webView.IsVisibleChanged += WebView_IsVisibleChanged;

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
            _webViewCount++;

            ApplyWebViewBackground();
        }

        private static Task<CoreWebView2Environment> GetWebViewEnvironment()
        {
            // A failed creation (no runtime yet, say) isn't kept, so the next attempt tries again.
            if (_webViewEnvironment is not { IsFaulted: false, IsCanceled: false })
            {
                // The default user data folder is next to the executable, which is not writable under Program Files.
                _webViewEnvironment = CoreWebView2Environment.CreateAsync(null, WebViewUserDataFolder);
            }
            return _webViewEnvironment;
        }

        /// <summary>Stops new panels using this panel's environment once its browser process has failed.</summary>
        private void ForgetWebViewEnvironment()
        {
            if (_webViewEnvironmentInUse != null && _webViewEnvironment == _webViewEnvironmentInUse)
            {
                _webViewEnvironment = null;
            }
        }

        private async Task InitializeWebViewAsync()
        {
            if (_webView is not WebView2 webView) return;

            try
            {
                _webViewEnvironmentInUse = GetWebViewEnvironment();
                await webView.EnsureCoreWebView2Async(await _webViewEnvironmentInUse);
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                if (_webView != webView) return;

                Logger.Error(ex, "WebView2 Runtime not found for profile {ProfileName}", Profile.Name);
                ShowWebViewMessage("Microsoft Edge WebView2 Runtime is not installed.\nDownload it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }
            catch (Exception ex)
            {
                // Closing the window or recovering while the browser is still starting disposes the control; that's not an error.
                if (_webView != webView) return;

                Logger.Error(ex, "Failed to initialize WebView2 for profile {ProfileName}", Profile.Name);
                _lastWebViewInitError = ex.Message;
                ForgetWebViewEnvironment();
                RecoverWebView(recreateControl: true);
                return;
            }

            // The window may have been closed or the control replaced while the browser was starting,
            // and a refresh may have started this control twice.
            if (_webView != webView || _hookedWebViewCore != null || WebViewCore is not CoreWebView2 core) return;

            _lastWebViewInitError = null;

            try
            {
                ConfigureWebViewCore(core);
                ApplyWebViewInteractive();
                ApplyWebViewMemoryTarget();
                await ApplyWebViewCacheAsync();
                NavigateWebView();
                UpdateWebViewAutoRefresh();
            }
            catch (Exception ex)
            {
                // The browser can die while this runs; ProcessFailed then takes care of recovery.
                Logger.Error(ex, "Failed to set up WebView2 for profile {ProfileName}", Profile.Name);
            }
        }

        private void ConfigureWebViewCore(CoreWebView2 core)
        {
            var settings = core.Settings;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            // The page never talks to InfoPanel, so close the bridges a page could use to reach the host.
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;
            // Nothing typed into a panel should be offered for saving or remembered.
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            // Hook ProcessFailed first, so a crash during the rest of start-up is still recovered.
            core.ProcessFailed += WebView_ProcessFailed;
            core.NavigationStarting += WebView_NavigationStarting;
            core.NewWindowRequested += WebView_NewWindowRequested;
            core.PermissionRequested += WebView_PermissionRequested;
            core.DownloadStarting += WebView_DownloadStarting;
            _hookedWebViewCore = core;
        }

        private void UnhookWebViewCore()
        {
            if (_hookedWebViewCore is not CoreWebView2 core) return;
            _hookedWebViewCore = null;

            try
            {
                core.ProcessFailed -= WebView_ProcessFailed;
                core.NavigationStarting -= WebView_NavigationStarting;
                core.NewWindowRequested -= WebView_NewWindowRequested;
                core.PermissionRequested -= WebView_PermissionRequested;
                core.DownloadStarting -= WebView_DownloadStarting;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // The browser is already gone, and its handlers went with it.
            }
        }

        private void WebView_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            var failedCore = sender as CoreWebView2;

            // Failures often arrive in pairs (renderer then browser); only the first may act on this control,
            // so a recovery queued for a control that has since been replaced is dropped.
            void Recover(bool recreateControl) => _dispatcher.BeginInvoke(() =>
            {
                if (failedCore != null && !ReferenceEquals(_hookedWebViewCore, failedCore)) return;
                RecoverWebView(recreateControl);
            });

            switch (e.ProcessFailedKind)
            {
                // The control is permanently unusable once its browser process is gone, and so may be its
                // environment: build new ones, outside this event.
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    Logger.Warning("WebView2 browser process exited ({Reason}, exit code {ExitCode}) for profile {ProfileName}",
                        e.Reason, e.ExitCode, Profile.Name);
                    ForgetWebViewEnvironment();
                    Recover(recreateControl: true);
                    break;
                // WebView2 starts a new renderer showing an error page; reload ours.
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    Logger.Warning("WebView2 render process exited ({Reason}, exit code {ExitCode}) for profile {ProfileName}",
                        e.Reason, e.ExitCode, Profile.Name);
                    Recover(recreateControl: false);
                    break;
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    var now = DateTime.UtcNow;
                    if (now - _lastWebViewUnresponsive > WebViewUnresponsiveGap) _webViewUnresponsiveSince = now;
                    _lastWebViewUnresponsive = now;

                    if (now - _webViewUnresponsiveSince < WebViewUnresponsiveReloadAfter)
                    {
                        Logger.Debug("WebView2 render process unresponsive for profile {ProfileName}", Profile.Name);
                        break;
                    }

                    Logger.Warning("WebView2 render process unresponsive for {Duration} for profile {ProfileName}; reloading",
                        now - _webViewUnresponsiveSince, Profile.Name);
                    _lastWebViewUnresponsive = DateTime.MinValue;
                    Recover(recreateControl: false);
                    break;
                // GPU, utility and subframe processes are restarted by WebView2 itself; the page carries on.
                default:
                    Logger.Warning("WebView2 process failed ({Kind}, {Reason}, exit code {ExitCode}) for profile {ProfileName}",
                        e.ProcessFailedKind, e.Reason, e.ExitCode, Profile.Name);
                    break;
            }
        }

        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Web pages and local files only: other schemes hand the URL to an external app.
            if (IsAllowedWebViewUri(e.Uri)) return;

            Logger.Warning("Blocked navigation to {Url} for profile {ProfileName}", e.Uri, Profile.Name);
            e.Cancel = true;
        }

        private void WebView_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Never spawn browser windows from a panel. A link the user clicked opens in the panel instead;
            // pop-ups the page opens by itself are dropped, so they can't take over an unattended panel.
            e.Handled = true;

            if (!e.IsUserInitiated || sender is not CoreWebView2 core)
            {
                Logger.Debug("Blocked pop-up {Url} for profile {ProfileName}", e.Uri, Profile.Name);
                return;
            }

            try
            {
                core.Navigate(e.Uri);
            }
            catch (ArgumentException ex)
            {
                Logger.Warning(ex, "Invalid link {Url} for profile {ProfileName}", e.Uri, Profile.Name);
            }
        }

        private void WebView_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // Media autoplay is what a dashboard page expects; it never prompts in a normal browser either.
            if (e.PermissionKind == CoreWebView2PermissionKind.Autoplay)
            {
                e.State = CoreWebView2PermissionState.Allow;
                return;
            }

            // Nobody is there to answer a prompt on a panel, so camera, location, notifications and the like are refused.
            e.State = CoreWebView2PermissionState.Deny;
            Logger.Debug("Denied {Permission} permission to {Url} for profile {ProfileName}", e.PermissionKind, e.Uri, Profile.Name);
        }

        private void WebView_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // A panel shows a page; it shouldn't save files on the page's say-so.
            e.Cancel = true;
            Logger.Information("Blocked download of {Url} for profile {ProfileName}", e.DownloadOperation.Uri, Profile.Name);
        }

        private void WebView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ApplyWebViewMemoryTarget();
            UpdateWebViewAutoRefresh();
        }

        /// <summary>
        /// Lets WebView2 trim the page's memory while it is off screen (its monitor is gone, or a message shows
        /// instead). Scripts keep running, unlike suspending it, so the page is up to date when it comes back.
        /// </summary>
        private void ApplyWebViewMemoryTarget()
        {
            if (_webView is not WebView2 webView || WebViewCore is not CoreWebView2 core) return;

            try
            {
                core.MemoryUsageTargetLevel = webView.IsVisible
                    ? CoreWebView2MemoryUsageTargetLevel.Normal
                    : CoreWebView2MemoryUsageTargetLevel.Low;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                Logger.Debug(ex, "Failed to set WebView2 memory target for profile {ProfileName}", Profile.Name);
            }
        }

        /// <summary>
        /// Turns the browser cache off (or back on) for this panel's page only, so polled data and
        /// assets that are replaced in place are always fetched fresh.
        /// </summary>
        private async Task ApplyWebViewCacheAsync()
        {
            if (WebViewCore is not CoreWebView2 core) return;
            // With the cache left on, the Network domain is never enabled and there is nothing to undo.
            if (!Profile.WebViewDisableCache && !_webViewNetworkEnabled) return;

            try
            {
                if (Profile.WebViewDisableCache)
                {
                    // setCacheDisabled only applies while the Network domain is enabled. Enabled, DevTools also keeps
                    // a copy of every response body for a client that never reads them, so give it no room for those.
                    await core.CallDevToolsProtocolMethodAsync("Network.enable", "{\"maxTotalBufferSize\":0,\"maxResourceBufferSize\":0}");
                    _webViewNetworkEnabled = true;
                    await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
                }
                else
                {
                    await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":false}");
                    await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
                    _webViewNetworkEnabled = false;
                }
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
            // Nothing to refresh while the page is off screen or a message shows instead.
            if (seconds <= 0 || _webView?.IsVisible != true)
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

            var interval = TimeSpan.FromSeconds(seconds);
            // Setting the same interval would restart the countdown on every visibility change.
            if (_webViewRefreshTimer.IsEnabled && _webViewRefreshTimer.Interval == interval) return;

            _webViewRefreshTimer.Interval = interval;
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
                try
                {
                    await RefreshWebViewAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to refresh web page for profile {ProfileName}", Profile.Name);
                }
            });
        }

        private async Task RefreshWebViewAsync()
        {
            if (_webView == null) return;

            // A message is showing instead of a page (no URL, a failure, or recovery waiting to retry): start over.
            if (_webViewMessage?.Visibility == Visibility.Visible)
            {
                ResetWebViewRestarts();
                if (WebViewCore == null)
                {
                    RecoverWebView(recreateControl: true);
                }
                else
                {
                    NavigateWebView();
                }
                return;
            }

            // Otherwise a missing core means the browser is still starting.
            if (WebViewCore is not CoreWebView2 core) return;

            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Hard reload failed for profile {ProfileName}; falling back to a normal reload", Profile.Name);
                core.Reload();
            }
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

        /// <summary>
        /// Brings the page back after a failure: reloads it, or first builds a new control when the browser
        /// itself is gone. The first attempt is immediate, later ones back off, and after too many in a row
        /// recovery pauses for <see cref="WebViewRetryAfter"/>, so a page or browser that dies on start is
        /// never relaunched in a tight loop.
        /// </summary>
        private void RecoverWebView(bool recreateControl)
        {
            // Null means the window is closing.
            if (_webView == null || FindName("SkiaContainer") is not Panel container) return;

            if (recreateControl)
            {
                DisposeWebView();
                container.Children.Clear();
                InjectWebView(container);
            }

            var now = DateTime.UtcNow;
            if (now - _lastWebViewRestart > WebViewStableAfter) _webViewRestarts = 0;
            _lastWebViewRestart = now;

            if (++_webViewRestarts > MaxWebViewRestarts)
            {
                Logger.Error("WebView2 failed {Count} times for profile {ProfileName}; retrying in {Delay}",
                    _webViewRestarts, Profile.Name, WebViewRetryAfter);
                ShowWebViewMessage(_lastWebViewInitError is string error
                    ? $"The web page failed to start: {error}\nRetrying in {WebViewRetryAfter.TotalMinutes:0} minutes."
                    : $"The web page keeps crashing. Retrying in {WebViewRetryAfter.TotalMinutes:0} minutes.");
                ScheduleWebViewRetry(WebViewRetryAfter);
                return;
            }

            if (_webViewRestarts == 1)
            {
                RetryWebView();
            }
            else
            {
                // 2s, then 4s.
                ScheduleWebViewRetry(TimeSpan.FromSeconds(1 << (_webViewRestarts - 1)));
            }
        }

        /// <summary>Starts the browser on a new control, or reloads the page in the existing one.</summary>
        private void RetryWebView()
        {
            if (_webView == null) return;

            if (WebViewCore == null)
            {
                _ = InitializeWebViewAsync();
            }
            else
            {
                _ = ReloadWebViewAsync();
            }
        }

        private async Task ReloadWebViewAsync()
        {
            try
            {
                // A new renderer may not keep the cache setting, so apply it again before reloading.
                await ApplyWebViewCacheAsync();
                NavigateWebView();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to reload web page for profile {ProfileName}", Profile.Name);
            }
        }

        private void ResetWebViewRestarts()
        {
            _webViewRetryTimer?.Stop();
            _webViewRestarts = 0;
            _lastWebViewRestart = DateTime.MinValue;
        }

        private void ScheduleWebViewRetry(TimeSpan delay)
        {
            if (_webViewRetryTimer == null)
            {
                _webViewRetryTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _dispatcher);
                _webViewRetryTimer.Tick += (_, _) =>
                {
                    _webViewRetryTimer.Stop();
                    // After giving up, the next attempt starts with a fresh restart count.
                    if (_webViewRestarts > MaxWebViewRestarts) ResetWebViewRestarts();
                    RetryWebView();
                };
            }

            _webViewRetryTimer.Stop();
            _webViewRetryTimer.Interval = delay;
            _webViewRetryTimer.Start();
        }

        private void DisposeWebView()
        {
            _webViewRefreshTimer?.Stop();
            _webViewRetryTimer?.Stop();
            if (_webView == null) return;

            UnhookWebViewCore();
            _webView.IsVisibleChanged -= WebView_IsVisibleChanged;
            BindingOperations.ClearBinding(_webView, WidthProperty);
            BindingOperations.ClearBinding(_webView, HeightProperty);
            try
            {
                // Releases the Edge browser processes immediately rather than at GC.
                _webView.Dispose();
            }
            finally
            {
                _webView = null;
                _webViewMessage = null;
                _webViewNetworkEnabled = false;

                // With no web page panel left, let the browser go; the next one creates a fresh environment.
                if (--_webViewCount == 0) _webViewEnvironment = null;
            }
        }

        /// <summary>
        /// Accepts full URLs, local file paths and bare hosts ("example.com", "localhost:8080").
        /// </summary>
        internal static string NormalizeWebViewUrl(string url)
        {
            url = url.Trim();

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedWebViewScheme(uri))
            {
                return uri.AbsoluteUri;
            }

            return "https://" + url;
        }

        private static bool IsAllowedWebViewUri(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedWebViewScheme(uri);

        private static bool IsAllowedWebViewScheme(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeFile || uri.Scheme == "about" || uri.Scheme == "data";

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
