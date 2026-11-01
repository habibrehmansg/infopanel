using System.Reflection;
using InfoPanel.Preview.Video;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// Annex B access units to AVCC with an avcC from the in-band SPS and PPS only. The fixtures are a hardware encoder's
/// real parameter sets (a 1600×960 Baseline session) and its media type's sequence header, around made-up slices.
/// </summary>
public class AnnexBTests
{
    /// <summary>The encoder's in-band SPS (pic_order_cnt_type 0), with an emulation prevention byte.</summary>
    internal const string AmdSps = "67420428E8A03203CDFF80008000A101010140000003004000000F23C2211960";

    internal const string AmdPps = "68CE3C80";

    /// <summary>The avcC those two make, byte for byte.</summary>
    internal const string AmdAvcc = "01420428FFE1002067420428E8A03203CDFF80008000A101010140000003004000000F23C221196001000468CE3C80";

    /// <summary>The same encoder's media type sequence header: another SPS (pic_order_cnt_type 2), which slices of the in-band one can't be read with.</summary>
    internal const string AmdSequenceHeader = "0000000167420428D94064079BFF000100014202020280000003008000001E47844232C00000000168CE3C80";

    private static readonly byte[] Sps = Convert.FromHexString(AmdSps);
    private static readonly byte[] Pps = Convert.FromHexString(AmdPps);
    private static readonly byte[] Aud = [0x09, 0xF0];
    private static readonly byte[] Sei = [0x06, 0x05, 0x02, 0xAB, 0xCD, 0x80];
    private static readonly byte[] Idr = MadeUpH264.Slice(5, ppsId: 0);
    private static readonly byte[] Delta = MadeUpH264.Slice(1, ppsId: 0);

    /// <summary>NAL units after four-byte start codes.</summary>
    private static byte[] Unit(params byte[][] nals) => [.. nals.SelectMany(n => (byte[])[0, 0, 0, 1, .. n])];

    /// <summary>NAL units after their four-byte big-endian lengths.</summary>
    private static byte[] Avcc4(params byte[][] nals) =>
        [.. nals.SelectMany(n => (byte[])[(byte)(n.Length >> 24), (byte)(n.Length >> 16), (byte)(n.Length >> 8), (byte)n.Length, .. n])];

    private static AnnexB Started()
    {
        var session = new AnnexB();
        Assert.Null(session.Normalise(Unit(Aud, Sps, Pps, Idr)).Problem);
        return session;
    }

    private static void AssertProblem(AnnexBUnit unit, string words)
    {
        Assert.NotNull(unit.Problem);
        Assert.Contains(words, unit.Problem);
        Assert.Null(unit.Data);
        Assert.Null(unit.Avcc);
    }

    [Fact]
    public void AnIdrWithItsParameterSets_BecomesAudAndIdr_WithFourByteLengths_AndTheEncodersAvcc()
    {
        var unit = new AnnexB().Normalise(Unit(Aud, Sps, Pps, Idr));
        Assert.Null(unit.Problem);
        Assert.True(unit.Keyframe);
        Assert.Equal(Avcc4(Aud, Idr), unit.Data);
        Assert.Equal(Convert.FromHexString(AmdAvcc), unit.Avcc);
        Assert.Equal(Sps, unit.NewSps);

        // The stream's rewrite takes it as it is.
        Assert.Equal(unit.Avcc, H264Sps.RewriteForStream(unit.Avcc!, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void ThreeAndFourByteStartCodes_AndTrailingZeros_GiveTheSameNalUnits()
    {
        byte[] three = [0, 0, 1, .. Aud, 0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 1, .. Idr];
        byte[] mixed = [0, 0, 0, 1, .. Aud, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 1, .. Idr];
        byte[] zeros = [0, 0, 0, 0, 0, 1, .. Aud, 0, 0, 0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr, 0, 0, 0];
        foreach (var input in new[] { Unit(Aud, Sps, Pps, Idr), three, mixed, zeros })
        {
            var unit = new AnnexB().Normalise(input);
            Assert.Null(unit.Problem);
            Assert.Equal(Avcc4(Aud, Idr), unit.Data);
            Assert.Equal(Convert.FromHexString(AmdAvcc), unit.Avcc);
        }
    }

    [Fact]
    public void ADelta_GivesNoAvcc()
    {
        var unit = Started().Normalise(Unit(Aud, Delta));
        Assert.Null(unit.Problem);
        Assert.False(unit.Keyframe);
        Assert.Null(unit.Avcc);
        Assert.Null(unit.NewSps);
        Assert.Equal(Avcc4(Aud, Delta), unit.Data);
    }

    [Fact]
    public void TheSoftwareEncodersShape_KeepsEverythingButSpsAndPps()
    {
        // AUD, SPS, PPS, two SEI and ten slices.
        var slices = Enumerable.Range(0, 10).Select(i => MadeUpH264.Slice(5, ppsId: 0, firstMb: i * 600)).ToArray();
        var unit = new AnnexB().Normalise(Unit([Aud, Sps, Pps, Sei, Sei, .. slices]));
        Assert.Null(unit.Problem);
        Assert.True(unit.Keyframe);
        Assert.Equal(Avcc4([Aud, Sei, Sei, .. slices]), unit.Data);
        Assert.Equal(Convert.FromHexString(AmdAvcc), unit.Avcc);
    }

    [Fact]
    public void TheTypesSequenceHeader_IsNeverUsed()
    {
        // Nothing takes it: no member or parameter of the normaliser names a sequence header or a media type.
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] names =
        [
            .. typeof(AnnexB).GetMembers(all).Select(m => m.Name),
            .. typeof(AnnexB).GetMethods(all).Concat<MethodBase>(typeof(AnnexB).GetConstructors(all)).SelectMany(m => m.GetParameters()).Select(p => p.Name!),
        ];
        Assert.DoesNotContain(names, n => n.Contains("SequenceHeader", StringComparison.OrdinalIgnoreCase) || n.Contains("MediaType", StringComparison.OrdinalIgnoreCase));

        // The avcC holds the in-band SPS; none of the header's SPS bytes.
        var header = Convert.FromHexString(AmdSequenceHeader);
        var headerSps = header[4..36];
        Assert.Equal(0x67, headerSps[0]);
        var unit = new AnnexB().Normalise(Unit(Aud, Sps, Pps, Idr));
        Assert.Equal(Sps, unit.Avcc![8..40]);
        Assert.DoesNotContain(Convert.ToHexString(headerSps), Convert.ToHexString(unit.Avcc), StringComparison.Ordinal);

        // And with no in-band pair, a first keyframe is a problem, never an avcC from elsewhere.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Idr)), "isn't an IDR with its parameter sets");
    }

    [Fact]
    public void ASessionsFirstUnit_ThatIsADelta_OrAnIdrWithoutItsParameterSets_IsAProblem()
    {
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Delta)), "the session's first frame isn't an IDR with its parameter sets");
        AssertProblem(new AnnexB().Normalise(Unit(Sps, Pps, Delta)), "the session's first frame isn't an IDR with its parameter sets");
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Idr)), "the session's first frame isn't an IDR with its parameter sets");
        AssertProblem(new AnnexB().Normalise(Unit(Sps, Idr)), "the session's first frame isn't an IDR with its parameter sets");
        AssertProblem(new AnnexB().Normalise(Unit(Pps, Idr)), "the session's first frame isn't an IDR with its parameter sets");
    }

    [Fact]
    public void ALaterKeyframeWithoutParameterSets_TakesTheEarlierPair()
    {
        var session = Started();
        Assert.Null(session.Normalise(Unit(Aud, Delta)).Problem);
        var unit = session.Normalise(Unit(Aud, Idr));
        Assert.Null(unit.Problem);
        Assert.True(unit.Keyframe);
        Assert.Equal(Avcc4(Aud, Idr), unit.Data);
        Assert.Equal(Convert.FromHexString(AmdAvcc), unit.Avcc);
        Assert.Null(unit.NewSps);
    }

    [Fact]
    public void ParameterSetIds_AnotherPairWithItsOwnIds_IsAnAvccOfThatPair()
    {
        var sps1 = MadeUpH264.Sps(1, 100, 60);
        var pps1 = MadeUpH264.Pps(1, spsId: 1);
        var unit = new AnnexB().Normalise(Unit(Aud, sps1, pps1, MadeUpH264.Slice(5, ppsId: 1)));
        Assert.Null(unit.Problem);
        Assert.Equal(sps1, unit.NewSps);
        Assert.Equal((byte[])[1, 66, 0, 40, 0xFF, 0xE1, 0, (byte)sps1.Length, .. sps1, 1, 0, (byte)pps1.Length, .. pps1], unit.Avcc);
    }

    [Fact]
    public void ParameterSetIds_TwoSps_AreAProblem_NeverAnAvccHoldingTheFirst()
    {
        var sps1 = MadeUpH264.Sps(1, 100, 60);

        // The PPS and the IDR use SPS 1.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, sps1, MadeUpH264.Pps(1, spsId: 1), MadeUpH264.Slice(5, ppsId: 1))), "parameter sets it can't describe");

        // And when they use SPS 0, the first carried: still two.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, sps1, Pps, Idr)), "parameter sets it can't describe");
    }

    [Fact]
    public void ParameterSetIds_TwoPps_AreAProblem()
    {
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps, MadeUpH264.Pps(1, spsId: 0), Idr)), "parameter sets it can't describe");
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, MadeUpH264.Pps(1, spsId: 0), Pps, Idr)), "parameter sets it can't describe");
    }

    [Fact]
    public void ParameterSetIds_APpsNamingAnSpsNotCarried_IsAProblem()
    {
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, MadeUpH264.Pps(0, spsId: 1), Idr)), "parameter sets it can't describe");

        // A later keyframe's PPS against the session's SPS.
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Pps(0, spsId: 1), Idr)), "parameter sets it can't describe");
    }

    [Fact]
    public void ALaterKeyframeWithOnlyAnSps_OrOnlyAPps_TakesTheOtherFromTheSession()
    {
        // SPS only: the session's PPS, and the SPS's id checked against it.
        var spsOnly = Started().Normalise(Unit(Aud, Sps, Idr));
        Assert.Null(spsOnly.Problem);
        Assert.True(spsOnly.Keyframe);
        Assert.Equal(Avcc4(Aud, Idr), spsOnly.Data);
        Assert.Equal(Convert.FromHexString(AmdAvcc), spsOnly.Avcc);
        Assert.Null(spsOnly.NewSps);

        // PPS only: the session's SPS.
        var ppsOnly = Started().Normalise(Unit(Aud, Pps, Idr));
        Assert.Null(ppsOnly.Problem);
        Assert.True(ppsOnly.Keyframe);
        Assert.Equal(Avcc4(Aud, Idr), ppsOnly.Data);
        Assert.Equal(Convert.FromHexString(AmdAvcc), ppsOnly.Avcc);
        Assert.Null(ppsOnly.NewSps);

        // The id checks run against the other half: an SPS with another id leaves the session's PPS naming SPS 0.
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Sps(1, 100, 60), Idr)), "its PPS names SPS 0, not SPS 1");
    }

    [Fact]
    public void ARejectedUpdate_LeavesThePreviousPairInForce()
    {
        var session = Started();

        // A keyframe with a new SPS but a PPS naming an SPS it doesn't carry, and one with a good SPS and a bad slice.
        AssertProblem(session.Normalise(Unit(Aud, MadeUpH264.Sps(1, 100, 60), MadeUpH264.Pps(1, spsId: 0), MadeUpH264.Slice(5, ppsId: 1))), "its PPS names SPS 0, not SPS 1");
        AssertProblem(session.Normalise(Unit(Aud, MadeUpH264.Sps(0, 20, 12), Pps, MadeUpH264.Slice(5, ppsId: 1))), "parameter sets it can't describe");

        // The session still has its first pair: a keyframe without parameter sets gets that avcC, and a delta takes its bytes.
        var later = session.Normalise(Unit(Aud, Idr));
        Assert.Null(later.Problem);
        Assert.Equal(Convert.FromHexString(AmdAvcc), later.Avcc);
        Assert.Null(later.NewSps);
        Assert.Null(session.Normalise(Unit(Aud, Sps, Pps, Delta)).Problem);
        AssertProblem(session.Normalise(Unit(Aud, MadeUpH264.Sps(0, 20, 12), Delta)), "parameter sets changed on a delta");
    }

    [Fact]
    public void ParameterSetIds_ASliceNamingAnotherPps_IsAProblem()
    {
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps, MadeUpH264.Slice(5, ppsId: 1))), "parameter sets it can't describe");

        // One slice of several.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps, Idr, MadeUpH264.Slice(5, ppsId: 1, firstMb: 3000))), "parameter sets it can't describe");

        // A later keyframe without parameter sets, against the session's pair; and a delta.
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Slice(5, ppsId: 1))), "parameter sets it can't describe");
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Slice(1, ppsId: 1))), "parameter sets it can't describe");
    }

    [Fact]
    public void ADeltaWithParameterSets_TheSessionsOwnBytes_AreStripped()
    {
        var unit = Started().Normalise(Unit(Aud, Sps, Pps, Delta));
        Assert.Null(unit.Problem);
        Assert.False(unit.Keyframe);
        Assert.Null(unit.Avcc);
        Assert.Equal(Avcc4(Aud, Delta), unit.Data);
    }

    [Fact]
    public void ADeltaWithParameterSets_OtherBytes_AreAProblem()
    {
        // Same ids, another picture: a delta carries no new avcC.
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Sps(0, 20, 12), Pps, Delta)), "parameter sets changed on a delta");
        AssertProblem(Started().Normalise(Unit(Aud, MadeUpH264.Sps(0, 20, 12), Delta)), "parameter sets changed on a delta");
        AssertProblem(Started().Normalise(Unit(Aud, Sps, MadeUpH264.Pps(0, spsId: 0, deblocking: false), Delta)), "parameter sets changed on a delta");
    }

    [Fact]
    public void Bounds_EachMakeTheFrameAProblem()
    {
        AssertProblem(new AnnexB().Normalise([]), "an empty access unit");
        AssertProblem(new AnnexB().Normalise([0, 0, 0, 1, 0, 0, 1]), "an empty access unit");
        AssertProblem(new AnnexB().Normalise([0, 0, 0, 0]), "an empty access unit");

        // The forbidden bit, on any NAL unit.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps, [(byte)(Idr[0] | 0x80), .. Idr[1..]])), "forbidden_zero_bit");
        AssertProblem(new AnnexB().Normalise(Unit([0x89, 0xF0], Sps, Pps, Idr)), "forbidden_zero_bit");

        // No slice.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps)), "no slice");
        AssertProblem(Started().Normalise(Unit(Aud, Sei)), "no slice");

        // An SPS or a PPS the avcC's 16-bit length can't hold.
        byte[] longSps = [.. Sps[..^1], .. Enumerable.Repeat((byte)0x55, 65_536 - Sps.Length), Sps[^1]];
        AssertProblem(new AnnexB().Normalise(Unit(Aud, longSps, Pps, Idr)), "over 65,535 bytes");
        byte[] longPps = [.. Pps[..^1], .. Enumerable.Repeat((byte)0x55, 65_536 - Pps.Length), Pps[^1]];
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, longPps, Idr)), "over 65,535 bytes");

        // Bytes before the first start code: not Annex B.
        AssertProblem(new AnnexB().Normalise([0x12, .. Unit(Aud, Sps, Pps, Idr)]), "before the first start code");

        // Parameter sets and slices whose ids end early.
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps[..4], Pps, Idr)), "parameter sets it can't describe");
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, [0x68], Idr)), "parameter sets it can't describe");
        AssertProblem(new AnnexB().Normalise(Unit(Aud, Sps, Pps, [0x65, 0x00, 0x01])), "a slice header it can't read");
    }

    [Fact]
    public void EmulationPreventionBytes_AreLeftAsTheyAre()
    {
        // A slice whose payload needed emulation prevention, and the SPS's own 00 00 03 00.
        var idr = MadeUpH264.Slice(5, ppsId: 0, payload: [0x7F, 0x00, 0x00, 0x00, 0x00, 0x22]);
        Assert.Contains("000003", Convert.ToHexString(idr), StringComparison.Ordinal);
        var unit = new AnnexB().Normalise(Unit(Aud, Sps, Pps, idr));
        Assert.Null(unit.Problem);
        Assert.Equal(Avcc4(Aud, idr), unit.Data);
        Assert.Contains("0000030040", Convert.ToHexString(unit.Avcc!), StringComparison.Ordinal);
    }

    [Fact]
    public void AnSpsThatChangesAtALaterIdr_GivesANewAvcc()
    {
        var session = Started();
        var changed = MadeUpH264.Sps(0, 21, 13, crop: (0, 7, 0, 7));
        var unit = session.Normalise(Unit(Aud, changed, Pps, Idr));
        Assert.Null(unit.Problem);
        Assert.Equal(changed, unit.NewSps);
        byte[] expected = [1, 66, 0, 40, 0xFF, 0xE1, 0, (byte)changed.Length, .. changed, 1, 0, 4, .. Pps];
        Assert.Equal(expected, unit.Avcc);

        // Deltas and later keyframes go on with the new pair.
        Assert.Null(session.Normalise(Unit(Aud, changed, Delta)).Problem);
        AssertProblem(session.Normalise(Unit(Aud, Sps, Delta)), "parameter sets changed on a delta");
        var later = session.Normalise(Unit(Aud, Idr));
        Assert.Equal(expected, later.Avcc);
        Assert.Null(later.NewSps);
    }

    [Fact]
    public void TheMadeUpPps_IsTheEncodersWithChosenIds()
    {
        Assert.Equal(Pps, MadeUpH264.Pps(0, spsId: 0));
    }
}

/// <summary>Made-up H.264 parameter sets and slices with chosen ids and sizes (emulation prevention bytes put in).</summary>
internal static class MadeUpH264
{
    /// <summary>
    /// An SPS (<c>profile_idc</c> <paramref name="profile"/>, level 4.0, POC type 0, one reference frame, no VUI) coded at
    /// <paramref name="widthMbs"/> macroblocks by <paramref name="heightMapUnits"/> map units, with the frame cropping offsets given.
    /// </summary>
    public static byte[] Sps(int id, int widthMbs, int heightMapUnits, bool frameMbsOnly = true, (int Left, int Right, int Top, int Bottom)? crop = null, byte profile = 66)
    {
        var w = new TestBits();
        w.Bits(0x67, 8);
        w.Bits(profile, 8);
        w.Bits(0, 8); // constraint_set flags
        w.Bits(40, 8); // level_idc
        w.Ue((uint)id);
        w.Ue(0); // log2_max_frame_num_minus4
        w.Ue(0); // pic_order_cnt_type
        w.Ue(0); // log2_max_pic_order_cnt_lsb_minus4
        w.Ue(1); // max_num_ref_frames
        w.Bit(false); // gaps_in_frame_num_value_allowed_flag
        w.Ue((uint)widthMbs - 1);
        w.Ue((uint)heightMapUnits - 1);
        w.Bit(frameMbsOnly);
        if (!frameMbsOnly)
            w.Bit(false); // mb_adaptive_frame_field_flag
        w.Bit(true); // direct_8x8_inference_flag
        w.Bit(crop != null);
        if (crop is var (left, right, top, bottom))
        {
            w.Ue((uint)left);
            w.Ue((uint)right);
            w.Ue((uint)top);
            w.Ue((uint)bottom);
        }

        w.Bit(false); // vui_parameters_present_flag
        w.TrailingBits();
        return H264Sps.Escape(w.ToArray());
    }

    /// <summary>A PPS with the ids given; otherwise the encoder's (CAVLC, one slice group, deblocking control present).</summary>
    public static byte[] Pps(int id, int spsId, bool deblocking = true)
    {
        var w = new TestBits();
        w.Bits(0x68, 8);
        w.Ue((uint)id);
        w.Ue((uint)spsId);
        w.Bit(false); // entropy_coding_mode_flag
        w.Bit(false); // bottom_field_pic_order_in_frame_present_flag
        w.Ue(0); // num_slice_groups_minus1
        w.Ue(0); // num_ref_idx_l0_default_active_minus1
        w.Ue(0); // num_ref_idx_l1_default_active_minus1
        w.Bit(false); // weighted_pred_flag
        w.Bits(0, 2); // weighted_bipred_idc
        w.Ue(0); // pic_init_qp_minus26 (se 0)
        w.Ue(0); // pic_init_qs_minus26 (se 0)
        w.Ue(0); // chroma_qp_index_offset (se 0)
        w.Bit(deblocking); // deblocking_filter_control_present_flag
        w.Bit(false); // constrained_intra_pred_flag
        w.Bit(false); // redundant_pic_cnt_present_flag
        w.TrailingBits();
        return H264Sps.Escape(w.ToArray());
    }

    /// <summary>
    /// A slice NAL unit (type 5, an IDR's I slice, or type 1, a P slice) whose header names <paramref name="ppsId"/>,
    /// then <paramref name="payload"/> as made-up slice data.
    /// </summary>
    public static byte[] Slice(int nalType, int ppsId, int firstMb = 0, byte[]? payload = null)
    {
        var w = new TestBits();
        w.Bits(nalType == 5 ? 0x65u : 0x41u, 8);
        w.Ue((uint)firstMb);
        w.Ue(nalType == 5 ? 7u : 5u); // slice_type: I or P, every slice of the picture alike
        w.Ue((uint)ppsId);
        foreach (var b in payload ?? [0x9A, 0x30, 0x0C, 0x5E, 0xE1])
            w.Bits(b, 8);
        w.TrailingBits();
        return H264Sps.Escape(w.ToArray());
    }
}

/// <summary>Writes bits, most significant first.</summary>
internal sealed class TestBits
{
    private readonly List<byte> _bytes = [];
    private int _bits;

    public void Bit(bool value)
    {
        if ((_bits & 7) == 0)
            _bytes.Add(0);
        if (value)
            _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
        _bits++;
    }

    public void Bits(uint value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            Bit(((value >> i) & 1) != 0);
    }

    public void Ue(uint value)
    {
        var coded = (ulong)value + 1;
        var length = 64 - System.Numerics.BitOperations.LeadingZeroCount(coded);
        for (var i = 0; i < length - 1; i++)
            Bit(false);
        for (var i = length - 1; i >= 0; i--)
            Bit(((coded >> i) & 1) != 0);
    }

    public void TrailingBits()
    {
        Bit(true);
        while ((_bits & 7) != 0)
            Bit(false);
    }

    public byte[] ToArray() => [.. _bytes];
}
