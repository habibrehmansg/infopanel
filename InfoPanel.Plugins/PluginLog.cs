namespace InfoPanel.Plugins
{
    /// <summary>The level of a <see cref="PluginLog"/> line.</summary>
    public enum PluginLogLevel
    {
        Debug,
        Information,
        Warning,
        Error,
    }

    /// <summary>
    /// A plugin's log. In InfoPanel each line goes to its package's log (<c>logs/plugins/{package}-{date}.log</c> in the
    /// engine's data folder), at the engine's log level, so a Debug line is kept only while the engine logs at Debug; outside
    /// InfoPanel (the simulator, a plugin's own tests) a line goes nowhere. Safe from any thread; a message is cut at
    /// <see cref="MaxMessageChars"/>. Never log a secret (a setting's value, a token) or a user's data.
    /// </summary>
    public static class PluginLog
    {
        /// <summary>The most characters of a message kept; the rest is cut.</summary>
        public const int MaxMessageChars = 4096;

        private static volatile Action<PluginLogLevel, string, Exception?>? _target;

        public static void Debug(string message) => Write(PluginLogLevel.Debug, message, null);

        public static void Info(string message) => Write(PluginLogLevel.Information, message, null);

        public static void Warning(string message) => Write(PluginLogLevel.Warning, message, null);

        public static void Error(string message, Exception? exception = null) => Write(PluginLogLevel.Error, message, exception);

        private static void Write(PluginLogLevel level, string? message, Exception? exception)
        {
            if (_target is not { } target)
                return;
            message ??= "";
            if (message.Length > MaxMessageChars)
                message = message[..MaxMessageChars];
            try
            {
                target(level, message, exception);
            }
            catch
            {
                // A log that fails never fails the plugin.
            }
        }

        /// <summary>Where lines go from now on (the plugin host's log); null: nowhere.</summary>
        internal static void Attach(Action<PluginLogLevel, string, Exception?>? target) => _target = target;
    }
}
