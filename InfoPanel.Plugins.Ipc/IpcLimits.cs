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

        // Pictures (PluginImage). Each goes alone in a message, as base64; it counts for the rates below by its wire bytes,
        // its base64 and the message around it (ImageEnvelopeBytes, or the host's estimate when its ids make that more).
        // The rates are sized from the engine's pending budget (32 MiB and 1,024 calls, PluginLimits): while the engine
        // stalls, ImageStallSeconds of a host's pictures come to at most ImageWireBurstBytes + 5 × ImageWireBytesPerSecond
        // = 12 + 10 = 22 MiB and ImagesBurst + 5 × ImagesPerSecond = 96 calls, leaving 10 MiB (more than one of the host's
        // largest messages of readings, HostMessageBytes) and over 900 calls for the rest.

        /// <summary>The largest picture sent (8 MiB): its base64 is 11,184,812 bytes.</summary>
        public const int ImageBytes = PluginImage.MaxBytes;

        /// <summary>A picture message's bytes beside its base64, as counted: its ids at their longest the engine takes, escaped, and the JSON around them.</summary>
        public const long ImageEnvelopeBytes = 4 * 1024;

        /// <summary>A host's pictures' wire bytes a second (2 MiB), past its burst.</summary>
        public const long ImageWireBytesPerSecond = 2 * 1024 * 1024;

        /// <summary>The wire bytes a host's pictures may come to at once (12 MiB): the largest picture's message fits.</summary>
        public const long ImageWireBurstBytes = 12 * 1024 * 1024;

        /// <summary>A host's pictures a second, past its burst.</summary>
        public const int ImagesPerSecond = 16;

        /// <summary>The pictures a host may send at once.</summary>
        public const int ImagesBurst = 16;

        /// <summary>An entry sends at most one new picture a second, counted from the end of its last picture's send.</summary>
        public const int ImageIntervalMs = 1000;

        /// <summary>
        /// The engine's stall the rates leave room for (5 s): the engine admits a host's pictures within the same rates with
        /// this many seconds' more burst, as a stall holds them back and hands them over at once.
        /// </summary>
        public const int ImageStallSeconds = 5;

        /// <summary>The wire bytes a picture of <paramref name="bytes"/> bytes counts for: its base64 and <see cref="ImageEnvelopeBytes"/>.</summary>
        public static long ImageWireBytes(long bytes) => 4 * ((bytes + 2) / 3) + ImageEnvelopeBytes;
    }
}
