using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// A plugin table with its column names and each cell as InfoPanel
        /// draws it (null where it draws nothing). <see cref="Text"/> keeps
        /// the flattened form for older clients.
        /// </summary>
        public DataApiTable? Table { get; init; }
    }

    /// <summary>A plugin table, compared by content so a filtered stream only resends changed tables.</summary>
    public sealed record DataApiTable
    {
        public IReadOnlyList<string> Columns { get; init; } = [];
        public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = [];

        /// <summary>
        /// The plugin's default column format (IPluginTable.DefaultFormat), which
        /// InfoPanel gives a new table sensor item as its TableFormat. Null when
        /// the plugin sets none.
        /// </summary>
        public string? Format { get; init; }

        public bool Equals(DataApiTable? other) =>
            other is not null
            && Format == other.Format
            && Columns.SequenceEqual(other.Columns)
            && Rows.Count == other.Rows.Count
            && Rows.Zip(other.Rows).All(pair => pair.First.SequenceEqual(pair.Second));

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var column in Columns) hash.Add(column);
            hash.Add(Rows.Count);
            hash.Add(Format);
            return hash.ToHashCode();
        }
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
