using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// A <c>png</c> subscription's sink holds a reference to each PNG it queues and gives it back exactly once: when a newer
/// frame replaces it in the slot, when the send loop has sent it, or when the subscription ends with it still queued.
/// </summary>
public class PngLeaseTests
{
    private const string ProfileId = "3dd19c0e-675a-4591-b096-83417853861f";

    private static FrameHeader Header(long tick) => new()
    {
        ProfileId = ProfileId, Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1, PixelWidth = 4, PixelHeight = 4,
        ProfileWidth = 4, ProfileHeight = 4, ItemBounds = [], EncodeMs = 0, Skipped = 0,
    };

    private sealed class Returns
    {
        public readonly List<byte[]> Arrays = [];

        public PngLease Lease()
        {
            var array = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
            return new PngLease(array, array.Length, a =>
            {
                lock (Arrays)
                    Arrays.Add(a);
                a.AsSpan().Fill(0xDD);
            });
        }

        public int Count
        {
            get { lock (Arrays) return Arrays.Count; }
        }
    }

    private static WebPreviewSink Sink() => new(ProfileId, new FrameRequest(1, 30), () => { }, (_, _) => { });

    [Fact]
    public void AFrameReplacedInTheSlot_BeforeItsSend_IsGivenBack()
    {
        var returns = new Returns();
        var sink = Sink();
        var first = returns.Lease();
        var second = returns.Lease();
        sink.QueuePng(Header(1), first);
        sink.QueuePng(Header(2), second);
        Assert.Equal(1, returns.Count);
        Assert.Equal(0xDD, first.Memory.Span[0]);
        Assert.Equal(0x89, second.Memory.Span[0]);
    }

    [Fact]
    public void ASentFrame_IsGivenBack_OnceTheSendCompletes_NotBefore()
    {
        var returns = new Returns();
        var sink = Sink();
        var lease = returns.Lease();
        sink.QueuePng(Header(1), lease);
        Assert.True(sink.TryTakeFrame(out var frame));
        Assert.Equal(0, returns.Count);
        Assert.Equal(0x89, frame.Payload.Span[0]);
        frame.Sent();
        Assert.Equal(1, returns.Count);
        Assert.False(sink.TryTakeFrame(out _));
    }

    [Fact]
    public void ARetiredSink_GivesBackWhatIsStillQueued_AndWhatArrivesAfter()
    {
        var returns = new Returns();
        var sink = Sink();
        sink.QueuePng(Header(1), returns.Lease());
        sink.Retire();
        Assert.Equal(1, returns.Count);
        sink.QueuePng(Header(2), returns.Lease());
        Assert.Equal(2, returns.Count);
    }
}
