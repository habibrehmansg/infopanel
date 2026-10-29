using System.Globalization;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Spec;

namespace InfoPanel.Api;

/// <summary>
/// The engine's recent log entries (spec/docs/api.md, "Logs"), for the session token only: <c>GET /api/logs?after={seq}</c>
/// answers the run's kept entries newer than <c>after</c>, oldest first, with the newest <c>seq</c> (<see cref="RecentLog"/>).
/// </summary>
public static class LogEndpoints
{
    /// <summary>The greatest <c>after</c>: 2^53 − 1, the greatest whole number every JSON reader holds exactly.</summary>
    public const long MaxAfter = 9_007_199_254_740_991;

    private const string Time = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public static void MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs", (HttpRequest request, RecentLog log) =>
        {
            if (AfterOf(request) is not { } after)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request",
                    detail: $"Give after at most once, as a whole number from 0 to {MaxAfter} in decimal digits: the seq of the last entry the client has.");

            var (entries, latest) = log.Page(after);
            return ApiResults.Json(new LogPage
            {
                Entries = [.. entries.Select(e => new LogEntryBody
                {
                    Seq = e.Seq,
                    Time = e.Time.ToString(Time, CultureInfo.InvariantCulture),
                    Level = e.Level,
                    Source = e.Source,
                    Message = e.Message,
                    Exception = e.Exception,
                })],
                Latest = latest,
            });
        }).RequireScope(ApiScope.Session);
    }

    /// <summary>
    /// The request's <c>after</c>: 0 when absent; null (a 400) when it is given more than once, or isn't 1 or more decimal
    /// digits (no sign, space or exponent) for a number from 0 to <see cref="MaxAfter"/>.
    /// </summary>
    private static long? AfterOf(HttpRequest request)
    {
        if (!request.Query.TryGetValue("after", out var values))
            return 0;
        if (values.Count != 1 || values[0] is not { Length: > 0 } text || !text.All(char.IsAsciiDigit))
            return null;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var after) && after <= MaxAfter ? after : null;
    }
}
