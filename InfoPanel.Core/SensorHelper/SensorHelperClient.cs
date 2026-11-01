using Serilog;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The engine's side of the helper's protocol (<see cref="HelperProtocol"/>), one request at a time. A sample has
/// <see cref="HelperProtocol.SampleTimeout"/> from the moment it is asked, anything else
/// <see cref="HelperProtocol.RequestTimeout"/>, on the client's clock, its wait for the request ahead of it included. A
/// request whose time runs out before its turn is never sent (<see cref="HelperBusyException"/>, the client stays open);
/// one whose time runs out once sent gives the helper up (<see cref="TimeoutException"/>). Whatever breaks the
/// protocol (an answer it refuses, of a type other than the request's, another version, a frame cut short or over the
/// cap, a sample that doesn't fit its catalogue, the pipe closing, a cancelled request) closes the client too
/// (<see cref="Closed"/>): what comes next on the pipe can't be trusted, so the helper is restarted, never resynced.
/// An <c>error</c> answer is thrown as <see cref="HelperErrorException"/>; only <c>treeFailed</c> and <c>tooLarge</c>
/// leave the client open, as they leave the helper's pipe.
/// </summary>
public sealed class SensorHelperClient : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SensorHelperClient>();

    /// <summary>
    /// Sees each answer the client accepts, as its JSON, in order: the seam a recording of the helper's catalogue and
    /// samples is made from. One that throws is logged once and called no more.
    /// </summary>
    public interface IObserver
    {
        void Answered(ReadOnlySpan<byte> json);
    }

    private readonly Stream _pipe;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IObserver? _observer;
    private volatile bool _closed;

    /// <summary>A client on a connected <paramref name="pipe"/>, which it owns.</summary>
    public SensorHelperClient(Stream pipe, TimeProvider? time = null, IObserver? observer = null)
    {
        _pipe = pipe;
        _time = time ?? TimeProvider.System;
        _observer = observer;
    }

    /// <summary>Connects to the helper serving on the pipe <paramref name="pipeName"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> isn't a name <see cref="HelperProtocol.IsPipeName"/> takes.</exception>
    /// <exception cref="TimeoutException">No helper served it within <paramref name="timeout"/>.</exception>
    public static async Task<SensorHelperClient> ConnectAsync(string pipeName, TimeSpan timeout, TimeProvider? time = null, IObserver? observer = null, CancellationToken cancel = default)
    {
        if (!HelperProtocol.IsPipeName(pipeName))
            throw new ArgumentException("The pipe's name is 1 to 64 letters, digits, '.', '_' or '-'.", nameof(pipeName));

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancel).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SensorHelperClient(pipe, time, observer);
    }

    /// <summary>The version of the last catalogue fetched (0 before any).</summary>
    public long CatalogueVersion { get; private set; }

    /// <summary>How many sensors the last catalogue fetched lists.</summary>
    public int CatalogueCount { get; private set; }

    /// <summary>Whether the client has given the helper up; every request then throws (<c>closed</c>).</summary>
    public bool Closed => _closed;

    /// <summary>Says hello at <see cref="HelperProtocol.Version"/>; a helper at another version is refused (<c>protocol</c>).</summary>
    public Task<HelloAnswer> HelloAsync(CancellationToken cancel = default) =>
        ExchangeAsync<HelloAnswer, HelloAnswer>(new HelloRequest(HelperProtocol.Version), HelperProtocol.RequestTimeout, hello => hello, cancel);

    /// <summary>
    /// The helper's catalogue now; <see cref="CatalogueVersion"/> and <see cref="CatalogueCount"/> follow it before
    /// another request can take its turn.
    /// </summary>
    public Task<CatalogueAnswer> CatalogueAsync(CancellationToken cancel = default) =>
        ExchangeAsync<CatalogueAnswer, CatalogueAnswer>(new CatalogueRequest(), HelperProtocol.RequestTimeout, catalogue =>
        {
            CatalogueVersion = catalogue.Version;
            CatalogueCount = catalogue.Sensors.Count;
            return catalogue;
        }, cancel);

    /// <summary>
    /// Every sensor's value now, given up <see cref="HelperProtocol.SampleTimeout"/> after it was asked; read against
    /// the catalogue the client holds in its turn.
    /// </summary>
    public Task<HelperSample> SampleAsync(CancellationToken cancel = default) =>
        ExchangeAsync<SampleAnswer, HelperSample>(new SampleRequest(), HelperProtocol.SampleTimeout, sample =>
        {
            if (sample.Version == CatalogueVersion && sample.Value.Count != CatalogueCount)
                throw new HelperProtocolException("malformed", $"A sample of {sample.Value.Count} values for a catalogue of {CatalogueCount} sensors.");
            return new HelperSample(sample, sample.Version != CatalogueVersion);
        }, cancel);

    /// <summary>Has the helper read storage or not, every <paramref name="storageInterval"/> seconds.</summary>
    /// <exception cref="HelperProtocolException">The interval is out of range; nothing is sent and the client stays open.</exception>
    public Task<ConfigureAnswer> ConfigureAsync(bool storage, int storageInterval, CancellationToken cancel = default) =>
        ExchangeAsync<ConfigureAnswer, ConfigureAnswer>(new ConfigureRequest(storage, storageInterval), HelperProtocol.RequestTimeout, answer => answer, cancel);

    /// <summary>
    /// One request and its answer, in the client's turn (<c>_gate</c>). Its deadline starts now, before it waits for
    /// its turn. <paramref name="accept"/> reads the answer in that turn, so the client's state it reads or sets is
    /// never seen half-changed by the next request; one that throws <see cref="HelperProtocolException"/> closes the
    /// client.
    /// </summary>
    private async Task<TResult> ExchangeAsync<T, TResult>(HelperRequest request, TimeSpan timeout, Func<T, TResult> accept, CancellationToken cancel) where T : HelperAnswer
    {
        var bytes = HelperCodec.EncodeRequest(request);
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new HelperBusyException(timeout);
        }

        try
        {
            if (_closed)
                throw new HelperProtocolException("closed", "The client gave the helper up.");
            cancel.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new HelperBusyException(timeout); // its time ran out as its turn came: still unsent

            byte[]? frame;
            try
            {
                await HelperFraming.WriteAsync(_pipe, bytes, linked.Token).ConfigureAwait(false);
                frame = await HelperFraming.ReadAsync(_pipe, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested)
            {
                Close();
                throw new TimeoutException($"The sensor helper didn't answer within {timeout.TotalSeconds:0} s.");
            }
            catch (OperationCanceledException)
            {
                Close(); // the answer may still come: the pipe is no longer in step
                throw;
            }
            catch (HelperProtocolException)
            {
                Close();
                throw;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Close();
                throw new HelperProtocolException("closed", "The sensor helper's pipe closed.");
            }

            if (frame is null)
            {
                Close();
                throw new HelperProtocolException("closed", "The sensor helper closed its pipe.");
            }

            HelperAnswer answer;
            try
            {
                answer = HelperCodec.DecodeAnswer(frame);
            }
            catch (HelperProtocolException)
            {
                Close();
                throw;
            }

            if (answer is ErrorAnswer error)
            {
                if (error.Code is not (ErrorAnswer.TreeFailed or "tooLarge"))
                    Close();
                throw new HelperErrorException(error.Code, error.Message);
            }

            if (answer is not T typed)
            {
                Close();
                throw new HelperProtocolException("unexpected", $"The sensor helper answered {answer.GetType().Name} to {request.GetType().Name}.");
            }

            if (typed is HelloAnswer hello && hello.Protocol != HelperProtocol.Version)
            {
                Close();
                throw new HelperProtocolException("protocol", $"The sensor helper speaks protocol {hello.Protocol}; the engine speaks {HelperProtocol.Version}.");
            }

            TResult result;
            try
            {
                result = accept(typed);
            }
            catch (HelperProtocolException)
            {
                Close();
                throw;
            }

            Observe(frame);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Observe(byte[] json)
    {
        try
        {
            _observer?.Answered(json);
        }
        catch (Exception e)
        {
            Logger.Warning(e, "The sensor helper's observer failed; it is called no more");
            _observer = null;
        }
    }

    private void Close()
    {
        _closed = true;
        try
        {
            _pipe.Dispose();
        }
        catch (IOException)
        {
            // Closing a broken pipe.
        }
    }

    /// <summary>Closes the pipe; the helper stops when it sees it close.</summary>
    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A sample's answer and whether it came under a catalogue version other than the client's (fetch the catalogue).</summary>
public sealed record HelperSample(SampleAnswer Answer, bool CatalogueChanged);

/// <summary>
/// A request that waited its whole deadline behind another and was never sent: the pipe is still in step, so the client
/// stays open. The request ahead of it has its own deadline and gives the helper up when it passes.
/// </summary>
public sealed class HelperBusyException(TimeSpan timeout)
    : TimeoutException($"The sensor helper was busy with another request for the whole {timeout.TotalSeconds:0} s; nothing was sent.");
