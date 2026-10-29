using System.Globalization;
using InfoPanel.Hosting;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The engine's log file rolls over to the day's next file a bounded number of times: when the day's files are full up
/// to the last sequence number a name can hold, or past the attempts it makes, the file log is off for the run and the
/// engine says so once on the console, at once, never after a long loop of opens.
/// </summary>
public sealed class EngineLogFileTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "infopanel-log-tests-" + Guid.NewGuid().ToString("N"), "logs");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_logs)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static LogEvent Event(string text) =>
        new(DateTimeOffset.Now, LogEventLevel.Information, null, new MessageTemplateParser().Parse(text), []);

    private void Full(string suffix)
    {
        using var file = File.Create(Path.Combine(_logs, $"infopanel-{DateTime.Now.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{suffix}.log"));
        file.SetLength(EngineLogFile.FileSizeLimit); // sparse: full by size, no bytes written
    }

    [Theory]
    [InlineData("_2147483647")] // the last sequence a name holds: one more would overflow
    [InlineData("_99999999999")] // a sequence past what a name holds, read as the last one
    public void TheDaysFilesFull_ToTheLastSequence_TurnTheFileLogOff_WithOneWarning(string last)
    {
        Directory.CreateDirectory(_logs);
        Full("");
        Full(last);
        if (last != "_2147483647")
            Full("_2147483647");
        var warnings = new List<string>();
        var sink = new EngineLogFile(_logs, warnings.Add);

        var emit = Task.Run(() =>
        {
            sink.Emit(Event("first"));
            sink.Emit(Event("second"));
        }, TestContext.Current.CancellationToken);

        Assert.True(emit.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the rollover kept opening files");
        Assert.True(sink.IsOff);
        Assert.Contains("console", Assert.Single(warnings));
        Assert.All(Directory.GetFiles(_logs), file => Assert.Equal(EngineLogFile.FileSizeLimit, new FileInfo(file).Length));
        sink.Dispose();
    }
}
