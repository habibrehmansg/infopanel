using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Spec;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>What one engine host's contract check saw: every (operation, status) it checked, and every violation.</summary>
public sealed class ContractLog
{
    private readonly ConcurrentDictionary<(string Operation, int Status), byte> _seen = new();
    private readonly ConcurrentQueue<string> _violations = new();

    /// <summary>Every (operation key, status) a response conformed with.</summary>
    public IReadOnlyCollection<(string Operation, int Status)> Seen => [.. _seen.Keys];

    public IReadOnlyCollection<string> Violations => [.. _violations];

    /// <summary>Every (method and path, status) the security layer answered before any operation, as it should.</summary>
    public IReadOnlyCollection<(string Request, int Status)> Refusals => [.. _refusals.Keys];

    private readonly ConcurrentDictionary<(string Request, int Status), byte> _refusals = new();

    internal void See(string operation, int status) => _seen.TryAdd((operation, status), 0);

    internal void Refused(string request, int status) => _refusals.TryAdd((request, status), 0);

    internal void Violated(string violation) => _violations.Enqueue(violation);
}

/// <summary>A response that breaks spec/openapi.json.</summary>
public sealed class ContractViolationException(string message) : Exception(message);

/// <summary>What the check sees of an exchange.</summary>
internal sealed record Exchange(string Method, string Path, int Status, string? ContentType, byte[] Body, Func<string, bool> HasHeader)
{
    public string? MediaType => ContentType?.Split(';')[0].Trim().ToLowerInvariant() is { Length: > 0 } media ? media : null;

    public string Excerpt => Body.Length == 0 ? "" : " Body: " + (Body.Length > 400 ? Encoding.UTF8.GetString(Body, 0, 400) + "…" : Encoding.UTF8.GetString(Body));
}

/// <summary>
/// Registers <see cref="ContractCheck"/> outermost in an engine host, so it sees every response as the client gets it,
/// the security layer's included.
/// </summary>
internal sealed class ContractCheckFilter(ContractLog log) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) => ContractCheck.RunAsync(context, nextMiddleware, log));
        next(app);
    };
}

/// <summary>
/// The host tests' check of every response against spec/openapi.json (test-only; the engine has no such code). The
/// response is buffered, then checked before it goes to the client:
/// <list type="bullet">
/// <item>The security layer answers before any operation: a <c>Host</c> the engine refuses gets 400 with no body, an
/// <c>Origin</c> it refuses 403 <c>problem</c>, and a cross-origin preflight 204 with no body.</item>
/// <item>A request that reaches an operation (the endpoint routing chose, or else the operation whose method and path
/// template it fills) gets a status the operation declares, with its required headers, one of its media types and, for
/// JSON, a body valid against the declared schema, through the engine's validator. Any other status fails.</item>
/// <item>A request that reaches no operation gets a <c>problem</c> under <c>/api</c>, and a <c>problem</c> or 404 with no
/// body under <c>/ws</c> and <c>/auth</c>. The web client's files aren't the API and aren't checked.</item>
/// </list>
/// A violation is recorded in the host's <see cref="ContractLog"/> and thrown, so the request fails in the test that sent
/// it (on a TestServer as the exception, on Kestrel as a 500). A WebSocket upgrade is told from its request headers, so
/// that every host treats it alike, and isn't buffered: an accepted one is recorded as the socket's 101 as it starts (its
/// messages are the test clients' to check), and a refused one is checked like any other response, but after it went
/// out, so its client may see the refusal before the check has run. A request the client aborted isn't checked.
/// </summary>
internal static class ContractCheck
{
    private const string ProblemSchema = SpecSchemas.Api + "#/$defs/problem";

    /// <summary>Whether the running test keeps its host unchecked: the <c>Category=Performance</c> tests, whose timings the buffering would skew.</summary>
    public static bool OptedOut() =>
        TestContext.Current.Test?.Traits is { } traits && traits.TryGetValue("Category", out var categories) && categories.Contains("Performance");

    public static async Task RunAsync(HttpContext context, RequestDelegate next, ContractLog log)
    {
        var response = context.Response;
        if (IsUpgrade(context))
        {
            // Unbuffered: an accepted upgrade's headers go out when the socket is accepted (on a TestServer through the
            // response body's start, which a buffer would hold back), and the request lasts as long as the socket; it is
            // the socket operation's 101, seen as it starts. A refused one passes through as it is written, and is
            // checked from a copy once it has gone out.
            response.OnStarting(() =>
            {
                if (response.StatusCode == StatusCodes.Status101SwitchingProtocols && OperationOf(context, ApiContract.Instance).Operation is { } socket)
                    log.See(socket.Key, StatusCodes.Status101SwitchingProtocols);
                return Task.CompletedTask;
            });
            var passing = response.Body;
            using var copy = new CopyingStream(passing, MaxRefusalBytes);
            response.Body = copy;
            try
            {
                await next(context);
            }
            finally
            {
                response.Body = passing;
            }

            if (response.StatusCode != StatusCodes.Status101SwitchingProtocols && !context.RequestAborted.IsCancellationRequested
                && (copy.Overflowed ? $"A refused upgrade ({context.Request.Method} {context.Request.Path}) answered over {MaxRefusalBytes} bytes." : Check(context, copy.Copied, log)) is { } refused)
            {
                log.Violated(refused);
                throw new ContractViolationException(refused);
            }

            return;
        }

        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            response.Body = original;
        }

        var body = buffer.ToArray();
        if (!context.RequestAborted.IsCancellationRequested && Check(context, body, log) is { } violation)
        {
            log.Violated(violation);
            throw new ContractViolationException(violation);
        }

        if (body.Length > 0)
        {
            try
            {
                await original.WriteAsync(body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>The most of a refused upgrade's body the check copies; a refusal is a small problem.</summary>
    private const int MaxRefusalBytes = 64 * 1024;

    /// <summary>A response body that passes every write and flush on, and keeps a copy of the first <c>cap</c> bytes written.</summary>
    private sealed class CopyingStream(Stream inner, int cap) : Stream
    {
        private readonly MemoryStream _copy = new();

        public byte[] Copied => _copy.ToArray();

        public bool Overflowed { get; private set; }

        private void Keep(ReadOnlySpan<byte> bytes)
        {
            var room = cap - (int)_copy.Length;
            if (bytes.Length > room)
                Overflowed = true;
            _copy.Write(bytes[..Math.Min(bytes.Length, Math.Max(room, 0))]);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Keep(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _copy.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Whether a request asks for a WebSocket upgrade, from its headers (HTTP/1.1 <c>Connection: Upgrade</c> and
    /// <c>Upgrade: websocket</c>) or an HTTP/2 extended CONNECT. Not <c>IsWebSocketRequest</c>: the WebSocket feature that
    /// reads is set before the pipeline on a TestServer but only by <c>UseWebSockets</c>, inside it, on Kestrel.
    /// </summary>
    internal static bool IsUpgrade(HttpContext context)
    {
        var headers = context.Request.Headers;
        var connection = headers.Connection.SelectMany(v => (v ?? "").Split(',')).Any(t => t.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase));
        if (connection && headers.Upgrade.Any(v => string.Equals(v?.Trim(), "websocket", StringComparison.OrdinalIgnoreCase)))
            return true;
        return context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpExtendedConnectFeature>() is { IsExtendedConnect: true, Protocol: var protocol }
            && string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Check(HttpContext context, byte[] body, ContractLog log)
    {
        var request = context.Request;
        var services = context.RequestServices;
        var exchange = new Exchange(request.Method, request.Path.Value ?? "/", context.Response.StatusCode, context.Response.ContentType, body,
            name => context.Response.Headers.ContainsKey(name));

        // The security layer, before any operation (spec/docs/api.md, "Where requests come from").
        string? Security(string what, int status, bool problem)
        {
            var violation = Expect(exchange, what, status, problem);
            if (violation is null)
                log.Refused($"{exchange.Method} {exchange.Path}", status);
            return violation;
        }

        var hosts = services.GetRequiredService<IOptions<HostFilteringOptions>>().Value.AllowedHosts;
        if (!hosts.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase))
            return Security($"a request for the host '{request.Host}'", StatusCodes.Status400BadRequest, problem: false);
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            var runtime = services.GetRequiredService<EngineRuntime>();
            if (origins.Count != 1 || !runtime.IsAllowedOrigin(origins[0] ?? ""))
                return Security($"a request from the origin '{origins}'", StatusCodes.Status403Forbidden, problem: true);
            if (runtime.IsCrossOrigin(origins[0]!) && CrossOrigin.AppliesTo(request.Path) && HttpMethods.IsOptions(request.Method)
                && request.Headers.ContainsKey("Access-Control-Request-Method"))
            {
                return Security("a cross-origin preflight", StatusCodes.Status204NoContent, problem: false);
            }
        }

        var contract = ApiContract.Instance;
        var (operation, unknown) = OperationOf(context, contract);
        if (unknown != null)
            return unknown;
        if (operation is null)
            return Unmatched(contract, exchange);

        var violation = Conforms(contract, operation, exchange);
        if (violation is null)
            log.See(operation.Key, exchange.Status);
        return violation;
    }

    /// <summary>What <see cref="Methods"/> names for an endpoint ASP.NET serves for every method.</summary>
    internal const string AnyMethod = "ANY";

    /// <summary>
    /// The methods an endpoint serves. One without methods, or with an empty list, serves any method: the socket, which
    /// is its <c>GET</c>, else <see cref="AnyMethod"/>, which is no operation.
    /// </summary>
    internal static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? [.. methods]
            : Template(endpoint.RoutePattern) == "/ws" ? ["GET"] : [AnyMethod];

    /// <summary>
    /// The operation a request reached: the endpoint routing chose (by <see cref="Methods"/>), or, when routing chose
    /// none (the security layer answered first) or a fallback, the operation whose method and template the request
    /// fills. A chosen endpoint that isn't in the document is a violation.
    /// </summary>
    internal static (Operation? Operation, string? Violation) OperationOf(HttpContext context, ApiContract contract)
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint && !IsFallback(endpoint))
        {
            var methods = Methods(endpoint);
            var method = methods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase) ? context.Request.Method.ToUpperInvariant() : methods[0];
            var key = $"{method} {Template(endpoint.RoutePattern)}";
            return contract.Find(key) is { } found
                ? (found, null)
                : (null, $"{key} is served by the engine ('{endpoint.DisplayName}') but isn't an operation in spec/openapi.json.");
        }

        return (contract.Match(context.Request.Method, context.Request.Path.Value ?? "/"), null);
    }

    /// <summary>The OpenAPI path template of a route: literal segments as they are, parameters as <c>{name}</c>, no trailing slash.</summary>
    internal static string Template(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart parameter => "{" + parameter.Name + "}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => throw new InvalidOperationException($"Unexpected route part in {pattern.RawText}"),
        }))));

    /// <summary>A catch-all fallback (<c>MapFallback</c>): the 404s under <c>/api</c>, <c>/ws</c> and <c>/auth</c>, and the web client's pages.</summary>
    internal static bool IsFallback(RouteEndpoint endpoint) =>
        endpoint.Order == int.MaxValue && endpoint.RoutePattern.Parameters.Any(p => p.IsCatchAll);

    /// <summary>The check of a response to <paramref name="operation"/>: null when it conforms, else what is wrong.</summary>
    internal static string? Conforms(ApiContract contract, Operation operation, Exchange r)
    {
        if (!operation.Responses.TryGetValue(r.Status, out var declared))
            return $"{operation} answered {r.Status}, a status spec/openapi.json doesn't declare for it (it declares {string.Join(", ", operation.Responses.Keys)}).{r.Excerpt}";

        foreach (var header in declared.RequiredHeaders)
        {
            if (!r.HasHeader(header))
                return $"{operation} answered {r.Status} without its {header} header.";
        }

        if (declared.Content.Count == 0)
            return r.Body.Length == 0 ? null : $"{operation} answered {r.Status} with a body ({r.ContentType}); spec/openapi.json declares none.{r.Excerpt}";

        var media = declared.Content.FirstOrDefault(c => c.Type == r.MediaType);
        if (media is null)
            return $"{operation} answered {r.Status} as {r.MediaType ?? "no media type"}; spec/openapi.json declares {string.Join(" or ", declared.Content.Select(c => c.Type))}.{r.Excerpt}";
        if (!media.IsJson || media.Schema is null || HttpMethods.IsHead(r.Method) && r.Body.Length == 0)
            return null;
        return Body(contract, media.Schema, r, operation.ToString());
    }

    private static string? Unmatched(ApiContract contract, Exchange r)
    {
        var path = new PathString(r.Path);
        var what = $"{r.Method} {r.Path}, which is no operation in spec/openapi.json,";
        if (path.StartsWithSegments("/api"))
            return Problem(contract, r, what);
        if (path.StartsWithSegments("/ws") || path.StartsWithSegments("/auth"))
            return r.Status == StatusCodes.Status404NotFound && r.Body.Length == 0 ? null : Problem(contract, r, what);
        return null;
    }

    private static string? Expect(Exchange r, string what, int status, bool problem)
    {
        if (r.Status != status)
            return $"{what} ({r.Method} {r.Path}) answered {r.Status}; the engine answers it {status} before any operation.{r.Excerpt}";
        if (problem)
            return Problem(ApiContract.Instance, r, what);
        return r.Body.Length == 0 ? null : $"{what} ({r.Method} {r.Path}) answered {status} with a body; it has none.{r.Excerpt}";
    }

    private static string? Problem(ApiContract contract, Exchange r, string what)
    {
        if (r.Status < 400)
            return $"{what} answered {r.Status}; it answers an error.";
        if (r.MediaType != "application/problem+json")
            return $"{what} answered {r.Status} as {r.MediaType ?? "no media type"}, not application/problem+json.{r.Excerpt}";
        return Body(contract, ProblemSchema, r, what);
    }

    private static string? Body(ApiContract contract, string schema, Exchange r, string what)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(r.Body, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            return $"{what} answered {r.Status} {r.MediaType} that isn't JSON: {ex.Message}{r.Excerpt}";
        }

        using (json)
        {
            var (result, at) = (contract.Schemas.Validate(json.RootElement, schema), "");
            // A documentResponse around a document of the whole jsonDepth is one level deeper than the validator reads:
            // the document is checked alone, then the envelope with a small document in its place. Any other body nested
            // past the validator's depth is a violation.
            if (result.Errors is [{ Keyword: "depth" }] && schema == DocumentResponseSchema && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("document", out var document))
            {
                (result, at) = (contract.Schemas.Validate(document, ResponseDocumentSchema), "/document");
                if (result.IsValid)
                {
                    using var envelope = JsonDocument.Parse(WithSmallDocument(json.RootElement));
                    (result, at) = (contract.Schemas.Validate(envelope.RootElement, schema), "");
                }
            }

            if (result.IsValid)
                return null;
            var name = schema.StartsWith(SpecSchemas.Api, StringComparison.Ordinal) ? schema[(schema.LastIndexOf('/') + 1)..] : "its declared schema";
            return $"{what} answered {r.Status} {r.MediaType} that isn't {name}: "
                + string.Join("; ", result.Errors.Take(5).Select(e => $"{(at + e.InstancePath is { Length: > 0 } path ? path : "(root)")} {e.Keyword}: {e.Message}"))
                + r.Excerpt;
        }
    }

    private const string DocumentResponseSchema = SpecSchemas.Api + "#/$defs/documentResponse";
    private const string ResponseDocumentSchema = SpecSchemas.Api + "#/$defs/responseDocument";

    /// <summary>A valid responseDocument: the spec's example documentResponse's document.</summary>
    private static readonly Lazy<string> SmallDocument = new(() =>
    {
        using var example = JsonDocument.Parse(File.ReadAllText(Path.Combine(ApiContract.FindSpecDir(), "examples", "api", "documentResponse", "desk.json")));
        return example.RootElement.GetProperty("document").GetRawText();
    });

    /// <summary>The members of a documentResponse body as they are, but its <c>document</c> replaced by <see cref="SmallDocument"/>.</summary>
    private static byte[] WithSmallDocument(JsonElement body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var member in body.EnumerateObject())
            {
                if (member.NameEquals("document"))
                {
                    writer.WritePropertyName(member.Name);
                    writer.WriteRawValue(SmallDocument.Value);
                }
                else
                    member.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
