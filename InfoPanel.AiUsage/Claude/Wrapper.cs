using System.Text;

namespace InfoPanel.AiUsage;

/// <summary>
/// The status-line wrapper Connect Claude Code limits writes beside a settings file (docs/2.0/ai-usage.md): a bash script
/// that copies what Claude Code gives the status line (its stdin, one JSON object) to <c>infopanel-usage.json</c> in the
/// Claude folder it runs for, then runs the user's own command with the same input, so its output, its stderr and its
/// exit code are the command's. bash, <c>mktemp</c> and <c>mv</c> (the new usage file and the rename that puts it in
/// place), and <c>rm</c> only when those fail (the new file taken away): it keeps working without InfoPanel. With no status line before, a minimal one stands in for the user's command.
/// </summary>
internal static class Wrapper
{
    /// <summary>The wrapper's file name beside a settings file named <c>settings.json</c>.</summary>
    public const string FileName = "infopanel-statusline.sh";

    /// <summary>What every wrapper's second line says, and how a command that runs one is told apart.</summary>
    public const string Marker = "infopanel-statusline";

    /// <summary>
    /// The script for <paramref name="command"/> (the user's status line command, or null for none), writing the usage
    /// file in <c>CLAUDE_CONFIG_DIR</c> when Claude Code runs with it, else in <paramref name="defaultFolder"/> (a
    /// shell word: <c>$HOME/.claude</c>). Null when the command can't be put in a bash word (it holds a NUL).
    /// </summary>
    public static string? Script(string? command, string defaultFolder = "$HOME/.claude")
    {
        if (command != null && command.Contains('\0'))
            return null;
        var script = new StringBuilder();
        script.Append("#!/bin/bash\n");
        script.Append("# Written by InfoPanel's AI usage plugin (Connect Claude Code limits). Disconnect in InfoPanel puts your status line back.\n");
        script.Append("umask 077\n");
        script.Append("IFS= read -r -d '' input || true\n");
        // The usage file: written to a new private file of its own (mktemp: a regular file, made only where nothing was,
        // 0600), then renamed over it, so a link there is replaced, never followed, a FIFO never waited on, and the file is
        // 0600 whatever it was. The user's command reads that file, so its input is the bytes Claude Code gave, exactly.
        script.Append("__infopanel_f=\"${CLAUDE_CONFIG_DIR:-").Append(defaultFolder).Append("}/").Append(LimitsReader.FileName).Append("\"\n");
        script.Append("if __infopanel_t=$(mktemp \"${__infopanel_f%/*}/.").Append(LimitsReader.FileName).Append(".XXXXXXXXXX\" 2>/dev/null) && { printf '%s' \"$input\" > \"$__infopanel_t\"; } 2>/dev/null; then\n");
        if (command != null)
            script.Append("  exec 3< \"$__infopanel_t\"\n");
        script.Append("  { [[ ! -d $__infopanel_f ]] && mv -f -- \"$__infopanel_t\" \"$__infopanel_f\"; } 2>/dev/null || rm -f -- \"$__infopanel_t\"\n");
        script.Append("else\n");
        script.Append("  [[ -z $__infopanel_t ]] || rm -f -- \"$__infopanel_t\" 2>/dev/null\n");
        if (command != null)
            script.Append("  exec 3< <(printf '%s' \"$input\" 2>/dev/null)\n"); // A command that doesn't read it closes the pipe: no error then.
        script.Append("fi\n");
        if (command != null)
        {
            script.Append("eval ").Append(Quote(command)).Append(" <&3 3<&-\n");
        }
        else
        {
            // No status line before: the limits alone, read with bash's own regular expressions.
            script.Append("f=–; w=–\n");
            script.Append("[[ $input =~ \\\"five_hour\\\":[[:space:]]*\\{[^}]*\\\"used_percentage\\\":[[:space:]]*([0-9]+) ]] && f=${BASH_REMATCH[1]}%\n");
            script.Append("[[ $input =~ \\\"seven_day\\\":[[:space:]]*\\{[^}]*\\\"used_percentage\\\":[[:space:]]*([0-9]+) ]] && w=${BASH_REMATCH[1]}%\n");
            script.Append("printf '5h %s · wk %s\\n' \"$f\" \"$w\"\n");
        }
        return script.ToString();
    }

    /// <summary><paramref name="text"/> as one bash word, in single quotes: every <c>'</c> in it as <c>'\''</c>; nothing else changes meaning there.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The status line command that runs the wrapper at <paramref name="path"/>: <c>bash '/…/infopanel-statusline.sh'</c>;
    /// on Windows, where Claude Code runs it through Git Bash, <c>bash "C:/…/infopanel-statusline.sh"</c> (forward
    /// slashes, double quotes; null for a path a double-quoted word would change: one holding <c>"</c>, <c>$</c>,
    /// <c>`</c>, <c>%</c> or <c>!</c>).
    /// </summary>
    public static string? Command(string path, bool windows)
    {
        if (!windows)
            return "bash " + Quote(path);
        if (path.IndexOfAny(['"', '$', '`', '%', '!', '\0']) >= 0)
            return null;
        return "bash \"" + path.Replace('\\', '/') + "\"";
    }
}
