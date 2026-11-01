using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>
/// The gadget key read over <see cref="IGadgetKeyApi"/>, bounded and consistent:
/// <list type="bullet">
/// <item>the value count first: a key with more than <see cref="HwInfoGadgetSampler.MaxValues"/> isn't listed; otherwise
/// no more indices than that count are listed, whatever the key grows to meanwhile;</item>
/// <item>only HWiNFO's names (<c>Sensor{n}</c>, <c>Label{n}</c>, <c>Value{n}</c>, <c>ValueRaw{n}</c>,
/// <see cref="MaxNameLength"/> characters at most) are asked about; each one's size before its data, a text larger
/// than <see cref="MaxTextBytes"/> never read (<see cref="GadgetValueKind.TooLong"/>);</item>
/// <item>a value gone between listing and reading is left out;</item>
/// <item>the last-write time and count are read again after the values: when either moved (HWiNFO renumbering mid-read
/// would otherwise pair one reading's name with another's value), the key is read again, at most
/// <see cref="MaxRetries"/> more times, then the round is dropped (<see cref="GadgetKeyChangingException"/>).</item>
/// </list>
/// </summary>
public sealed class GadgetRegistryReader(IGadgetKeyApi api) : IGadgetRegistry
{
    /// <summary>The largest text value read: <see cref="HwInfoGadgetSampler.MaxTextLength"/> characters and a terminator.</summary>
    public const int MaxTextBytes = (HwInfoGadgetSampler.MaxTextLength + 1) * sizeof(char);

    /// <summary>The longest of HWiNFO's value names: <c>ValueRaw1023</c>.</summary>
    public const int MaxNameLength = 12;

    /// <summary>A key that changed while it was read is read again at most this many times.</summary>
    public const int MaxRetries = 3;

    public GadgetSnapshot? Read(GadgetHive hive)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var key = api.Open(hive);
            if (key == null)
                return null;

            var (count, lastWrite) = key.QueryInfo();
            if (count > HwInfoGadgetSampler.MaxValues)
                return new GadgetSnapshot(hive, lastWrite, count, []);

            var values = ReadValues(key, count);
            if (key.QueryInfo() == (count, lastWrite))
                return new GadgetSnapshot(hive, lastWrite, count, values);
            if (attempt == MaxRetries)
                throw new GadgetKeyChangingException(attempt + 1);
        }
    }

    private static List<GadgetValue> ReadValues(IGadgetOpenKey key, int count)
    {
        var values = new List<GadgetValue>();
        for (var i = 0; i < count; i++)
        {
            var found = key.EnumName(i, MaxNameLength, out var name);
            if (found == GadgetEnumResult.NoMore)
                break;
            if (found != GadgetEnumResult.Found || name == null || !HwInfoGadgetSampler.IsValueName(name))
                continue;

            // Gone since it was listed: left out.
            if (key.QueryValue(name) is not { } info)
                continue;
            if (info.Kind != GadgetValueKind.String)
                values.Add(new GadgetValue(name, GadgetValueKind.Other, null));
            else if (info.Bytes > MaxTextBytes)
                values.Add(new GadgetValue(name, GadgetValueKind.TooLong, null));
            else if (key.ReadText(name, info.Bytes) is { } text)
                values.Add(new GadgetValue(name, GadgetValueKind.String, text));
        }

        return values;
    }
}

/// <summary>The gadget key changed during every read of a round: the round is dropped.</summary>
public sealed class GadgetKeyChangingException(int reads)
    : Exception($"the key kept changing while it was read ({reads} reads): the round is dropped");
