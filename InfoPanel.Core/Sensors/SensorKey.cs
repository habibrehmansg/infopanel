using InfoPanel.Enums;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Identifies one sensor reading across all sources. <see cref="Id"/> is source-specific:
    /// HWiNFO uses <c>{remote}:{id}:{instance}:{entry}</c>, Libre and plugins use their own sensor id, and the engine's
    /// own sources the 1.x plugin id form under their source id (<c>/infopanel.mock/gpu/gpu-temp</c>).
    /// The string form is <c>hwinfo:-1:1:0:7</c>, <c>libre:/amdcpu/0/load/0</c>, <c>plugin:/{plugin id}/…</c> or, for the
    /// engine's own sources, <c>infopanel:/{source}/{path}</c> (<c>infopanel:/mock/gpu/gpu-temp</c>): the source id
    /// without <c>infopanel.</c>, lowercase letters and digits in parts joined by single dashes, then a path that isn't
    /// empty. Parsing is strict and canonical (<c>ToString(Parse(s)) == s</c>): the plugin form of a built-in key
    /// (a plugin id starting <c>infopanel.</c>, any case), which no plugin may take, is refused, as is every
    /// <c>infopanel:</c> text in another shape.
    /// </summary>
    public readonly record struct SensorKey(SensorType Type, string Id)
    {
        private const string HwInfoPrefix = "hwinfo";
        private const string LibrePrefix = "libre";
        private const string PluginPrefix = "plugin";
        private const string BuiltInPrefix = "infopanel";

        // The id of a built-in source's key: the source id with its leading slash, then the path.
        private const string BuiltInId = "/infopanel.";

        public static SensorKey HwInfo(int remote, uint id, uint instance, uint entry)
        {
            return new SensorKey(SensorType.HwInfo, string.Create(CultureInfo.InvariantCulture, $"{remote}:{id}:{instance}:{entry}"));
        }

        public static SensorKey Libre(string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            return new SensorKey(SensorType.Libre, id);
        }

        public static SensorKey Plugin(string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            return new SensorKey(SensorType.Plugin, id);
        }

        public override string ToString()
        {
            if (Type == SensorType.Plugin && Id.StartsWith(BuiltInId, StringComparison.Ordinal))
                return BuiltInPrefix + ":/" + Id[BuiltInId.Length..];

            var prefix = Type switch
            {
                SensorType.HwInfo => HwInfoPrefix,
                SensorType.Libre => LibrePrefix,
                SensorType.Plugin => PluginPrefix,
                _ => Type.ToString().ToLowerInvariant(),
            };

            return $"{prefix}:{Id}";
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out SensorKey key)
        {
            key = default;

            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            var separator = s.IndexOf(':');
            if (separator <= 0 || separator == s.Length - 1)
            {
                return false;
            }

            var prefix = s[..separator];
            var id = s[(separator + 1)..];

            switch (prefix)
            {
                case HwInfoPrefix:
                    {
                        var parts = id.Split(':');
                        if (parts.Length != 4
                            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var remote)
                            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sensorId)
                            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var instance)
                            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var entry))
                        {
                            return false;
                        }

                        key = HwInfo(remote, sensorId, instance, entry);
                        return true;
                    }
                case LibrePrefix:
                    key = Libre(id);
                    return true;
                case PluginPrefix:
                    if (IsBuiltInId(id))
                    {
                        return false;
                    }

                    key = Plugin(id);
                    return true;
                case BuiltInPrefix:
                    {
                        // "/{source}/{path}": a source in the grammar, then a path that isn't empty.
                        var slash = id.Length > 1 && id[0] == '/' ? id.IndexOf('/', 1) : -1;
                        if (slash < 0 || slash == id.Length - 1 || !IsBuiltInSource(id.AsSpan(1, slash - 1)))
                        {
                            return false;
                        }

                        key = Plugin(BuiltInId + id[1..]);
                        return true;
                    }
                default:
                    return false;
            }
        }

        /// <summary>
        /// True for a key text in the plugin form (<c>plugin:/{plugin id}/…</c>) whose plugin id starts <c>infopanel.</c> in
        /// any case: the form the engine's own sources' keys had before they took <c>infopanel:/</c>, refused wherever a
        /// key is read.
        /// </summary>
        public static bool IsBuiltInInPluginForm(string? s) =>
            s is not null && s.StartsWith(PluginPrefix + ":", StringComparison.Ordinal) && IsBuiltInId(s.AsSpan(PluginPrefix.Length + 1));

        // A plugin key's id that names a built-in source, in any case.
        private static bool IsBuiltInId(ReadOnlySpan<char> id) => id.StartsWith(BuiltInId, StringComparison.OrdinalIgnoreCase);

        // A built-in source's name in a key: [a-z0-9]+(-[a-z0-9]+)*.
        private static bool IsBuiltInSource(ReadOnlySpan<char> source)
        {
            if (source.IsEmpty || source[0] == '-' || source[^1] == '-')
            {
                return false;
            }

            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '-' ? source[i - 1] == '-' : !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
