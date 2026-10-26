using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// Temperatures and fans from the SMC (<see cref="Smc"/>; private, no root). Key names are undocumented and differ by
/// chip, so temperatures are aggregates over key families found at start-up, not single keys: CPU over every
/// <c>Tp*</c> (P-core) and <c>Te*</c> (E-core) <c>flt</c> key, GPU over every <c>Tg*</c> key, each as an average and a
/// maximum of the keys that read a plausible value this round. Gated clusters' keys read placeholders (-4, 0, 1.5, 5.2,
/// exactly 40 °C on an M4 Max at idle), so only values in 10–125 °C other than exactly 40 count; a round with none
/// fails the sensor (stale, then unavailable while the cluster stays gated). Fans: <c>FNum</c>, then <c>F{n}Ac</c>
/// (actual RPM); battery: <c>TB0T</c>. Polled every other second (about 160 SMC calls). An SMC open the OS refuses lists the CPU and GPU
/// temperatures as permission-denied.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SmcReader(MacHostInfo host) : MacReader
{
    private const double MinPlausible = 10;
    private const double MaxPlausible = 125;
    private const double GatedPlaceholder = 40;

    private sealed class Family(int average, int maximum, SmcKeyInfo[] keys)
    {
        public readonly int Average = average;
        public readonly int Maximum = maximum;
        public readonly SmcKeyInfo[] Keys = keys;
    }

    private Smc? _smc;
    private bool _denied;
    private readonly List<Family> _families = [];
    private readonly List<(int Index, SmcKeyInfo Key)> _fans = [];
    private (int Index, SmcKeyInfo Key)? _battery;

    public override int Every => 2;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        _smc = Smc.Open(out var kr);
        if (_smc == null)
        {
            if (IOKit.IsDenied(kr))
            {
                // Without the SMC nothing can be enumerated; list what every Apple Silicon Mac has, as refused.
                _denied = true;
                Add(definitions, "cpu/temperature", host.CpuGroup, "CPU Temperature (avg)", "°C");
                Add(definitions, "gpu/temperature", host.GpuGroup, "GPU Temperature (avg)", "°C");
            }

            return;
        }

        var cpu = new List<SmcKeyInfo>();
        var gpu = new List<SmcKeyInfo>();
        foreach (var key in _smc.EnumerateKeys())
        {
            var family = (byte)(key >> 24) == 'T' ? (char)(byte)(key >> 16) : '\0';
            if (family is not ('p' or 'e' or 'g'))
                continue;
            if (!_smc.TryGetInfo(key, out var info, out _) || info.Type != Smc.TypeFlt)
                continue;
            (family == 'g' ? gpu : cpu).Add(info);
        }

        if (cpu.Count > 0)
        {
            _families.Add(new Family(
                Add(definitions, "cpu/temperature", host.CpuGroup, "CPU Temperature (avg)", "°C"),
                Add(definitions, "cpu/temperature-max", host.CpuGroup, "CPU Temperature (max)", "°C"),
                cpu.ToArray()));
        }

        if (gpu.Count > 0)
        {
            _families.Add(new Family(
                Add(definitions, "gpu/temperature", host.GpuGroup, "GPU Temperature (avg)", "°C"),
                Add(definitions, "gpu/temperature-max", host.GpuGroup, "GPU Temperature (max)", "°C"),
                gpu.ToArray()));
        }

        if (_smc.TryGetInfo(Smc.FourCC("FNum"), out var fnum, out _) && _smc.TryRead(fnum, out var fans, out _))
        {
            for (var i = 0; i < (int)Math.Clamp(fans, 0, 9); i++)
            {
                if (_smc.TryGetInfo(Smc.FourCC(string.Create(CultureInfo.InvariantCulture, $"F{i}Ac")), out var actual, out _))
                {
                    var index = Add(definitions, string.Create(CultureInfo.InvariantCulture, $"fans/{i}/speed"), "Fans",
                        string.Create(CultureInfo.InvariantCulture, $"Fan {i + 1} Speed"), "RPM");
                    _fans.Add((index, actual));
                }
            }
        }

        if (_smc.TryGetInfo(Smc.FourCC("TB0T"), out var battery, out _) && _smc.TryRead(battery, out _, out _))
            _battery = (Add(definitions, "battery/temperature", "Battery", "Battery Temperature", "°C"), battery);
    }

    /// <summary>Whether an SMC temperature value counts (see the class summary).</summary>
    internal static bool IsPlausibleTemperature(double value) =>
        value is >= MinPlausible and <= MaxPlausible && value != GatedPlaceholder;

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        if (_denied)
        {
            foreach (ref var s in mine)
                s.Outcome = SampleOutcome.PermissionDenied;
            return;
        }

        var smc = _smc!;
        foreach (var family in _families)
        {
            double sum = 0, max = double.MinValue;
            var count = 0;
            var refused = false;
            foreach (var key in family.Keys)
            {
                if (smc.TryRead(key, out var value, out var kr))
                {
                    if (IsPlausibleTemperature(value))
                    {
                        sum += value;
                        max = Math.Max(max, value);
                        count++;
                    }
                }
                else if (IOKit.IsDenied(kr))
                {
                    refused = true;
                    break;
                }
            }

            if (count > 0)
            {
                mine[family.Average].Set(sum / count);
                mine[family.Maximum].Set(max);
            }
            else
            {
                var outcome = refused ? SampleOutcome.PermissionDenied : SampleOutcome.Failed;
                mine[family.Average].Outcome = outcome;
                mine[family.Maximum].Outcome = outcome;
            }
        }

        foreach (var (index, key) in _fans)
        {
            if (smc.TryRead(key, out var rpm, out var kr) && rpm >= 0 && rpm < 100_000)
                mine[index].Set(rpm);
            else
                mine[index].Outcome = IOKit.IsDenied(kr) ? SampleOutcome.PermissionDenied : SampleOutcome.Failed;
        }

        if (_battery is { } battery)
        {
            // Not a gated cluster: only the range applies (a battery can be at exactly 40 °C).
            if (smc.TryRead(battery.Key, out var celsius, out var kr) && celsius is >= MinPlausible and <= MaxPlausible)
                mine[battery.Index].Set(celsius);
            else
                mine[battery.Index].Outcome = IOKit.IsDenied(kr) ? SampleOutcome.PermissionDenied : SampleOutcome.Failed;
        }
    }

    public override void Dispose()
    {
        _smc?.Dispose();
        _smc = null;
    }
}
