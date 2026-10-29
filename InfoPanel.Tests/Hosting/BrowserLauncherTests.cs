using System.ComponentModel;
using System.Diagnostics;
using InfoPanel.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The browser launcher's failure: the error, which can name the URL and so the token, is logged to the console only;
/// the log file's branch gets a line naming the error's type and no URL. Its process start is replaced, so nothing opens.
/// </summary>
public sealed class BrowserLauncherTests
{
    private const string Url = "http://127.0.0.1:5300/auth/secret-token_42";

    [Fact]
    public void AStartThatFails_LogsTheErrorToTheConsoleOnly_AndALineWithoutTheUrlToTheFile()
    {
        var logger = new ScopedLogger();
        var launcher = new BrowserLauncher(logger, command =>
            throw new Win32Exception($"An error occurred trying to start process '{string.Join(" ", [command.FileName, .. command.ArgumentList])}'."));

        launcher.Open(Url);

        Assert.Equal(2, logger.Entries.Count);
        var console = Assert.Single(logger.Entries, e => e.Target == LogTargets.Console);
        Assert.Equal(LogLevel.Warning, console.Level);
        Assert.Contains(Url, console.Exception!.Message);
        var file = Assert.Single(logger.Entries, e => e.Target == LogTargets.File);
        Assert.Equal(LogLevel.Warning, file.Level);
        Assert.Null(file.Exception);
        Assert.Contains("Could not open the browser", file.Message);
        Assert.Contains(typeof(Win32Exception).FullName!, file.Message);
        Assert.DoesNotContain("secret-token_42", file.Message);
    }

    [Fact]
    public void AStartThatWorks_LogsNothing_AndStartsTheUrl()
    {
        var logger = new ScopedLogger();
        var started = new List<ProcessStartInfo>();
        var launcher = new BrowserLauncher(logger, command => { started.Add(command); return null; });

        launcher.Open(Url);

        var command = Assert.Single(started);
        Assert.Contains(Url, OperatingSystem.IsWindows() ? [command.FileName] : command.ArgumentList);
        Assert.Empty(logger.Entries);
    }

    /// <summary>A logger that records each entry with the <see cref="LogTargets"/> target of its scope.</summary>
    private sealed class ScopedLogger : ILogger<BrowserLauncher>
    {
        private readonly Stack<string?> _targets = new();

        public List<(LogLevel Level, string Message, Exception? Exception, string? Target)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _targets.Push(state is IReadOnlyDictionary<string, object> d && d.TryGetValue(LogTargets.Property, out var t) ? t as string : null);
            return new Pop(_targets);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception, _targets.FirstOrDefault(t => t != null)));

        private sealed class Pop(Stack<string?> targets) : IDisposable
        {
            public void Dispose() => targets.Pop();
        }
    }
}
