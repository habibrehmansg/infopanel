using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using InfoPanel.Sensors.Network;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The public address lookup on a stub handler: what it sends (a GET to ipify, nothing else), what it keeps (a body of
/// at most 64 bytes that is an address of the family asked), its 10 s bound, both families together and never two
/// lookups at once, failures and their log lines, and its task: no request while unused, one on coming into use, one
/// on a network change, 30 min otherwise, back-off on errors, and a stop that cancels a lookup in flight.
/// </summary>
public sealed class PublicIpLookupTests : IDisposable
{
    /// <summary>Answers each request through <see cref="Answer"/>, recording it; by default each family's address.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public string V4 = "203.0.113.7";
        public string V6 = "2001:db8::7";
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Answer;
        public int InFlight;
        public int MaxInFlight;
        public int Cancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
            {
                Requests.Add(request);
                InFlight++;
                MaxInFlight = Math.Max(MaxInFlight, InFlight);
            }

            try
            {
                if (Answer is { } answer)
                    return await answer(request, ct);
                return Ok(request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? V6 : V4);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
            finally
            {
                lock (Requests)
                    InFlight--;
            }
        }

        public int Count
        {
            get
            {
                lock (Requests)
                    return Requests.Count;
            }
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) };

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly StubHandler _handler = new();
    private readonly List<LogEvent> _events = [];
    private readonly PublicIpLookup _lookup;

    public PublicIpLookupTests()
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _lookup = new PublicIpLookup(_handler, _time, log);
    }

    public void Dispose()
    {
        _lookup.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _lookup.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Waits (on the wall clock, at most 10 s) until <paramref name="condition"/> holds.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Waits until the task has taken <paramref name="lookups"/> lookups and waits, so nothing more is under way; the waits so far.</summary>
    private async Task<long> Idle(long lookups)
    {
        await Until(() => _lookup.Lookups == lookups && _lookup.IsWaiting, $"the task to wait after {lookups} lookups");
        return _lookup.Waits;
    }

    private string? Fetch(string body, AddressFamily family = AddressFamily.InterNetwork)
    {
        _handler.Answer = (_, _) => Task.FromResult(Ok(body));
        return _lookup.FetchAsync(family == AddressFamily.InterNetwork ? PublicIpLookup.IPv4Endpoint : PublicIpLookup.IPv6Endpoint, family, Ct).GetAwaiter().GetResult();
    }

    private int Logged(LogEventLevel level, string contains) =>
        _events.Count(e => e.Level == level && e.RenderMessage().Contains(contains, StringComparison.Ordinal));

    // ---- What it keeps ----

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("203.0.113.7\n", "203.0.113.7")]
    [InlineData(" 8.8.8.8 ", "8.8.8.8")]
    [InlineData("<html><body>Sign in</body></html>", null)]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData("0x7f.0.0.1", null)]
    [InlineData("010.0.0.1", null)]
    [InlineData("203.0.113.7 extra", null)]
    [InlineData("2001:db8::7", null)]
    [InlineData("::ffff:203.0.113.7", null)]
    public void TheIPv4Answer_IsKeptOnlyWhenItIsADottedQuad(string body, string? kept) =>
        Assert.Equal(kept, PublicIpLookup.Parse(body, AddressFamily.InterNetwork));

    [Theory]
    [InlineData("2001:db8::7", "2001:db8::7")]
    [InlineData("2001:DB8:0:0:0:0:0:7\r\n", "2001:db8::7")]
    [InlineData("203.0.113.7", null)]
    [InlineData("fe80::1%en0", null)]
    [InlineData("<html></html>", null)]
    public void TheIPv6Answer_IsKeptOnlyWhenItIsAnIPv6Address(string body, string? kept) =>
        Assert.Equal(kept, PublicIpLookup.Parse(body, AddressFamily.InterNetworkV6));

    [Fact]
    public void ABodyOver64Bytes_IsRefused_ThoughItHoldsAnAddress_And64IsTaken()
    {
        Assert.Null(Fetch("203.0.113.7".PadRight(65)));
        Assert.Equal("203.0.113.7", Fetch("203.0.113.7".PadRight(64)));
        Assert.Null(Fetch("<html>" + new string('x', 10_000) + "</html>"));
    }

    /// <summary>
    /// One HTTP/1.1 connection in memory: reads hand out <see cref="Response"/> at most <see cref="Chunk"/> bytes at a
    /// time and count them (<see cref="Consumed"/>); what is written (the request) is dropped.
    /// </summary>
    private sealed class CountingConnection(byte[] response) : Stream
    {
        public const int Chunk = 1024;
        private int _position;
        private int _disposed;

        public byte[] Response => response;

        public int Consumed => Volatile.Read(ref _position);

        public bool Closed => Volatile.Read(ref _disposed) == 1;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(buffer.Length, Chunk), response.Length - _position);
            response.AsSpan(_position, n).CopyTo(buffer);
            Volatile.Write(ref _position, _position + n);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task ARefusedBody_IsNotDrained_ByTheLookupsHandler(int status)
    {
        // 200,000 bytes of body behind a 200 (refused past 64 bytes) or a 500 (refused unread).
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Internal Server Error")}\r\nContent-Length: 200000\r\nContent-Type: text/plain\r\n\r\n");
        var connection = new CountingConnection([.. head, .. Enumerable.Repeat((byte)'x', 200_000)]);
        var handler = PublicIpLookup.CreateHandler();
        // The system's proxy would only change where the in-memory connection says it goes.
        handler.UseProxy = false;
        handler.ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(connection);
        using var lookup = new PublicIpLookup(handler, _time);

        Assert.Null(await lookup.FetchAsync(new Uri("http://api.ipify.test/"), AddressFamily.InterNetwork, Ct));

        // A drain would read the rest in the background: wait for the connection to close, or the body to be read out.
        await Until(() => connection.Closed || connection.Consumed == connection.Response.Length, "the connection to close or be drained");
        await Task.Delay(100, Ct);
        Assert.InRange(connection.Consumed, 0, head.Length + 16 * 1024);
        Assert.True(connection.Closed, "the connection is closed, not kept for reuse");
    }

    [Fact]
    public void TheHandler_DrainsNothing() => Assert.Equal(0, PublicIpLookup.CreateHandler().MaxResponseDrainSize);

    [Fact]
    public void AnIPv6AnswerToTheIPv4Ask_AndAPortalsPage_AreFailures()
    {
        Assert.Null(Fetch("2001:db8::7", AddressFamily.InterNetwork));
        Assert.Null(Fetch("203.0.113.7", AddressFamily.InterNetworkV6));
        Assert.Null(Fetch("<!doctype html><title>Wi-Fi</title>"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NoContent)]
    public void AnAnswerOtherThan200_IsAFailure(HttpStatusCode status)
    {
        _handler.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("203.0.113.7") });

        Assert.Null(_lookup.FetchAsync(PublicIpLookup.IPv4Endpoint, AddressFamily.InterNetwork, Ct).GetAwaiter().GetResult());
    }

    [Fact]
    public void ARedirect_IsNotFollowed_AndIsAFailure()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://portal.example/welcome");
        _handler.Answer = (_, _) => Task.FromResult(redirect);

        Assert.Null(_lookup.FetchAsync(PublicIpLookup.IPv4Endpoint, AddressFamily.InterNetwork, Ct).GetAwaiter().GetResult());
        Assert.Equal(1, _handler.Count);
        Assert.False(PublicIpLookup.CreateHandler().AllowAutoRedirect);
    }

    [Fact]
    public void AThrowingHandler_IsAFailure()
    {
        _handler.Answer = (_, _) => throw new HttpRequestException("No route to host");

        Assert.Null(_lookup.FetchAsync(PublicIpLookup.IPv4Endpoint, AddressFamily.InterNetwork, Ct).GetAwaiter().GetResult());
    }

    // ---- What it sends ----

    [Fact]
    public async Task ALookup_SendsAGetToEachOfIpifysEndpoints_WithNoHeaderOfOurs()
    {
        await _lookup.LookUpAsync(Ct);

        Assert.Equal(2, _handler.Count);
        Assert.Equal(["https://api.ipify.org/", "https://api6.ipify.org/"], _handler.Requests.Select(r => r.RequestUri!.ToString()).Order());
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Empty(r.Headers);
            Assert.Null(r.Content);
        });
    }

    [Fact]
    public void TheHandler_FollowsNoRedirect_KeepsNoCookies_UsesTheSystemsProxy_AsksForNoEncoding()
    {
        using var handler = PublicIpLookup.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task EachRequest_IsBoundedAt10Seconds()
    {
        _handler.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("203.0.113.7");
        };

        var fetch = _lookup.FetchAsync(PublicIpLookup.IPv4Endpoint, AddressFamily.InterNetwork, Ct);
        await Until(() => _handler.Count == 1, "the request");
        _time.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(50, Ct);
        Assert.False(fetch.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(0.1));

        Assert.Null(await fetch.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, _handler.Cancelled);
    }

    [Fact]
    public async Task BothFamilies_AreAskedTogether()
    {
        var both = new TaskCompletionSource();
        _handler.Answer = async (request, ct) =>
        {
            // Neither answers until both are asked.
            if (_handler.Count == 2)
                both.TrySetResult();
            await both.Task.WaitAsync(ct);
            return Ok(request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7");
        };

        await _lookup.LookUpAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, _handler.MaxInFlight);
        Assert.Equal(new PublicIpLookup.Result("203.0.113.7", true), _lookup.Latest(AddressFamily.InterNetwork));
        Assert.Equal(new PublicIpLookup.Result("2001:db8::7", true), _lookup.Latest(AddressFamily.InterNetworkV6));
    }

    // ---- Results and failures ----

    [Fact]
    public async Task AFailedLookup_KeepsTheLastAddress_NotAnswered_AndAnUnchangedAddressIsTheSameString()
    {
        await _lookup.LookUpAsync(Ct);
        var first = _lookup.Latest(AddressFamily.InterNetwork).Address;
        await _lookup.LookUpAsync(Ct);
        Assert.Same(first, _lookup.Latest(AddressFamily.InterNetwork).Address);

        _handler.V4 = "garbage";
        await _lookup.LookUpAsync(Ct);

        Assert.Equal(new PublicIpLookup.Result("203.0.113.7", false), _lookup.Latest(AddressFamily.InterNetwork));
    }

    [Fact]
    public async Task OneLogLineWhenLookupsStartFailing_AndOneWhenTheyAnswerAgain()
    {
        _handler.V4 = _handler.V6 = "nope";
        await _lookup.LookUpAsync(Ct);
        await _lookup.LookUpAsync(Ct);
        await _lookup.LookUpAsync(Ct);
        _handler.V4 = "203.0.113.7";
        await _lookup.LookUpAsync(Ct);
        await _lookup.LookUpAsync(Ct);

        Assert.Equal(1, Logged(LogEventLevel.Warning, "Public IP: the lookup failed"));
        Assert.Equal(1, Logged(LogEventLevel.Information, "Public IP: the lookup answers again"));
    }

    [Fact]
    public async Task ALookupFails_WhenNoFamilyAnswers_OrOneThatAnsweredLastTimeDoesnt_NotForWantOfIPv6()
    {
        // A network without IPv6: answered, the next in 30 min.
        _handler.V6 = "203.0.113.7";
        await _lookup.LookUpAsync(Ct);
        Assert.Equal(TimeSpan.Zero, _lookup.Schedule.Backoff);

        // IPv4 answered last time and doesn't now: failed, backing off.
        _handler.V4 = "nope";
        await _lookup.LookUpAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(30), _lookup.Schedule.Backoff);

        // Nothing answers: failed.
        await _lookup.LookUpAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(60), _lookup.Schedule.Backoff);

        _handler.V4 = "203.0.113.7";
        _handler.V6 = "2001:db8::7";
        await _lookup.LookUpAsync(Ct);
        Assert.Equal(TimeSpan.Zero, _lookup.Schedule.Backoff);

        // IPv6 answered last time and doesn't now: failed.
        _handler.V6 = "nope";
        await _lookup.LookUpAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(30), _lookup.Schedule.Backoff);
    }

    // ---- The task ----

    [Fact]
    public async Task TheTask_NoRequestWhileUnused()
    {
        _lookup.Start();
        await Idle(0);

        _time.Advance(TimeSpan.FromHours(6));
        _lookup.NetworkChanged();
        await Task.Delay(100, Ct);

        Assert.Equal(0, _handler.Count);
        Assert.Equal(0, _lookup.Lookups);
    }

    [Fact]
    public async Task TheTask_OneRequestWhenItComesIntoUse_ThenNoneWhileReadsGoOn()
    {
        _lookup.Start();
        await Idle(0);

        _lookup.NoteUse();
        await Idle(1);
        Assert.True(_lookup.Latest(AddressFamily.InterNetwork).Answered);
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            _lookup.NoteUse();
        }

        await Task.Delay(50, Ct);
        Assert.Equal(1, _lookup.Lookups);
        Assert.Equal(2, _handler.Count);
    }

    [Fact]
    public async Task TheTask_ARequestOnANetworkChange()
    {
        _lookup.Start();
        _lookup.NoteUse();
        await Idle(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        _lookup.NoteUse();
        _lookup.NetworkChanged();

        await Idle(2);
        Assert.Equal(4, _handler.Count);
    }

    [Fact]
    public async Task TheTask_Every30MinutesOtherwise_WhileInUse()
    {
        _lookup.Start();
        _lookup.NoteUse();
        await Idle(1);

        for (var i = 0; i < 29; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            _lookup.NoteUse();
        }

        await Task.Delay(50, Ct);
        Assert.Equal(1, _lookup.Lookups);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Idle(2);
    }

    [Fact]
    public async Task TheTask_NoRequestAt30Minutes_OnceUnused_ThenOneWhenReadAgain()
    {
        _lookup.Start();
        _lookup.NoteUse();
        var waits = await Idle(1);

        // The 30 minutes pass with nothing read since the first: the task wakes, asks nothing, and waits.
        _time.Advance(TimeSpan.FromMinutes(31));
        await Until(() => _lookup.Waits > waits, "the task to wait again");
        _time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50, Ct);
        Assert.Equal(1, _lookup.Lookups);

        _lookup.NoteUse();
        await Idle(2);
    }

    [Fact]
    public async Task TheTask_BacksOffOnErrors()
    {
        _handler.V4 = _handler.V6 = "nope";
        _lookup.Start();
        _lookup.NoteUse();
        await Idle(1);

        // 30 s, then 60 s.
        _time.Advance(TimeSpan.FromSeconds(29));
        _lookup.NoteUse();
        await Task.Delay(50, Ct);
        Assert.Equal(1, _lookup.Lookups);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Idle(2);

        _time.Advance(TimeSpan.FromSeconds(59));
        _lookup.NoteUse();
        await Task.Delay(50, Ct);
        Assert.Equal(2, _lookup.Lookups);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Idle(3);
    }

    [Fact]
    public async Task TheTask_ANetworkChangeDuringABackOff_AsksOnlyWhenItEnds()
    {
        _handler.V4 = _handler.V6 = "nope";
        _lookup.Start();
        _lookup.NoteUse();
        await Idle(1);

        _time.Advance(TimeSpan.FromSeconds(5));
        _lookup.NoteUse();
        _lookup.NetworkChanged();
        await Task.Delay(100, Ct);
        Assert.Equal(1, _lookup.Lookups);
        Assert.Equal(2, _handler.Count);

        _time.Advance(TimeSpan.FromSeconds(25));
        await Idle(2);
        Assert.Equal(4, _handler.Count);
    }

    [Fact]
    public async Task TheTask_NeverTwoLookupsAtOnce()
    {
        var release = new TaskCompletionSource();
        _handler.Answer = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Ok(request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7");
        };
        _lookup.Start();
        _lookup.NoteUse();
        await Until(() => _handler.Count == 2, "the first lookup's requests");

        // Causes while it is in flight: another lookup only after it.
        _lookup.NetworkChanged();
        _lookup.NetworkChanged();
        await Task.Delay(100, Ct);
        Assert.Equal(2, _handler.Count);

        release.SetResult();
        await Idle(2);
        Assert.Equal(4, _handler.Count);
        Assert.Equal(2, _handler.MaxInFlight);
    }

    [Fact]
    public async Task TheStop_CancelsALookupInFlight_AndTheTaskEnds()
    {
        _handler.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("203.0.113.7");
        };
        _lookup.Start();
        _lookup.NoteUse();
        await Until(() => _handler.Count == 2, "the lookup's requests");

        await _lookup.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, _handler.Cancelled);
        Assert.Equal(0, _handler.InFlight);
        // Stopped, nothing more is asked.
        _lookup.NoteUse();
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, Ct);
        Assert.Equal(2, _handler.Count);
    }
}
