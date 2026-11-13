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

        // Everything a host sends the engine is held to one budget (the wire), sized from what the engine holds while it
        // handles nothing (its pending budget, 32 MiB and 1,024 calls, PluginLimits): in any StallSeconds a host's
        // messages come to at most WireBurstBytes + 5 × WireBytesPerSecond = 16 + 15 = 31 MiB and CallsBurst + 5 ×
        // CallsPerSecond = 64 + 160 = 224 calls, each counted by the host's estimate of its bytes (an upper bound). Within
        // it, readings at most ReadingsBurstBytes + 2 MiB a second and pictures at most ImageWireBurstBytes + 2 MiB a
        // second, so neither can keep the other from the wire: each has at least 1 MiB a second of the 3. A message of
        // readings or a picture waits for room (the newest values go when it comes, those that waited first); a report
        // (an error, the host's processor use) that finds none is dropped, as the engine keeps a package's newest errors
        // only; the catalogue, once at the start, counts against it.

        /// <summary>The engine's stall the host's budget leaves room for: 5 s of its messages fit the engine's pending budget.</summary>
        public const int StallSeconds = 5;

        /// <summary>A host's messages' bytes a second (3 MiB), past its burst.</summary>
        public const long WireBytesPerSecond = 3 * 1024 * 1024;

        /// <summary>The bytes a host's messages may come to at once (16 MiB): its largest picture's, or two of its largest messages of readings.</summary>
        public const long WireBurstBytes = 16 * 1024 * 1024;

        /// <summary>A host's messages a second, past its burst.</summary>
        public const int CallsPerSecond = 32;

        /// <summary>The messages a host may send at once.</summary>
        public const int CallsBurst = 64;

        /// <summary>A host's messages of readings' bytes a second (2 MiB), past their burst.</summary>
        public const long ReadingsBytesPerSecond = 2 * 1024 * 1024;

        /// <summary>The bytes a host's messages of readings may come to at once: one of its largest (<see cref="HostMessageBytes"/>).</summary>
        public const long ReadingsBurstBytes = HostMessageBytes;

        // Pictures (PluginImage). Each goes alone in a message, as base64; it counts for the rates below, and the wire's,
        // by its wire bytes, its base64 and the message around it (ImageEnvelopeBytes, or the host's estimate when its ids
        // make that more). In a stall they come to at most 12 + 5 × 2 = 22 MiB and 16 + 5 × 16 = 96 calls, which the
        // engine admits (PluginLimits).

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

        /// <summary>The wire bytes a picture of <paramref name="bytes"/> bytes counts for: its base64 and <see cref="ImageEnvelopeBytes"/>.</summary>
        public static long ImageWireBytes(long bytes) => 4 * ((bytes + 2) / 3) + ImageEnvelopeBytes;
    }
}
