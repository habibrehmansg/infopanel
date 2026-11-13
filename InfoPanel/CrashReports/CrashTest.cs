namespace InfoPanel.CrashReporting;

/// <summary>
/// <c>INFOPANEL_CRASH_TEST</c>, read only in a build that names a crash-report address, with crash reports started: 10 s
/// after start, <c>unhandled</c> throws on a background thread (an unhandled exception: a fatal report, and the engine
/// ends); <c>fail-fast</c> ends the process at once (<see cref="Environment.FailFast(string)"/>: a native crash, reported
/// at the next start as a run that ended unexpectedly). An environment variable rather than a flag, so the command line
/// and its help stay as they are. Any other value is logged and ignored.
/// </summary>
public static class CrashTest
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    public static void Run(string kind, Serilog.ILogger logger)
    {
        Action? crash = kind switch
        {
            "unhandled" => () => throw new InvalidOperationException("INFOPANEL_CRASH_TEST: an unhandled exception on a background thread"),
            "fail-fast" => () => Environment.FailFast("INFOPANEL_CRASH_TEST: fail-fast"),
            _ => null,
        };
        if (crash == null)
        {
            logger.Warning("INFOPANEL_CRASH_TEST {Kind} isn't one of unhandled and fail-fast; ignored", kind);
            return;
        }

        logger.Warning("INFOPANEL_CRASH_TEST {Kind}: the engine crashes on purpose in {Seconds} s", kind, Delay.TotalSeconds);
        new Thread(() =>
        {
            Thread.Sleep(Delay);
            crash();
        }) { IsBackground = true, Name = "INFOPANEL_CRASH_TEST" }.Start();
    }
}
