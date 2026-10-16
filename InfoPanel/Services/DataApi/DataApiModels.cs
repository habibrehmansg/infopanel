using System.Collections.Generic;

namespace InfoPanel.DataApi
{
    /// <summary>One sensor reading as the Data API serves it. A record, so a filtered stream can send only changed values.</summary>
    public sealed record DataApiValue
    {
        public double? Value { get; init; }
        public string? Text { get; init; }
        public string Unit { get; init; } = "";
        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Avg { get; init; }

        /// <summary>Human-readable display name.</summary>
        public string? Name { get; init; }

        /// <summary>
        /// Where the value came from: "infopanel" (plugins, LibreHardwareMonitor,
        /// HWiNFO), "system", or the source label of an external push.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// The sensor's native InfoPanel binding, so a client can match a key
        /// to a profile item: "libre:{identifier}", "hwinfo:{remote}/{id}/{instance}/{entry}"
        /// or "plugin:{sensor id}". Null for system and pushed values. Sent by
        /// /api/sensors only; the event streams leave it out.
        /// </summary>
        public string? Ref { get; init; }
    }

    /// <summary>Freshness of one external push source.</summary>
    public sealed class PushSourceStatus
    {
        /// <summary>Unix time (ms) of the source's last push.</summary>
        public long LastSeen { get; init; }

        /// <summary>Number of keys the source currently owns.</summary>
        public int Keys { get; init; }
    }

    /// <summary>The full payload of /api/sensors and of each /events message.</summary>
    public sealed class DataApiSnapshot
    {
        /// <summary>Unix time (ms) the snapshot was built.</summary>
        public long Timestamp { get; init; }

        public Dictionary<string, DataApiValue> Sensors { get; init; } = [];

        /// <summary>External push sources only (see PushStore).</summary>
        public Dictionary<string, PushSourceStatus> Sources { get; init; } = [];
    }
}
