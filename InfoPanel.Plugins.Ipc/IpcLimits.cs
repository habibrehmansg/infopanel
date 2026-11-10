namespace InfoPanel.Plugins.Ipc
{
    /// <summary>
    /// The bounds the engine holds a plugin host's messages to, shared so the host keeps within them: the engine closes a
    /// host's connection for a message over <see cref="MessageBytes"/> and cuts texts, names and tables past the rest, and
    /// the host makes the same cuts before it sends, and keeps each message well under the engine's limit.
    /// </summary>
    public static class IpcLimits
    {
        /// <summary>The largest message the engine reads from a host (16 MiB).</summary>
        public const long MessageBytes = 16 * 1024 * 1024;

        /// <summary>A text's value is cut to this many characters (4 KiB).</summary>
        public const int TextChars = 4096;

        /// <summary>A table keeps this many rows.</summary>
        public const int TableRows = 1000;

        /// <summary>A table keeps this many columns.</summary>
        public const int TableColumns = 64;

        /// <summary>A table cell's text is cut to this many characters (1 KiB).</summary>
        public const int CellChars = 1024;

        /// <summary>Names, units and a table's column names are cut to this many characters.</summary>
        public const int NameChars = 256;

        /// <summary>
        /// The most the host puts in one message, by its estimate (an upper bound): half the engine's limit. More changes
        /// than that in one cycle go in several messages.
        /// </summary>
        public const long HostMessageBytes = MessageBytes / 2;

        /// <summary>The most one table comes to in a message, by the same estimate: a table past it keeps its first rows that fit.</summary>
        public const long HostTableBytes = MessageBytes / 4;
    }
}
