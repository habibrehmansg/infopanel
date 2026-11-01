using System.Collections.Generic;
using System.Globalization;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The helper's command line: <c>--pipe &lt;name&gt; --parent &lt;engine's process id&gt; --parent-start &lt;its start
/// time&gt;</c>, each once and nothing else. The pipe is a name (<see cref="HelperProtocol.IsPipeName"/>), never a path;
/// the start time is the engine's own, as <see cref="HelperParent.CurrentStartTime"/> reads it, so the helper can tell
/// the engine from a process that took its id after it ended.
/// </summary>
public sealed record HelperCommandLine(string Pipe, int Parent, long ParentStart)
{
    /// <summary>The command line <paramref name="args"/> holds, or null when it holds anything else.</summary>
    public static HelperCommandLine? Parse(string[] args)
    {
        if (args.Length != 6)
            return null;

        string? pipe = null;
        int? parent = null;
        long? start = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe" when pipe is null && HelperProtocol.IsPipeName(args[i + 1]):
                    pipe = args[i + 1];
                    break;
                case "--parent" when parent is null && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0:
                    parent = pid;
                    break;
                case "--parent-start" when start is null && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks > 0:
                    start = ticks;
                    break;
                default:
                    return null;
            }
        }

        return pipe is null || parent is null || start is null ? null : new HelperCommandLine(pipe, parent.Value, start.Value);
    }

    /// <summary>This command line as the helper's arguments, in the order <see cref="Parse"/> reads them back.</summary>
    public IReadOnlyList<string> Arguments =>
        ["--pipe", Pipe, "--parent", Parent.ToString(CultureInfo.InvariantCulture), "--parent-start", ParentStart.ToString(CultureInfo.InvariantCulture)];
}
