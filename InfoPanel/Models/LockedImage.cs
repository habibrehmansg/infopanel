using InfoPanel.Extensions;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        public enum ImageType
        {
            SK, SVG, FFMPEG
        }

        public readonly string ImagePath;
        private readonly ConcurrentDictionary<Guid, ImageDisplayItem> imageDisplayItems = [];

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKImageMemoryCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKGLImageMemoryCache = new();

        public int Width { get; private set; } = 0;
        public int Height { get; private set; } = 0;

        public readonly ImageType Type;

        private readonly SKSvg? SKSvg;
        private readonly BackgroundVideoPlayer? _backgroundVideoPlayer;

        public TimeSpan? CurrentTime => _backgroundVideoPlayer?.GetCurrentTime();
        public TimeSpan? Duration => _backgroundVideoPlayer?.Duration;
        public double? FrameRate => _backgroundVideoPlayer?.FrameRate;

        public bool HasAudio => _backgroundVideoPlayer?.HasAudio ?? false;

        public PlayerStatus? VideoPlayerStatus => _backgroundVideoPlayer?.Status;

        public float Volume
        {
            get { return _backgroundVideoPlayer?.Volume * 100 ?? 0; }
            set
            {
                if (value >= 0 && _backgroundVideoPlayer != null)
                {
                    if (_backgroundVideoPlayer.Volume != value)
                    {
                        _backgroundVideoPlayer.Volume = value;
                    }
                }
            }
        }

        public readonly long Frames;
        public readonly long TotalFrameTime;

        private readonly SKCodec? _codec;
        private readonly Stream? _stream;
        private SKBitmap? _compositeBitmap;
        private readonly long[]? _cumulativeFrameTimes;
        private int _lastRenderedFrame = -1;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new();

        public bool Loaded { get; private set; } = false;

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;

            try
            {
                var uri = new UriBuilder(imagePath) { Query = "" };
                var strippedUrl = uri.Uri.ToString();
                if (strippedUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ImagePath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                        && !ImagePath.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ImagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            && !ImagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            && !File.Exists(ImagePath))
                        {
                            throw new ArgumentException("Video file does not exist.", nameof(imagePath));
                        }
                    }

                    try
                    {
                        Type = ImageType.FFMPEG;
                        _backgroundVideoPlayer = new BackgroundVideoPlayer(ImagePath);
                        Width = _backgroundVideoPlayer.Width;
                        Height = _backgroundVideoPlayer.Height;
                        Frames = _backgroundVideoPlayer.TotalFrames;
                        TotalFrameTime = (long)_backgroundVideoPlayer.Duration.TotalMilliseconds;

                        Loaded = true;
                        return;
                    }
                    catch (Exception e)
                    {
                        throw new ArgumentException($"Error initializing video player: {e.Message}");
                    }
                }
                else if (ImagePath.IsUrl())
                {
                    using HttpClient client = new();
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");

                    try
                    {
                        var data = client.GetByteArrayAsync(ImagePath).GetAwaiter().GetResult();
                        _stream = new MemoryStream(data);
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine(e.Message);
                    }
                }
                else if (File.Exists(ImagePath))
                {
                    try
                    {
                        var fileStream = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        _stream = new MemoryStream();
                        fileStream.CopyTo(_stream);
                        fileStream.Dispose();
                        _stream.Position = 0;

                        Trace.WriteLine($"Image loaded from file: {ImagePath}");
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine(e.Message);
                    }
                }


                if (_stream == null)
                {
                    throw new ArgumentException("Image path is invalid or file does not exist.", nameof(imagePath));
                }

                if (IsSvgContent(_stream))
                {
                    Type = ImageType.SVG;

                    SKSvg = new SKSvg();
                    SKSvg.Load(_stream);

                    if (SKSvg.Picture is SKPicture picture)
                    {
                        Width = (int)picture.CullRect.Width;
                        Height = (int)picture.CullRect.Height;
                        Frames = 1;
                    }
                }
                else
                {
                    _codec?.Dispose();
                    _codec = SKCodec.Create(_stream);

                    if (_codec == null)
                    {
                        Trace.WriteLine($"Failed to create SKCodec for {ImagePath}");
                        throw new ArgumentException("Unsupported image format or codec creation failed.", nameof(imagePath));
                    }

                    Width = _codec.Info.Width;
                    Height = _codec.Info.Height;

                    Frames = _codec.FrameCount;

                    //ensure at least 1 frame
                    if (Frames == 0)
                    {
                        Frames = 1;
                    }

                    _cumulativeFrameTimes = new long[Frames];

                    if (Frames > 1)
                    {
                        for (int i = 0; i < Frames; i++)
                        {
                            var frameDelay = _codec.FrameInfo[i].Duration;

                            if (frameDelay == 0)
                            {
                                frameDelay = 100;
                            }

                            TotalFrameTime += frameDelay;
                            _cumulativeFrameTimes[i] = TotalFrameTime;
                        }

                        //start the stopwatch
                        Stopwatch.Start();
                    }
                }

                Loaded = true;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Error initializing LockedImage: {e.Message}");
            }
        }

        public void AddImageDisplayItem(ImageDisplayItem item)
        {
            if (imageDisplayItems.TryAdd(item.Guid, item))
            {

                item.Profile.PropertyChanged += Profile_PropertyChanged;
                item.PropertyChanged += ImageDisplayItem_PropertyChanged;

                UpdateVolume();
            }
        }

        public void RemoveImageDisplayItem(ImageDisplayItem item)
        {
          if(imageDisplayItems.TryRemove(item.Guid, out _))
            {
                item.Profile.PropertyChanged -= Profile_PropertyChanged;
                item.PropertyChanged -= ImageDisplayItem_PropertyChanged;

                UpdateVolume();
            }
        }

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Profile.Active):
                    UpdateVolume();
                    break;
            }
        }

        private void ImageDisplayItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ImageDisplayItem.Volume):
                case nameof(ImageDisplayItem.Hidden):
                    UpdateVolume();
                    break;
            }
        }

        private void UpdateVolume()
        {
            int volume = 0;
            foreach (var item in imageDisplayItems.Values)
            {
                if(item.Profile.Active && !item.Hidden && item.Volume > volume)
                {
                   volume = item.Volume;
                }
            }

            Volume = volume / 100f;
        }

        private static bool IsSvgContent(Stream stream)
        {
            if (stream.Length < 512)
            {
                return false;
            }

            var buffer = new byte[512];
            stream.Read(buffer, 0, buffer.Length);
            stream.Position = 0;

            // Check for SVG markers in the first bytes
            var text = Encoding.UTF8.GetString(buffer);
            return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("svg", StringComparison.OrdinalIgnoreCase);
        }

        private SKBitmap? GetSKBitmapFromSK(int frame)
        {
            if (_stream != null && _codec != null)
            {
                var info = _codec.Info;
                _compositeBitmap ??= new SKBitmap(info);

                SKBitmap? keepCopy = null;

                if (frame != _lastRenderedFrame)
                {
                    if (_lastRenderedFrame >= frame)
                    {
                        ResetCompositeBitmap(_compositeBitmap);
                        _lastRenderedFrame = -1;
                    }

                    for (int i = _lastRenderedFrame + 1; i <= frame; i++)
                    {

                        SKCodecFrameInfo? frameInfo = null;
                        if (_codec.FrameCount > 0)
                        {
                            frameInfo = _codec.FrameInfo[i];
                            if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestoreBackgroundColor)
                            {
                                ResetCompositeBitmap(_compositeBitmap);
                            }
                            else if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious)
                            {
                                keepCopy?.Dispose();
                                keepCopy = _compositeBitmap.Copy();
                            }
                        }

                        //var options = new SKCodecOptions(i, i > 0 ? i - 1 : 0);
                        var requiredFrame = frameInfo?.RequiredFrame ?? (i > 0 ? i - 1 : 0);

                        var options = new SKCodecOptions(i, requiredFrame);
                        try
                        {
                            var r = _codec.GetPixels(info, _compositeBitmap.GetPixels(), options);

                            if (r != SKCodecResult.Success)
                            {
                                Trace.WriteLine(r + $" i={i}");
                                return null;
                            }
                        }
                        catch (Exception e)
                        {
                            Trace.WriteLine(e.Message);
                        }
                    }

                    _lastRenderedFrame = frame;
                }

                var result = _compositeBitmap.Copy(SKColorType.Bgra8888);

                if (keepCopy != null)
                {
                    _compositeBitmap?.Dispose();
                    _compositeBitmap = keepCopy;
                }

                return result;
            }

            return null;
        }


        private static void ResetCompositeBitmap(SKBitmap bitmap)
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
        }

        private int GetCurrentFrameCount()
        {
            if (_codec == null || _cumulativeFrameTimes == null || Frames <= 1 || TotalFrameTime == 0)
            {
                return 0;
            }

            var elapsedTime = Stopwatch.ElapsedMilliseconds;

            // Reset stopwatch every day (24 hours).
            if (elapsedTime >= 86400000)
            {
                Stopwatch.Restart();
                elapsedTime = 0;
            }

            var elapsedFrameTime = elapsedTime % TotalFrameTime;

            // Use binary search to find the current frame index
            int index = Array.BinarySearch(_cumulativeFrameTimes, (int)elapsedFrameTime);

            // BinarySearch returns a negative value if the exact value isn't found.
            if (index < 0)
            {
                index = ~index;
            }

            // Handle wrapping around if needed
            if (index >= _cumulativeFrameTimes.Length)
            {
                index = 0; // Wrap to the first frame
            }

            return index;
        }

        public void AccessSVG(Action<SKPicture> access)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            if (!Loaded)
            {
                return;
            }

            lock (Lock)
            {
                if (SKSvg?.Picture is SKPicture picture)
                {
                    access(picture);
                }
            }
        }

        private SKImageFrameSlot[] GetSKBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKImageMemoryCache.Set(cacheHint, cacheValue, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromSeconds(5),
                        PostEvictionCallbacks = {
                            new PostEvictionCallbackRegistration
                            {
                                EvictionCallback = (key, value, reason, state) =>
                                {
                                    Trace.WriteLine($"Cache entry '{key}' evicted due to {reason}.");
                                    if (value is SKImageFrameSlot[] slots)
                                    {
                                        foreach (var slot in slots)
                                        {
                                            slot.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                    });
                }

                return cacheValue;
            }
        }

        public SKImageFrameSlot[] GetD2DBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKGLImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKGLImageMemoryCache.Set(cacheHint, cacheValue);
                }

                return cacheValue;
            }
        }



        public void AccessSK(int targetWidth, int targetHeight, Action<SKImage> access, bool cache = true, string cacheHint = "default", GRContext? grContext = null)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (!Loaded)
            {
                return;
            }

            if (targetWidth <= 0 || targetHeight <= 0)
                return;

            lock (Lock)
            {
                if (Type == ImageType.FFMPEG)
                {
                    using var image = _backgroundVideoPlayer?.GetLatestFrame(targetWidth, targetHeight);

                    if (image != null)
                    {
                        access(image);
                    }

                    return;
                }

                var SKBitmapCache = grContext != null ? GetD2DBitmapFrameCache(cacheHint) : GetSKBitmapFrameCache(cacheHint);

                var frame = GetCurrentFrameCount();

                var bitmapFrame = SKBitmapCache[frame];

                if (cache && (bitmapFrame.Width != targetWidth || bitmapFrame.Height != targetHeight))
                {
                    bitmapFrame.Invalidate();
                }

                if (bitmapFrame.Image != null && grContext != null && !bitmapFrame.Image.IsValid(grContext))
                {
                    bitmapFrame.Invalidate();
                }

                var shouldDispose = false;

                if (bitmapFrame.Image == null)
                {
                    using var bitmap = GetSKBitmapFromSK(frame);
                    using var resizedBitmap = bitmap?.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);

                    if (grContext != null && resizedBitmap != null)
                    {
                        using var image = SKImage.FromBitmap(resizedBitmap);
                        bitmapFrame.Image = image.ToTextureImage(grContext);
                    }
                    else
                    {
                        bitmapFrame.Image = SKImage.FromBitmap(resizedBitmap);
                    }

                    if (grContext == null && !cache)
                    {
                        shouldDispose = true;
                    }
                }

                if (bitmapFrame.Image != null)
                {
                    access(bitmapFrame.Image);

                    if (shouldDispose)
                    {
                        bitmapFrame.Invalidate();
                    }
                }
            }
        }

        public void DisposeSKAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKImageMemoryCache.Keys)
                {
                    Trace.WriteLine($"Clearing SKImageMemoryCache[{key}]");
                }
                SKImageMemoryCache.Clear();
            }
        }

        public void DisposeD2DAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKGLImageMemoryCache.Keys)
                {
                    Trace.WriteLine($"Clearing SKGLImageMemoryCache[{key}]");
                }
                SKGLImageMemoryCache.Clear();
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            lock (Lock)
            {
                if (!IsDisposed)
                {
                    SKImageMemoryCache.Dispose();
                    SKGLImageMemoryCache.Dispose();

                    SKSvg?.Dispose();
                    _backgroundVideoPlayer?.Dispose();


                    _codec?.Dispose();
                    _stream?.Dispose();
                    _compositeBitmap?.Dispose();

                    Stopwatch.Stop();

                    foreach (var item in imageDisplayItems.Values)
                    {
                        item.Profile.PropertyChanged -= Profile_PropertyChanged;
                        item.PropertyChanged -= ImageDisplayItem_PropertyChanged;
                    }

                    imageDisplayItems.Clear();

                    IsDisposed = true;
                    Trace.WriteLine($"LockedImage {ImagePath} disposed.");
                }
            }

            GC.SuppressFinalize(this);
        }

        public class SKImageFrameSlot : IDisposable
        {
            private SKImage? _bitmap;
            public SKImage? Image
            {
                get => _bitmap;
                set
                {
                    Invalidate();
                    _bitmap = value;
                }
            }

            public int Width => _bitmap?.Width ?? 0;
            public int Height => _bitmap?.Height ?? 0;

            public void Invalidate()
            {
                if (_bitmap == null)
                    return;

                try
                {
                    _bitmap?.Dispose();
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Error invalidating SKImageFrameSlot: {e.Message}");
                }

                _bitmap = null;
            }

            public void Dispose()
            {
                Invalidate();
                GC.SuppressFinalize(this);
            }
        }
    }
}
