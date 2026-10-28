using System.Runtime.CompilerServices;
using InfoPanel.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Hosting;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEventsCollection
{
    /// <summary>Tests that raise or handle process-wide events (unobserved task exceptions), one at a time.</summary>
    public const string Name = "Process events";
}

/// <summary>
/// Errors nothing else catches are logged (1.4.x: <c>App.xaml.cs</c>, its <c>AppDomain</c> and
/// <c>TaskScheduler</c> handlers): an unhandled exception on any thread as <c>Fatal</c>, then the log is flushed before
/// the process ends; a faulted task nobody observed as <c>Error</c>, then observed.
/// </summary>
[Collection(ProcessEventsCollection.Name)]
public sealed class CrashLoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ILogger Logger, List<LogEvent> Events) Capture()
    {
        var events = new List<LogEvent>();
        return (new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger(), events);
    }

    [Fact]
    public async Task AFaultedTaskNobodyObserved_IsLoggedAsAnError_AndObserved()
    {
        var (logger, events) = Capture();
        var marker = "unobserved-" + Guid.NewGuid().ToString("N");
        var observed = false;
        void Check(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == marker))
                observed |= e.Observed;
        }

        using (CrashLogging.Install(logger))
        {
            // Subscribed after the engine's handler, so it sees what that handler left.
            TaskScheduler.UnobservedTaskException += Check;
            try
            {
                for (var attempt = 0; attempt < 50 && !Logged(events, marker); attempt++)
                {
                    DropAFaultedTask(marker);
                    await Task.Delay(10, Ct);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Check;
            }
        }

        Assert.True(Logged(events, marker), "The faulted task was never reported unobserved.");
        var logged = Matching(events, marker).First();
        Assert.Equal(LogEventLevel.Error, logged.Level);
        Assert.Equal(marker, logged.Exception!.Message);
        Assert.IsType<InvalidOperationException>(logged.Exception);
        Assert.True(observed);
    }

    [Fact]
    public void AnUnhandledException_IsLoggedAsFatal_WithWhetherItEndsTheProcess_AndTheLogIsFlushed()
    {
        var (logger, events) = Capture();
        var flushed = 0;
        using var crashes = CrashLogging.Install(logger, flush: () => flushed++);
        var error = new InvalidOperationException("on a worker thread");

        crashes.OnUnhandledException(this, new UnhandledExceptionEventArgs(error, isTerminating: true));

        var logged = Assert.Single(events);
        Assert.Equal(LogEventLevel.Fatal, logged.Level);
        Assert.Same(error, logged.Exception);
        Assert.Equal("True", logged.Properties["IsTerminating"].ToString());
        Assert.Equal(1, flushed);
    }

    [Fact]
    public void AnUnhandledObjectThatIsntAnException_IsLoggedToo()
    {
        var (logger, events) = Capture();
        using var crashes = CrashLogging.Install(logger, flush: () => { });

        crashes.OnUnhandledException(this, new UnhandledExceptionEventArgs("thrown from native code", isTerminating: true));

        var logged = Assert.Single(events);
        Assert.Equal(LogEventLevel.Fatal, logged.Level);
        Assert.Contains("thrown from native code", logged.RenderMessage());
    }

    [Fact]
    public void Disposing_Uninstalls()
    {
        var (logger, _) = Capture();
        var crashes = CrashLogging.Install(logger);
        Assert.True(crashes.Installed);

        crashes.Dispose();
        crashes.Dispose();

        Assert.False(crashes.Installed);
    }

    [Fact]
    public async Task TheEngineHost_InstallsCrashLogging_AndUninstallsItWhenItStops()
    {
        CrashLogging crashes;
        using (var factory = new EngineFactory())
        {
            using (var client = factory.CreateApiClient())
                Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);
            crashes = factory.Services.GetRequiredService<CrashLogging>();
            Assert.True(crashes.Installed);
        }

        Assert.False(crashes.Installed);
    }

    private static IEnumerable<LogEvent> Matching(List<LogEvent> events, string marker)
    {
        lock (events)
            return events.Where(e => e.Exception?.Message == marker).ToList();
    }

    private static bool Logged(List<LogEvent> events, string marker) => Matching(events, marker).Any();

    /// <summary>Starts a task that faults and keeps no reference to it, so the collector finalises it unobserved.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropAFaultedTask(string marker) =>
        _ = Task.Run(() => throw new InvalidOperationException(marker));

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
