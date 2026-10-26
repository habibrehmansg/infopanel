using InfoPanel.Sensors.MacOS;
using Xunit;

// Pure decoding helpers of the macOS readers: no native call, so they run on every OS.
#pragma warning disable CA1416

namespace InfoPanel.Core.Tests.Sensors;

public class MacSensorDecodingTests
{
    [Fact]
    public void Smc_DecodesEachNumericType()
    {
        Assert.True(Smc.Decode(Smc.TypeFlt, BitConverter.GetBytes(83.5f), out var flt));
        Assert.Equal(83.5, flt);
        Assert.True(Smc.Decode(Smc.TypeSp78, [0x2F, 0x80], out var sp78));
        Assert.Equal(47.5, sp78);
        Assert.True(Smc.Decode(Smc.TypeFpe2, [0x24, 0x38], out var fpe2));
        Assert.Equal(2318, fpe2);
        Assert.True(Smc.Decode(Smc.TypeUi8, [2], out var ui8));
        Assert.Equal(2, ui8);
        Assert.True(Smc.Decode(Smc.TypeUi32, [0, 0, 0x0D, 0xA5], out var ui32));
        Assert.Equal(3493, ui32);
        Assert.True(Smc.Decode(Smc.TypeSi16, [0xFF, 0xFE], out var si16));
        Assert.Equal(-2, si16);

        Assert.False(Smc.Decode(Smc.FourCC("ch8*"), [1, 2, 3, 4], out _));
        Assert.False(Smc.Decode(Smc.TypeFlt, [1, 2], out _));
        Assert.False(Smc.Decode(Smc.TypeFlt, BitConverter.GetBytes(float.NaN), out _));
        Assert.Equal("TCMz", Smc.KeyName(Smc.FourCC("TCMz")));
    }

    [Theory]
    [InlineData(55.3, true)]
    [InlineData(10, true)]
    [InlineData(125, true)]
    [InlineData(40, false)] // the placeholder gated P-core keys read
    [InlineData(-4, false)]
    [InlineData(1.5, false)]
    [InlineData(5.2, false)]
    [InlineData(126, false)]
    public void Smc_PlausibleTemperatures(double value, bool plausible) =>
        Assert.Equal(plausible, SmcReader.IsPlausibleTemperature(value));

    [Fact]
    public void DvfsTables_AreMHz_WhateverTheirUnit()
    {
        static byte[] Table(params uint[] frequencies) =>
            frequencies.SelectMany(f => BitConverter.GetBytes(f).Concat(BitConverter.GetBytes(800_000u))).ToArray();

        // M4 CPU tables are kHz, GPU tables Hz with a leading 0 entry; M1–M3 CPU tables are Hz.
        Assert.Equal([1020, 2592], IOReportReader.ToMHz(Table(1_020_000, 2_592_000)));
        Assert.Equal([338, 1578], IOReportReader.ToMHz(Table(0, 338_000_000, 1_578_000_000)));
        Assert.Equal([600, 3228], IOReportReader.ToMHz(Table(600_000_000, 3_228_000_000)));
        Assert.Empty(IOReportReader.ToMHz(null));
        Assert.Empty(IOReportReader.ToMHz(Table(0)));
    }

    [Fact]
    public void NetworkCounters_WrapAt32Bits()
    {
        // Wi-Fi at 1.536 Gbit/s for one second: at most 288 MB with headroom.
        var oneSecond = NetworkReader.MaxBytes(1_536_000_000, 1);
        Assert.Equal(288_000_000, oneSecond);
        Assert.Equal(2.0 * 1024 * 1024 * 1024, NetworkReader.MaxBytes(0, 1));

        Assert.Equal(100UL, NetworkReader.Delta(1000, 1100, oneSecond));
        // Counters macOS truncates to 32 bits wrap.
        Assert.Equal(0x2000UL, NetworkReader.Delta(0xFFFF_F000, 0x1000, oneSecond));
        // A 64-bit counter going down is a reset, not a wrap.
        Assert.Equal(0UL, NetworkReader.Delta(0x1_0000_0000, 5, oneSecond));
        // So is a 32-bit drop the link couldn't have wrapped through in the interval.
        Assert.Equal(0UL, NetworkReader.Delta(10_000_000, 0, oneSecond));
        Assert.Equal(0UL, NetworkReader.Delta(0xC000_0000, 0, oneSecond));
        Assert.Equal(0UL, NetworkReader.Delta(0xF000_0000, 0x8000_0000, oneSecond));
        // A longer interval allows a longer wrap.
        Assert.Equal(0x1000_0000UL + 0x1000, NetworkReader.Delta(0xF000_0000, 0x1000, NetworkReader.MaxBytes(1_536_000_000, 2)));
    }
}
