using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// Battery and input power from the <c>AppleSmartBattery</c> service's registry properties (a public IOKit call; the
/// keys are undocumented): <c>CurrentCapacity</c>/<c>MaxCapacity</c> (percent on Apple Silicon, mAh on Intel, so the
/// ratio is the level), <c>Voltage</c> (mV), <c>Amperage</c> (mA, negative while discharging), <c>CycleCount</c>,
/// <c>IsCharging</c>, <c>ExternalConnected</c>, <c>TimeRemaining</c> (minutes; 65535 while it can't tell, which reads
/// as a failed sample) and <c>PowerTelemetryData.SystemPowerIn</c> (mW from the adapter). Macs without a battery list
/// none of these.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class BatteryReader : MacReader
{
    private const string Group = "Battery";

    private static readonly IntPtr Current = CF.Str("CurrentCapacity");
    private static readonly IntPtr Max = CF.Str("MaxCapacity");
    private static readonly IntPtr Voltage = CF.Str("Voltage");
    private static readonly IntPtr Amperage = CF.Str("Amperage");
    private static readonly IntPtr Cycles = CF.Str("CycleCount");
    private static readonly IntPtr Charging = CF.Str("IsCharging");
    private static readonly IntPtr External = CF.Str("ExternalConnected");
    private static readonly IntPtr TimeRemaining = CF.Str("TimeRemaining");
    private static readonly IntPtr Telemetry = CF.Str("PowerTelemetryData");
    private static readonly IntPtr SystemPowerIn = CF.Str("SystemPowerIn");

    private uint _service;
    private int _level, _voltage, _current, _cycles, _charging, _external, _remaining, _powerIn = -1;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        _service = IOKit.FirstService("AppleSmartBattery");
        if (_service == 0 || !TryGet(Max, out _))
            return;

        _level = Add(definitions, "battery/level", Group, "Battery Level", "%");
        _voltage = Add(definitions, "battery/voltage", Group, "Battery Voltage", "V");
        _current = Add(definitions, "battery/current", Group, "Battery Current", "A");
        _cycles = Add(definitions, "battery/cycles", Group, "Battery Cycle Count", "");
        _charging = Add(definitions, "battery/charging", Group, "Battery Charging", "");
        _external = Add(definitions, "battery/external-power", Group, "On External Power", "");
        _remaining = Add(definitions, "battery/time-remaining", Group, "Battery Time Remaining", "min");
        if (ReadPowerIn(out _))
            _powerIn = Add(definitions, "power/system-in", "Power", "System Power In", "W");
    }

    private bool TryGet(IntPtr key, out double value)
    {
        var cf = IOKit.IORegistryEntryCreateCFProperty(_service, key, IntPtr.Zero, 0);
        if (cf == IntPtr.Zero)
        {
            value = 0;
            return false;
        }

        try
        {
            // Integers through SInt64, so a negative Amperage stored as a large unsigned value comes out negative.
            if (CF.TryGetInt64(cf, out var l))
            {
                value = l;
                return true;
            }

            return CF.TryGetDouble(cf, out value);
        }
        finally
        {
            CF.CFRelease(cf);
        }
    }

    private bool ReadPowerIn(out double milliwatts)
    {
        milliwatts = 0;
        var telemetry = IOKit.IORegistryEntryCreateCFProperty(_service, Telemetry, IntPtr.Zero, 0);
        if (telemetry == IntPtr.Zero)
            return false;
        try
        {
            return CF.TryGetDouble(telemetry, SystemPowerIn, out milliwatts);
        }
        finally
        {
            CF.CFRelease(telemetry);
        }
    }

    private void Put(Span<SensorSample> mine, int index, IntPtr key, double scale = 1)
    {
        if (TryGet(key, out var value))
            mine[index].Set(value * scale);
        else
            mine[index].Outcome = SampleOutcome.Failed;
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        if (TryGet(Current, out var current) && TryGet(Max, out var max) && max > 0)
            mine[_level].Set(Math.Clamp(current / max * 100, 0, 100));
        else
            mine[_level].Outcome = SampleOutcome.Failed;

        Put(mine, _voltage, Voltage, 0.001);
        Put(mine, _current, Amperage, 0.001);
        Put(mine, _cycles, Cycles);
        Put(mine, _charging, Charging);
        Put(mine, _external, External);
        if (TryGet(TimeRemaining, out var minutes) && minutes is >= 0 and < 65535)
            mine[_remaining].Set(minutes);
        else
            mine[_remaining].Outcome = SampleOutcome.Failed;

        if (_powerIn >= 0)
        {
            if (ReadPowerIn(out var mw))
                mine[_powerIn].Set(mw / 1000);
            else
                mine[_powerIn].Outcome = SampleOutcome.Failed;
        }
    }

    public override void Dispose()
    {
        if (_service != 0)
        {
            IOKit.IOObjectRelease(_service);
            _service = 0;
        }
    }
}
