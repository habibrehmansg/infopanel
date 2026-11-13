using Sentry;
using Serilog.Core;
using Serilog.Events;

namespace InfoPanel.CrashReporting;

/// <summary>
/// The engine log's part in crash reports, while they are on (<see cref="Reports"/>): each entry at Information or above
/// becomes a breadcrumb holding its message template, never its rendered text or a property's value (the template is the
/// engine's own fixed text), with its source, its level and its exception's type; and an entry at Error or above with an
/// exception is reported (<see cref="CrashReports.ReportLogged"/>). Debug entries are never kept.
/// </summary>
public sealed class TemplateBreadcrumbSink : ILogEventSink
{
    /// <summary>The breadcrumb data's one member: the logged exception's type.</summary>
    public const string ExceptionTypeKey = "exception_type";

    /// <summary>The breadcrumbs' type, which the reports keep.</summary>
    public const string BreadcrumbType = "log";

    /// <summary>The reports the entries go to, once they exist; none sends nothing.</summary>
    public CrashReports? Reports { get; set; }

    public void Emit(LogEvent logEvent)
    {
        if (Reports is not { IsOn: true } reports || logEvent.Level < LogEventLevel.Information)
            return;

        var source = SourceOf(logEvent);
        var data = logEvent.Exception is { } exception
            ? new Dictionary<string, string> { [ExceptionTypeKey] = exception.GetType().FullName ?? exception.GetType().Name }
            : null;
        reports.AddBreadcrumb(new Breadcrumb(logEvent.MessageTemplate.Text, BreadcrumbType, data, source, LevelOf(logEvent.Level)));

        if (logEvent.Level >= LogEventLevel.Error && logEvent.Exception != null)
            reports.ReportLogged(logEvent.Exception, logEvent.MessageTemplate.Text, source, logEvent.Level == LogEventLevel.Fatal);
    }

    /// <summary>The entry's source context (its category), or null.</summary>
    internal static string? SourceOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value) && value is ScalarValue { Value: string context } ? context : null;

    private static BreadcrumbLevel LevelOf(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => BreadcrumbLevel.Warning,
        LogEventLevel.Error => BreadcrumbLevel.Error,
        LogEventLevel.Fatal => BreadcrumbLevel.Fatal,
        _ => BreadcrumbLevel.Info,
    };
}
