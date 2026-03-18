using AsyncKeyedLock;
using InfoPanel.Drawing;
using InfoPanel.Models;
using Serilog;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfoPanel.Services
{
    public sealed class ProfilePreviewCoordinator
    {
        private static readonly Lazy<ProfilePreviewCoordinator> _instance = new(() => new ProfilePreviewCoordinator());
        public static ProfilePreviewCoordinator Instance => _instance.Value;

        private static readonly ILogger Logger = Log.ForContext<ProfilePreviewCoordinator>();

        private const int TickIntervalMs = 500;
        private const int BatchSize = 3;

        private readonly Dictionary<Guid, ProfileEntry> _entries = [];
        private readonly object _entriesLock = new();
        private DispatcherTimer? _timer;
        private int _currentIndex;

        private ProfilePreviewCoordinator() { }

        public void Register(Profile profile, SKElement skElement, ScrollViewer? scrollViewer)
        {
            lock (_entriesLock)
            {
                _entries[profile.Guid] = new ProfileEntry(profile, skElement, scrollViewer);

                if (_timer == null)
                {
                    _timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(TickIntervalMs)
                    };
                    _timer.Tick += Timer_Tick;
                    _timer.Start();
                    Logger.Debug("ProfilePreviewCoordinator timer started");
                }
            }

            // Do an immediate first render for this profile
            _ = RenderEntryAsync(new ProfileEntry(profile, skElement, scrollViewer));
        }

        public void Unregister(Profile profile)
        {
            lock (_entriesLock)
            {
                _entries.Remove(profile.Guid);

                if (_entries.Count == 0 && _timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= Timer_Tick;
                    _timer = null;
                    _currentIndex = 0;
                    Logger.Debug("ProfilePreviewCoordinator timer stopped");
                }
            }
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            List<ProfileEntry> batch;
            List<Profile> allProfiles;

            lock (_entriesLock)
            {
                if (_entries.Count == 0) return;

                var entries = new List<ProfileEntry>(_entries.Values);
                allProfiles = entries.Select(e => e.Profile).ToList();
                batch = [];

                for (int i = 0; i < BatchSize && i < entries.Count; i++)
                {
                    var index = (_currentIndex + i) % entries.Count;
                    batch.Add(entries[index]);
                }

                _currentIndex = (_currentIndex + BatchSize) % entries.Count;
            }

            // Keep outer image cache alive for all registered profiles,
            // not just the batch being rendered this tick
            TouchProfileImages(allProfiles);

            foreach (var entry in batch)
            {
                await RenderEntryAsync(entry);
            }
        }

        private static void TouchProfileImages(List<Profile> profiles)
        {
            foreach (var profile in profiles)
            {
                foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
                {
                    TouchDisplayItemImages(displayItem);
                }
            }
        }

        private static void TouchDisplayItemImages(DisplayItem displayItem)
        {
            switch (displayItem)
            {
                case GroupDisplayItem groupDisplayItem:
                    foreach (var child in groupDisplayItem.DisplayItemsCopy)
                    {
                        TouchDisplayItemImages(child);
                    }
                    break;
                case ImageDisplayItem imageDisplayItem:
                    Cache.TouchImage(imageDisplayItem);
                    break;
                case GaugeDisplayItem gaugeDisplayItem:
                    gaugeDisplayItem.EvaluateImageFrame(out var gaugeImgA, out var gaugeImgB, out _);
                    if (gaugeImgA != null) Cache.TouchImage(gaugeImgA);
                    if (gaugeImgB != null) Cache.TouchImage(gaugeImgB);
                    break;
            }
        }

        private static async Task RenderEntryAsync(ProfileEntry entry)
        {
            try
            {
                if (!IsElementVisible(entry.SkElement, entry.ScrollViewer))
                    return;

                using var _ = await entry.RenderLock.LockAsync();

                var profile = entry.Profile;
                var skElement = entry.SkElement;

                var canvasWidth = skElement.CanvasSize.Width;
                var canvasHeight = skElement.CanvasSize.Height;

                if (canvasWidth <= 0 || canvasHeight <= 0) return;

                var scale = 1.0f;

                if (profile.Height > canvasHeight)
                {
                    scale = canvasHeight / profile.Height;
                }

                if (profile.Width > canvasWidth)
                {
                    scale = Math.Min(scale, canvasWidth / profile.Width);
                }

                var width = (int)(profile.Width * scale);
                var height = (int)(profile.Height * scale);

                if (width <= 0 || height <= 0) return;

                if (profile.PreviewBitmap != null && (profile.PreviewBitmap.Width != width || profile.PreviewBitmap.Height != height))
                {
                    profile.PreviewBitmap.Dispose();
                    profile.PreviewBitmap = null;
                }

                profile.PreviewBitmap ??= new SKBitmap(width, height);

                await Task.Run(() =>
                {
                    using var g = SkiaGraphics.FromBitmap(profile.PreviewBitmap, profile.FontScale);
                    PanelDraw.Run(profile, g, true, scale, false, $"PREVIEW-{profile.Guid}");
                });

                entry.PaintCompletionSource = new TaskCompletionSource<bool>();
                skElement.InvalidateVisual();
                await entry.PaintCompletionSource.Task;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error rendering preview for profile {ProfileGuid}", entry.Profile.Guid);
            }
        }

        public void CompletePaint(Profile profile)
        {
            lock (_entriesLock)
            {
                if (_entries.TryGetValue(profile.Guid, out var entry))
                {
                    entry.PaintCompletionSource?.TrySetResult(true);
                }
            }
        }

        private static bool IsElementVisible(SKElement element, ScrollViewer? scrollViewer)
        {
            if (scrollViewer == null || !element.IsVisible)
                return element.IsVisible;

            try
            {
                var transform = element.TransformToAncestor(scrollViewer);
                var elementBounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var viewportBounds = new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight);
                return elementBounds.IntersectsWith(viewportBounds);
            }
            catch
            {
                return true; // If transform fails, render anyway
            }
        }

        private sealed class ProfileEntry(Profile profile, SKElement skElement, ScrollViewer? scrollViewer)
        {
            public Profile Profile { get; } = profile;
            public SKElement SkElement { get; } = skElement;
            public ScrollViewer? ScrollViewer { get; } = scrollViewer;
            public AsyncNonKeyedLocker RenderLock { get; } = new(1);
            public TaskCompletionSource<bool>? PaintCompletionSource { get; set; }
        }
    }
}
