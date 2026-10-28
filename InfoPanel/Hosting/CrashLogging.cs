namespace InfoPanel.Hosting;

/// <summary>
/// Logs the errors nothing else catches (1.4.x: <c>App.xaml.cs</c>'s <c>AppDomain</c> and <c>TaskScheduler</c> handlers):
/// <list type="bullet">
/// <item>an exception unhandled on any thread (<see cref="AppDomain.UnhandledException"/>): <c>Fatal</c>, with whether
/// it ends the process, and then, when it does, <c>flush</c> runs so the line reaches the log file first;</item>
/// <item>a faulted task nobody observed (<see cref="TaskScheduler.UnobservedTaskException"/>, raised when the task is
/// collected): each of its exceptions at <c>Error</c>, then it is marked observed.</item>
/// </list>
/// Installed by the engine right after its logger is made; disposing (when the host stops) removes the handlers.
/// Nothing is sent anywhere.
/// </summary>
public sealed class CrashLogging : IDisposable
{
    private readonly Serilog.ILogger _logger;
    private readonly Action? _flush;
    private int _installed;

    private CrashLogging(Serilog.ILogger logger, Action? flush)
    {
        _logger = logger;
        _flush = flush;
    }

    /// <summary>Whether the handlers are subscribed.</summary>
    public bool Installed => Volatile.Read(ref _installed) == 1;

    /// <param name="logger">Where the errors are logged.</param>
    /// <param name="flush">Writes out everything logged so far, before a process ends (the engine disposes its logger).</param>
    public static CrashLogging Install(Serilog.ILogger logger, Action? flush = null)
    {
        var crashes = new CrashLogging(logger, flush);
        AppDomain.CurrentDomain.UnhandledException += crashes.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += crashes.OnUnobservedTaskException;
        Volatile.Write(ref crashes._installed, 1);
        return crashes;
    }

    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            _logger.Fatal(exception, "An unhandled exception (IsTerminating: {IsTerminating})", e.IsTerminating);
        else
            _logger.Fatal("An unhandled exception that isn't an Exception: {Thrown} (IsTerminating: {IsTerminating})", e.ExceptionObject, e.IsTerminating);

        if (!e.IsTerminating)
            return;
        try
        {
            _flush?.Invoke();
        }
        catch
        {
            // The process is ending; a failing flush must not hide the original error.
        }
    }

    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var exception in e.Exception.InnerExceptions)
            _logger.Error(exception, "A background task failed and nothing observed it");
        e.SetObserved();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
            return;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}
