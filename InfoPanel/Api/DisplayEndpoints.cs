using InfoPanel.Desktop;
using InfoPanel.Security;
using InfoPanel.Spec;

namespace InfoPanel.Api;

/// <summary>
/// The computer's displays (spec/docs/api.md, "Displays"), for every token: <c>GET /api/displays</c> reads them from the
/// platform after each request comes, on its UI thread (<see cref="DisplayLister"/>: requests while a listing is posted
/// share it, and one nobody waits for any more isn't read), the primary first, each with exactly
/// the <c>displayMatch</c> a profile's <c>window.display</c> takes (<see cref="DisplayMatcher.TargetOf"/>). Where the engine
/// shows no desktop windows (<see cref="DisplayWindowManager.Enabled"/>) it answers <c>supported</c> false and no displays.
/// A UI thread that doesn't answer within <see cref="Wait"/>, or a platform that throws, is logged and answered 500.
/// </summary>
public static class DisplayEndpoints
{
    /// <summary>How long a request waits for the UI thread to list the displays.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public static void MapDisplayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/displays", async (DisplayWindowManager windows, DisplayLister lister, TimeProvider time, ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            if (!windows.Enabled)
                return ApiResults.Json(new DisplayList { Supported = false, Displays = [] });

            IReadOnlyList<DisplayInfo> displays;
            try
            {
                displays = await lister.ListAsync(Wait, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                Logger(loggers).LogError("The desktop thread didn't list the displays within {Wait} s", Wait.TotalSeconds);
                return Failed();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger(loggers).LogError(ex, "Could not list the displays");
                return Failed();
            }

            return ApiResults.Json(new DisplayList { Supported = true, Displays = [.. displays.Select(Body)] });
        }).RequireScope(ApiScope.Read);
    }

    /// <summary>One display's entry: the platform lists the primary first, so the first is the primary.</summary>
    private static DisplayInfoBody Body(DisplayInfo display, int index)
    {
        var match = DisplayMatcher.TargetOf(display);
        var label = display.DisplayLabel;
        return new DisplayInfoBody
        {
            Name = match.Name,
            Width = match.Width,
            Height = match.Height,
            X = match.X!.Value,
            Y = match.Y!.Value,
            Label = DisplayMatcher.Cut(label),
            Scale = display.Scale,
            Primary = index == 0,
        };
    }

    private static ILogger Logger(ILoggerFactory loggers) => loggers.CreateLogger(typeof(DisplayEndpoints).FullName!);

    private static IResult Failed() => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal Server Error",
        detail: "The engine couldn't list the displays; see its log.");
}
