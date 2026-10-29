using System.Text.RegularExpressions;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// Pins the ASP.NET logging override in <c>Program.cs</c>. The sign-in token stays out of the log file, the recent
/// entries and <c>GET /api/logs</c> only because ASP.NET's own messages (the request path <c>/auth/{token}</c>, the
/// redirect to <c>/#token=…</c>) are below the level the engine logs at. The override is fixed in code and the
/// <c>logLevel</c> setting can't lower it; this fails if a code change does.
/// </summary>
public sealed partial class LogLevelPinTests
{
    /// <summary>Categories that carry the request path and the sign-in redirect.</summary>
    public static TheoryData<string> TokenCarryingCategories =>
    [
        "Microsoft",
        "Microsoft.AspNetCore",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Routing.EndpointMiddleware",
        "Microsoft.AspNetCore.Http.Result.RedirectResult",
        "Microsoft.AspNetCore.Mvc.Infrastructure.RedirectResultExecutor",
    ];

    [Theory]
    [MemberData(nameof(TokenCarryingCategories))]
    public void TheMicrosoftLoggingOverride_StaysAtWarningOrAbove_SoTheTokenNeverReachesTheLog(string category)
    {
        var overrides = ReadOverrides();

        // The most specific override that covers the category (Serilog's rule); with none, the engine's own level
        // (Information or lower) applies.
        var covering = overrides
            .Where(o => category == o.Prefix || category.StartsWith(o.Prefix + ".", StringComparison.Ordinal))
            .OrderByDescending(o => o.Prefix.Length)
            .Select(o => (LogEventLevel?)o.Level)
            .FirstOrDefault();

        Assert.True(covering is { } level && level >= LogEventLevel.Warning,
            $"The logging override for {category} is {(covering?.ToString() ?? "missing (the engine's own level applies)")}, below Warning. " +
            "ASP.NET's request messages (the path /auth/{token}) and the sign-in redirect to /#token=... would then reach the log file, " +
            "the recent entries and GET /api/logs with the session token in them. Keep MinimumLevel.Override(\"Microsoft\", Warning) " +
            "in Program.cs, or redact the token before the file and recent sinks.");
    }

    private static List<(string Prefix, LogEventLevel Level)> ReadOverrides()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var source = File.ReadAllText(Path.Combine(dir.FullName, "InfoPanel", "Program.cs"));
        var found = OverrideCall().Matches(source)
            .Select(m => (m.Groups["prefix"].Value, Enum.Parse<LogEventLevel>(m.Groups["level"].Value)))
            .ToList();
        Assert.True(found.Count > 0, "Program.cs has no MinimumLevel.Override calls to read: the pin can't see the Microsoft override.");
        return found;
    }

    [GeneratedRegex("""^\s*\.MinimumLevel\.Override\(\s*"(?<prefix>[^"]+)"\s*,\s*LogEventLevel\.(?<level>\w+)\s*\)""", RegexOptions.Multiline)]
    private static partial Regex OverrideCall();
}
