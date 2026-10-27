using System.Net;
using System.Net.Sockets;
using System.Text;
using InfoPanel.Models;
using InfoPanel.Spec;
using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// An SVG image file is decoded like an SVG asset on the spec path: only from the document the validator rebuilt from it
/// (spec/docs/svg.md), so a file outside the profile is refused before the SVG decoder sees any of its bytes.
/// </summary>
public class LockedImageSvgTests
{
    private const string Head = "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'>";

    [Fact]
    public void OrdinarySvg_LoadsFromTheRebuiltDocument_AndDraws()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20' viewBox='0 0 4 2'>" +
                  "<!-- a comment the rebuilt document leaves out -->" +
                  "<rect width='2' height='2' style='fill: #ff0000'/><rect x='2' width='2' height='2' fill='blue'/></svg>";
        var decoded = new List<byte[]>();
        using var image = Load(svg, decoded);

        Assert.True(image.Loaded);
        Assert.True(image.IsVector);
        Assert.Equal(40, image.Width);
        Assert.Equal(20, image.Height);
        // The decoder read the rebuilt document, not the file.
        var file = Encoding.UTF8.GetBytes(svg);
        var read = Assert.Single(decoded);
        Assert.Equal(MediaValidator.Check(file).Sanitized, read);
        Assert.NotEqual(file, read);
        Assert.DoesNotContain("comment", Encoding.UTF8.GetString(read));

        using var bitmap = new SKBitmap(40, 20);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            image.AccessVector(picture => canvas.DrawPicture(picture));
        }
        Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 10));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(30, 10));
    }

    [Fact]
    public void BareUriMask_OverHttp_IsRefused_AndNothingIsFetched()
    {
        using var server = new RecordingServer();
        var uri = $"{server.Prefix}x.svg";
        var decoded = new List<byte[]>();

        using var image = Load(Head + $"<rect width='64' height='64' fill='red' mask='{uri}#m'/></svg>", decoded);

        Assert.False(image.Loaded);
        AssertNeverDecoded(decoded, uri);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public void BareUriMask_ToALocalFile_IsRefused_AndNothingIsOpened()
    {
        var target = Path.Combine(Path.GetTempPath(), $"infopanel-{Guid.NewGuid():N}.svg");
        File.WriteAllText(target, Head + "<mask id='m'><rect width='64' height='64' fill='white'/></mask></svg>");
        try
        {
            var uri = new Uri(target).AbsoluteUri;
            var decoded = new List<byte[]>();

            using var image = Load(Head + $"<rect width='64' height='64' fill='red' mask='{uri}#m'/></svg>", decoded);

            Assert.False(image.Loaded);
            AssertNeverDecoded(decoded, uri);
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void DeeplyNestedGroups_AreRefused_WithoutReachingTheDecoder()
    {
        // The SVG decoder recurses once per level; this many levels overflowed the stack and ended the process.
        const int levels = 3_000;
        var svg = new StringBuilder(Head);
        for (var i = 0; i < levels; i++) svg.Append("<g>");
        svg.Append("<rect width='64' height='64' fill='red'/>");
        for (var i = 0; i < levels; i++) svg.Append("</g>");
        svg.Append("</svg>");
        var decoded = new List<byte[]>();

        using var image = Load(svg.ToString(), decoded);

        Assert.False(image.Loaded);
        Assert.Empty(decoded);
    }

    [Fact]
    public void SvgOverTheSvgByteLimit_IsRefused_WithoutReachingTheDecoder()
    {
        var limit = SpecLimits.Default.SvgBytes;
        var decoded = new List<byte[]>();

        using var image = LoadPadded(Encoding.UTF8.GetBytes(Head + "</svg>"), limit + 1, decoded);

        Assert.False(image.Loaded);
        Assert.Empty(decoded);
    }

    [Fact]
    public void RasterOverTheMediaByteLimit_IsRefused_AndOneAtTheLimitLoads()
    {
        // A PNG reads only its header, so trailing padding alone decides whether it loads.
        using var bitmap = new SKBitmap(2, 2);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        var limit = SpecLimits.Default.MediaBytes;

        using (var atLimit = LoadPadded(png.ToArray(), limit, []))
        {
            Assert.True(atLimit.Loaded);
            Assert.Equal(2, atLimit.Width);
        }
        using var overLimit = LoadPadded(png.ToArray(), limit + 1, []);
        Assert.False(overLimit.Loaded);
    }

    private static LockedImage Load(string svg, List<byte[]> decoded)
    {
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, svg);
        try
        {
            return new LockedImage(path, Recording(decoded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Loads <paramref name="content"/> padded with zeros to <paramref name="length"/> bytes (sparse where the file system allows).</summary>
    private static LockedImage LoadPadded(byte[] content, long length, List<byte[]> decoded)
    {
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-{Guid.NewGuid():N}.img");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew))
            {
                file.Write(content);
                file.SetLength(length);
            }
            return new LockedImage(path, Recording(decoded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Loads the SVG decoder as the engine does, recording the bytes of the stream it is handed.</summary>
    private static Action<SKSvg, Stream> Recording(List<byte[]> decoded) => (svg, stream) =>
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        decoded.Add(copy.ToArray());
        stream.Position = 0;
        svg.Load(stream);
    };

    /// <summary>A refused file never reaches the decoder; were anything decoded, it would not carry the URI.</summary>
    private static void AssertNeverDecoded(List<byte[]> decoded, string uri)
    {
        Assert.All(decoded, bytes => Assert.DoesNotContain(uri, Encoding.UTF8.GetString(bytes)));
        Assert.Empty(decoded);
    }

    /// <summary>
    /// A minimal HTTP server on a loopback port that counts the connections it accepts. The port stays bound from
    /// allocation to disposal, so nothing else can take it in between.
    /// </summary>
    private sealed class RecordingServer : IDisposable
    {
        private static readonly byte[] NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _requests;

        public RecordingServer()
        {
            _listener.Start();
            Prefix = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(ServeAsync);
        }

        public string Prefix { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                // Counted before the answer, so a connection made while the image loads is counted before it returns.
                Interlocked.Increment(ref _requests);
                using (client)
                {
                    try
                    {
                        await client.GetStream().WriteAsync(NotFound);
                    }
                    catch (Exception e) when (e is IOException or SocketException)
                    {
                    }
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
