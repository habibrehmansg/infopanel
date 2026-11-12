using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using InfoPanel.Sensors.NowPlaying;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

// The declarations pinned below are Windows-only for the product; their IIDs, slot order and layouts are metadata that
// any OS reads, and the async wait runs over a fake. Nothing here calls Windows.
#pragma warning disable CA1416

/// <summary>
/// The Windows reader's WinRT ABI (<see cref="WinNowPlayingReader"/>), on any OS: each interface's IID (a parameterised
/// one computed by WinRT's signature rule, and compared with its published value) and its slots in the Windows
/// metadata's order (IInspectable's three first), each PreserveSig with an int HRESULT; the 64-bit sizes of what its
/// methods hand back by value; the enum values it reads; the async wait: done, failed, cancelled, and timed out (cancelled,
/// never closed while it runs, kept until it ends, no other of its kind started meanwhile); the thumbnail's stream read
/// over a scripted stream (the bound checked before anything is allocated or read, exactly the bound and one over it, a
/// read that never ends); and the shell's names read no further than the bound.
/// </summary>
public sealed class WinNowPlayingAbiTests
{
    private static readonly string[] Inspectable = ["GetIids", "GetRuntimeClassName", "GetTrustLevel"];

    // The generic interfaces' own IIDs, from Windows.Foundation.winmd.
    private const string AsyncOperation = "{9fc2b0bb-e446-44e2-aa61-9cab8f636af2}";
    private const string AsyncOperationWithProgress = "{b5d036d7-e297-498f-ba60-0289e76e23dd}";
    private const string Reference = "{61c17706-2d65-11e0-9ae8-d48564015472}";
    private const string VectorView = "{bbe1fa4c-b0e3-4583-baef-1f1b2e483e56}";

    /// <summary>
    /// WinRT's rule for a parameterised interface's IID: a version 5 UUID, the SHA-1 of the WinRT namespace's bytes and
    /// the instance's signature in UTF-8.
    /// </summary>
    private static Guid ParameterisedIid(string signature)
    {
        byte[] ns = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
        var hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(signature)]);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The parameterised interfaces: their signature and their published IID (the SDK headers', or .NET's projection's).</summary>
    public static TheoryData<Type, string, string> Parameterised() => new()
    {
        {
            typeof(IAsyncOperationOfManager),
            $"pinterface({AsyncOperation};rc(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;{{cace8eac-e86e-504a-ab31-5ff8ff1bce49}}))",
            "3eec115e-7346-5c27-8c5f-da78514a277b"
        },
        {
            typeof(IAsyncOperationOfMediaProperties),
            $"pinterface({AsyncOperation};rc(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties;{{68856cf6-adb4-54b2-ac16-05837907acb6}}))",
            "b185e6f3-e0d8-51cb-913f-c98d48c93c46"
        },
        { typeof(IAsyncOperationOfStream), $"pinterface({AsyncOperation};{{cc254827-4b3d-438f-9232-10c76bc7e038}})", "c4a57c5e-32b0-55b3-ad13-ce1c23041ed6" },
        { typeof(IAsyncOperationOfBuffer), $"pinterface({AsyncOperationWithProgress};{{905a0fe0-bc53-11df-8c49-001e4fc686da}};u4)", "d26b2819-897f-5c7d-84d6-56d796561431" },
        { typeof(IReferenceOfBoolean), $"pinterface({Reference};b1)", "3c00fd60-2950-5939-a21a-2d12c5a01b8a" },
        { typeof(IReferenceOfDouble), $"pinterface({Reference};f8)", "2f2d6c29-5473-5f3e-92e7-96572bb990e2" },
        { typeof(IVectorViewOfString), $"pinterface({VectorView};string)", "2f13c006-a03a-5f69-b090-75a43e33423e" },
    };

    [Theory]
    [MemberData(nameof(Parameterised))]
    public void A_parameterised_interfaces_IID_is_WinRTs_signature_rules_and_its_published_value(Type type, string signature, string published)
    {
        Assert.Equal(Guid.Parse(published), ParameterisedIid(signature));
        Assert.Equal(Guid.Parse(published), type.GUID);
    }

    [Fact]
    public void The_repeat_modes_reference_has_the_IID_the_signature_rule_gives_for_an_Int32_enum()
    {
        // No published value at hand: the rule, checked against the six above, and the PC's run, which asks for it.
        var iid = ParameterisedIid($"pinterface({Reference};enum(Windows.Media.MediaPlaybackAutoRepeatMode;i4))");
        Assert.Equal(Guid.Parse("50a7f41f-58d5-5c4d-9475-8dd1acd65836"), iid);
        Assert.Equal(iid, typeof(IReferenceOfAutoRepeatMode).GUID);
    }

    /// <summary>Each interface's IID and its methods in slot order after IUnknown's three, from the Windows metadata (WinMetadata's winmd files) and the SDK headers.</summary>
    public static TheoryData<Type, string, string[]> Interfaces() => new()
    {
        { typeof(IGsmtcManagerStatics), "2050c4ee-11a0-57de-aed7-c97c70338245", [.. Inspectable, "RequestAsync"] },
        { typeof(IGsmtcManager), "cace8eac-e86e-504a-ab31-5ff8ff1bce49", [.. Inspectable, "GetCurrentSession"] },
        {
            typeof(IGsmtcSession), "7148c835-9b14-5ae2-ab85-dc9b1c14e1a8",
            [.. Inspectable, "get_SourceAppUserModelId", "TryGetMediaPropertiesAsync", "GetTimelineProperties", "GetPlaybackInfo"]
        },
        {
            typeof(IGsmtcMediaProperties), "68856cf6-adb4-54b2-ac16-05837907acb6",
            [
                .. Inspectable, "get_Title", "get_Subtitle", "get_AlbumArtist", "get_Artist", "get_AlbumTitle", "get_TrackNumber",
                "get_Genres", "get_AlbumTrackCount", "get_PlaybackType", "get_Thumbnail",
            ]
        },
        {
            typeof(IGsmtcPlaybackInfo), "94b4b6cf-e8ba-51ad-87a7-c10ade106127",
            [.. Inspectable, "get_Controls", "get_PlaybackStatus", "get_PlaybackType", "get_AutoRepeatMode", "get_PlaybackRate", "get_IsShuffleActive"]
        },
        {
            typeof(IGsmtcTimelineProperties), "ede34136-6f25-588d-8ecf-ea5b6735aaa5",
            [.. Inspectable, "get_StartTime", "get_EndTime", "get_MinSeekTime", "get_MaxSeekTime", "get_Position", "get_LastUpdatedTime"]
        },
        { typeof(IAsyncInfo), "00000036-0000-0000-c000-000000000046", [.. Inspectable, "get_Id", "get_Status", "get_ErrorCode", "Cancel", "Close"] },
        { typeof(IAsyncOperationOfManager), "3eec115e-7346-5c27-8c5f-da78514a277b", [.. Inspectable, "put_Completed", "get_Completed", "GetResults"] },
        { typeof(IAsyncOperationOfMediaProperties), "b185e6f3-e0d8-51cb-913f-c98d48c93c46", [.. Inspectable, "put_Completed", "get_Completed", "GetResults"] },
        { typeof(IAsyncOperationOfStream), "c4a57c5e-32b0-55b3-ad13-ce1c23041ed6", [.. Inspectable, "put_Completed", "get_Completed", "GetResults"] },
        { typeof(IReferenceOfBoolean), "3c00fd60-2950-5939-a21a-2d12c5a01b8a", [.. Inspectable, "get_Value"] },
        { typeof(IReferenceOfDouble), "2f2d6c29-5473-5f3e-92e7-96572bb990e2", [.. Inspectable, "get_Value"] },
        { typeof(IReferenceOfAutoRepeatMode), "50a7f41f-58d5-5c4d-9475-8dd1acd65836", [.. Inspectable, "get_Value"] },
        { typeof(IVectorViewOfString), "2f13c006-a03a-5f69-b090-75a43e33423e", [.. Inspectable, "GetAt", "get_Size"] },
        { typeof(IRandomAccessStreamReference), "33ee3134-1dd6-4e3a-8067-d1c162e8642b", [.. Inspectable, "OpenReadAsync"] },
        { typeof(IRandomAccessStream), "905a0fe1-bc53-11df-8c49-001e4fc686da", [.. Inspectable, "get_Size"] },
        { typeof(IContentTypeProvider), "97d098a5-3b99-4de9-88a5-e11d2f50c795", [.. Inspectable, "get_ContentType"] },
        { typeof(IInputStream), "905a0fe2-bc53-11df-8c49-001e4fc686da", [.. Inspectable, "ReadAsync"] },
        { typeof(IBuffer), "905a0fe0-bc53-11df-8c49-001e4fc686da", [.. Inspectable, "get_Capacity", "get_Length", "put_Length"] },
        { typeof(IBufferFactory), "71af914d-c10f-484b-bc50-14bc623b3a27", [.. Inspectable, "Create"] },
        { typeof(IBufferByteAccess), "905a0fef-bc53-11df-8c49-001e4fc686da", ["Buffer"] },
        {
            typeof(IAsyncOperationOfBuffer), "d26b2819-897f-5c7d-84d6-56d796561431",
            [.. Inspectable, "put_Progress", "get_Progress", "put_Completed", "get_Completed", "GetResults"]
        },
        { typeof(IShellItem), "43826d1e-e718-42ee-bc55-a1e261c37bfe", ["BindToHandler", "GetParent", "GetDisplayName"] },
    };

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void The_interfaces_have_the_metadatas_IIDs_and_slots_each_PreserveSig_returning_an_HRESULT(Type type, string iid, string[] slots)
    {
        Assert.Equal(Guid.Parse(iid), type.GUID);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.MetadataToken).ToArray();
        Assert.Equal(slots, methods.Select(m => m.Name));
        Assert.All(methods, m =>
        {
            Assert.Equal(typeof(int), m.ReturnType);
            Assert.True(m.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), $"{type.Name}.{m.Name} isn't PreserveSig");
        });
    }

    [Fact]
    public void What_the_methods_hand_back_by_value_has_the_metadatas_64_bit_sizes()
    {
        Assert.Equal(8, IntPtr.Size);
        // HSTRING and interface pointers: a pointer each.
        Assert.Equal(typeof(nint), OutType(typeof(IGsmtcSession), "get_SourceAppUserModelId"));
        Assert.Equal(typeof(nint), OutType(typeof(IGsmtcMediaProperties), "get_Thumbnail"));
        // Windows.Foundation.TimeSpan (Duration) and DateTime (UniversalTime): one Int64 each.
        Assert.All(["get_StartTime", "get_EndTime", "get_MinSeekTime", "get_MaxSeekTime", "get_Position", "get_LastUpdatedTime"],
            name => Assert.Equal(typeof(long), OutType(typeof(IGsmtcTimelineProperties), name)));
        // The playback status, the repeat mode and the async status: Int32 enums; a WinRT Boolean: one byte; the track number: Int32.
        Assert.Equal(typeof(int), OutType(typeof(IGsmtcPlaybackInfo), "get_PlaybackStatus"));
        Assert.Equal(typeof(int), OutType(typeof(IReferenceOfAutoRepeatMode), "get_Value"));
        Assert.Equal(typeof(int), OutType(typeof(IAsyncInfo), "get_Status"));
        Assert.Equal(1, Unsafe.SizeOf<byte>());
        Assert.Equal(typeof(byte), OutType(typeof(IReferenceOfBoolean), "get_Value"));
        Assert.Equal(typeof(double), OutType(typeof(IReferenceOfDouble), "get_Value"));
        Assert.Equal(typeof(int), OutType(typeof(IGsmtcMediaProperties), "get_TrackNumber"));
        Assert.Equal(typeof(ulong), OutType(typeof(IRandomAccessStream), "get_Size"));
        // ReadAsync's count and InputStreamOptions (a flags enum over UInt32), a buffer's length and capacity: UInt32; its bytes: a pointer.
        Assert.Equal([typeof(nint), typeof(uint), typeof(uint), typeof(nint).MakeByRefType()],
            typeof(IInputStream).GetMethod("ReadAsync")!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(uint), OutType(typeof(IBuffer), "get_Length"));
        Assert.Equal(typeof(uint), OutType(typeof(IBuffer), "get_Capacity"));
        Assert.Equal(typeof(nint), OutType(typeof(IBufferByteAccess), "Buffer"));
    }

    private static Type OutType(Type type, string method) =>
        type.GetMethod(method)!.GetParameters().Single(p => p.IsOut).ParameterType.GetElementType()!;

    [Fact]
    public void The_enum_values_and_the_instants_are_the_metadatas()
    {
        // GlobalSystemMediaTransportControlsSessionPlaybackStatus: Closed 0, Opened 1, Changing 2, Stopped 3, Playing 4, Paused 5.
        Assert.Equal(4, WinPlayback.Playing);
        Assert.Equal(5, WinPlayback.Paused);
        // AsyncStatus: Started 0, Completed 1, Canceled 2, Error 3.
        Assert.Equal((0, 1, 2, 3), (WinRt.Started, WinRt.Completed, WinRt.Canceled, WinRt.Error));
        // REGDB_E_CLASSNOTREG, and HRESULT_FROM_WIN32(ERROR_SERVICE_DOES_NOT_EXIST, 1060).
        Assert.Equal(unchecked((int)0x80040154), WinNowPlayingReader.ClassNotRegistered);
        Assert.Equal(unchecked((int)0x80070000 | 1060), WinNowPlayingReader.ServiceMissing);
        // DateTime: 100 ns since 1601-01-01 UTC; 0 is none.
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), WinRt.Instant(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero).ToFileTime()));
        Assert.Null(WinRt.Instant(0));
        Assert.Null(WinRt.Instant(long.MaxValue));
    }

    // ---- The async wait, over a fake operation ----

    /// <summary>An async operation whose status the test scripts: it counts its cancels and closes (the reader's tests use it too).</summary>
    internal sealed class FakeAsync(params int[] statuses) : IAsyncInfo
    {
        private int _asked;
        public int ErrorCode = unchecked((int)0x80070005);
        public int Cancels;
        public int Closes;

        /// <summary>Closes asked while the operation was still running (its status last <see cref="WinRt.Started"/>): refused, as Windows refuses them.</summary>
        public int RejectedCloses;
        private int _status = WinRt.Started;

        public int GetIids(out uint count, out nint iids) => throw new NotSupportedException();
        public int GetRuntimeClassName(out nint name) => throw new NotSupportedException();
        public int GetTrustLevel(out int level) => throw new NotSupportedException();

        public int get_Id(out uint id)
        {
            id = 1;
            return 0;
        }

        /// <summary>When set, the status it answers from now on, in place of the script.</summary>
        public int? Now;

        /// <summary>When set, what its close answers once it has ended.</summary>
        public int CloseFails;

        public int get_Status(out int status)
        {
            status = _status = Now ?? statuses[Math.Min(_asked++, statuses.Length - 1)];
            return 0;
        }

        public int get_ErrorCode(out int code)
        {
            code = ErrorCode;
            return 0;
        }

        public int Cancel()
        {
            Cancels++;
            return 0;
        }

        public int Close()
        {
            Closes++;
            if (_status != WinRt.Started)
                return CloseFails;
            RejectedCloses++;
            return unchecked((int)0x8000000E); // E_ILLEGAL_METHOD_CALL
        }
    }

    /// <summary>What an awaited operation was let go of with, and its result discarded with.</summary>
    private sealed class Held
    {
        public int Released;
        public int Discarded;
        public void Release() => Released++;
        public void Discard(int _) => Discarded++;
    }

    private static int Await(FakeAsync op, Held held, WinAsyncSlot slot, TimeSpan? timeout = null) =>
        WinRt.Await(op, () => 42, held.Release, held.Discard, timeout ?? TimeSpan.FromSeconds(2), slot, "test");

    [Fact]
    public void An_operation_that_completes_is_waited_for_then_its_result_taken_then_it_is_closed_and_let_go_of()
    {
        var op = new FakeAsync(WinRt.Started, WinRt.Started, WinRt.Completed);
        var held = new Held();
        var slot = new WinAsyncSlot("test");

        Assert.Equal(WinRt.Completed, WinRt.Wait(op, TimeSpan.FromSeconds(2), "test"));
        Assert.Equal((0, 0), (op.Cancels, op.Closes));

        Assert.Equal(42, Await(new FakeAsync(WinRt.Started, WinRt.Completed), held, slot));
        Assert.Equal((1, 0), (held.Released, held.Discarded));
        Assert.False(slot.Busy);
    }

    [Fact]
    public void An_operation_that_fails_is_closed_let_go_of_and_throws_its_error()
    {
        var op = new FakeAsync(WinRt.Started, WinRt.Error);
        var held = new Held();

        var ex = Assert.Throws<COMException>(() => Await(op, held, new WinAsyncSlot("test")));

        Assert.Equal(unchecked((int)0x80070005), ex.HResult);
        Assert.Equal((0, 1, 0), (op.Cancels, op.Closes, op.RejectedCloses));
        Assert.Equal(1, held.Released);
    }

    [Fact]
    public void An_operation_cancelled_elsewhere_is_closed_let_go_of_and_throws()
    {
        var op = new FakeAsync(WinRt.Canceled);
        var held = new Held();

        Assert.Throws<COMException>(() => Await(op, held, new WinAsyncSlot("test")));

        Assert.Equal((0, 1, 0), (op.Cancels, op.Closes, op.RejectedCloses));
        Assert.Equal(1, held.Released);
    }

    [Fact]
    public void An_operation_not_done_in_time_is_cancelled_never_closed_while_it_runs_and_kept_until_it_ends()
    {
        var op = new FakeAsync(WinRt.Started);
        var held = new Held();
        var slot = new WinAsyncSlot("test");

        Assert.Throws<TimeoutException>(() => Await(op, held, slot, TimeSpan.FromMilliseconds(50)));

        // Cancelled, not closed, still held: the slot keeps it.
        Assert.Equal((1, 0, 0), (op.Cancels, op.Closes, op.RejectedCloses));
        Assert.Equal(0, held.Released);
        Assert.True(slot.Busy);

        // Still running: no other of its kind starts (the round skips it), and it isn't closed.
        Assert.Throws<TimeoutException>(slot.Claim);
        Assert.Equal((0, 0), (op.Closes, held.Released));

        // Ended: closed once, then let go of, and the slot is free again.
        op.Now = WinRt.Canceled;
        slot.Claim();
        Assert.Equal((1, 0), (op.Closes, op.RejectedCloses));
        Assert.Equal(1, held.Released);
        Assert.False(slot.Busy);
        Assert.Equal(42, Await(new FakeAsync(WinRt.Completed), new Held(), slot));
    }

    [Fact]
    public void A_close_that_fails_after_completion_isnt_ignored_its_result_is_discarded_and_it_is_let_go_of()
    {
        var op = new FakeAsync(WinRt.Completed) { CloseFails = unchecked((int)0x80004005) };
        var held = new Held();

        var ex = Assert.Throws<COMException>(() => Await(op, held, new WinAsyncSlot("test")));

        Assert.Equal(unchecked((int)0x80004005), ex.HResult);
        Assert.Equal((1, 1), (held.Discarded, held.Released));
    }

    // ---- The thumbnail's stream, read by the native layer over a scripted stream ----

    /// <summary>A stream that says <paramref name="size"/> bytes and answers each read with at most <paramref name="chunk"/> of its pattern.</summary>
    private sealed class ScriptedStream(ulong size, int chunk) : IWinStream
    {
        public int Reads;
        public long Delivered;
        public bool Hangs;
        public readonly List<ScriptedRead> Started = [];

        public string? ContentType => "image/png";
        public ulong Size => size;

        public IWinRead Read(uint count)
        {
            Reads++;
            var n = (int)Math.Min(Math.Min(count, (uint)chunk), size - (ulong)Delivered);
            var read = new ScriptedRead(Hangs ? new FakeAsync(WinRt.Started) : new FakeAsync(WinRt.Completed), Delivered, n);
            Delivered += n;
            Started.Add(read);
            return read;
        }
    }

    private sealed class ScriptedRead(FakeAsync info, long offset, int count) : IWinRead
    {
        public readonly FakeAsync Op = info;
        public int Released;
        public IAsyncInfo Info => Op;

        public int CopyTo(Span<byte> destination)
        {
            var n = Math.Min(count, destination.Length);
            for (var i = 0; i < n; i++)
                destination[i] = Pattern(offset + i);
            return n;
        }

        public void Release() => Released++;
    }

    private static byte Pattern(long at) => (byte)(at % 251);

    [Fact]
    public void A_thumbnail_of_exactly_the_bound_is_read_whole_in_the_streams_chunks_each_read_closed_and_let_go_of()
    {
        var stream = new ScriptedStream(NowPlayingSource.MaxArtBytes, 3 * 1024 * 1024);

        var thumbnail = WinRt.ReadThumbnail(stream, NowPlayingSource.MaxArtBytes, TimeSpan.FromSeconds(10), new WinAsyncSlot("test"));

        var bytes = thumbnail.Bytes!.Value.Span;
        Assert.Equal(NowPlayingSource.MaxArtBytes, bytes.Length);
        Assert.Equal(((ulong)NowPlayingSource.MaxArtBytes, "image/png"), (thumbnail.Size, thumbnail.ContentType));
        foreach (var at in new[] { 0, 1, 3 * 1024 * 1024 - 1, 3 * 1024 * 1024, NowPlayingSource.MaxArtBytes - 1 })
            Assert.Equal(Pattern(at), bytes[at]);
        Assert.Equal(3, stream.Reads);
        Assert.All(stream.Started, r => Assert.Equal((1, 0, 1), (r.Op.Closes, r.Op.RejectedCloses, r.Released)));
    }

    [Theory]
    [InlineData(NowPlayingSource.MaxArtBytes + 1UL)]
    [InlineData(ulong.MaxValue)]
    public void A_thumbnail_that_says_it_is_over_the_bound_is_neither_allocated_nor_read(ulong size)
    {
        var stream = new ScriptedStream(size, 1024);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var thumbnail = WinRt.ReadThumbnail(stream, NowPlayingSource.MaxArtBytes, TimeSpan.FromSeconds(10), new WinAsyncSlot("test"));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(thumbnail.Bytes);
        Assert.Equal(size, thumbnail.Size);
        Assert.Equal(0, stream.Reads);
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void A_thumbnail_shorter_than_it_says_is_what_its_stream_gave()
    {
        var stream = new ScriptedStream(10, 4) { };

        var thumbnail = WinRt.ReadThumbnail(new ShortStream(stream, 6), NowPlayingSource.MaxArtBytes, TimeSpan.FromSeconds(10), new WinAsyncSlot("test"));

        Assert.Equal(6, thumbnail.Bytes!.Value.Length);
        Assert.Equal(10UL, thumbnail.Size);
    }

    /// <summary>A stream that ends after <paramref name="ends"/> bytes though it says more.</summary>
    private sealed class ShortStream(ScriptedStream inner, int ends) : IWinStream
    {
        public string? ContentType => inner.ContentType;
        public ulong Size => inner.Size;
        public IWinRead Read(uint count) => inner.Read((uint)Math.Min(count, (ulong)Math.Max(0, ends - inner.Delivered)));
    }

    [Fact]
    public void A_thumbnail_read_that_never_ends_times_out_its_read_kept_unclosed_and_no_other_read_starts_until_it_ends()
    {
        var stream = new ScriptedStream(100, 100) { Hangs = true };
        var slot = new WinAsyncSlot("test");

        Assert.Throws<TimeoutException>(() => WinRt.ReadThumbnail(stream, NowPlayingSource.MaxArtBytes, TimeSpan.FromMilliseconds(50), slot));
        var read = Assert.Single(stream.Started);
        Assert.Equal((1, 0, 0), (read.Op.Cancels, read.Op.Closes, read.Released));

        // The next art read skips while it runs: no second read is started.
        Assert.Throws<TimeoutException>(() => WinRt.ReadThumbnail(stream, NowPlayingSource.MaxArtBytes, TimeSpan.FromSeconds(1), slot));
        Assert.Equal(1, stream.Reads);

        // Once it has ended it is closed and let go of, and reads go on.
        read.Op.Now = WinRt.Canceled;
        stream.Hangs = false;
        stream.Delivered = 0;
        var thumbnail = WinRt.ReadThumbnail(stream, NowPlayingSource.MaxArtBytes, TimeSpan.FromSeconds(1), slot);
        Assert.Equal((1, 0, 1), (read.Op.Closes, read.Op.RejectedCloses, read.Released));
        Assert.Equal(100, thumbnail.Bytes!.Value.Length);
    }

    // ---- The shell's display name, read no further than the bound ----

    [Fact]
    public void A_shell_name_is_read_no_further_than_the_bound()
    {
        var bound = WinNowPlayingReader.MaxKeptChars;

        Assert.Equal(new string('n', bound), WinAppNames.TakeText(Marshal.StringToCoTaskMemUni(new string('n', 100_000)), bound));
        Assert.Equal("Microsoft Edge", WinAppNames.TakeText(Marshal.StringToCoTaskMemUni("Microsoft Edge"), bound));
        Assert.Equal("", WinAppNames.TakeText(Marshal.StringToCoTaskMemUni(""), bound));
    }
}
