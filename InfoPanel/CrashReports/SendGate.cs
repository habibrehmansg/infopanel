using System.Net;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace InfoPanel.CrashReporting;

/// <summary>
/// One start of the reporting library's sending. Retired once, when reports are turned off or a stop passes its deadline:
/// from then on every send it gates is refused before it starts, and the one in flight is abandoned (its connection
/// cancelled), so nothing that start queued is ever sent, whatever the library still holds.
/// </summary>
internal sealed class SendGeneration
{
    private readonly CancellationTokenSource _retired = new();

    public bool Live => !_retired.IsCancellationRequested;

    public CancellationToken Retired => _retired.Token;

    public void Retire() => _retired.Cancel();

    /// <summary>Runs <paramref name="send"/> while this start lives: refused once it is retired, and abandoned when it is retired meanwhile.</summary>
    public async Task<T> Gate<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        _retired.Token.ThrowIfCancellationRequested();
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _retired.Token);
        return await send(either.Token).WaitAsync(either.Token).ConfigureAwait(false);
    }
}

/// <summary>A transport given in the options (tests'), behind a <see cref="SendGeneration"/>.</summary>
internal sealed class GatedTransport(ITransport inner, SendGeneration generation) : ITransport
{
    public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default) =>
        generation.Gate(async token =>
        {
            await inner.SendEnvelopeAsync(envelope, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}

/// <summary>The library's own HTTP connection, innermost, behind a <see cref="SendGeneration"/>.</summary>
internal sealed class GatedHandler(SendGeneration generation, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public GatedHandler(SendGeneration generation)
        : this(generation, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        generation.Gate(token => base.SendAsync(request, token), cancellationToken);
}
