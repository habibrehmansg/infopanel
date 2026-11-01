using System.Buffers.Binary;

namespace InfoPanel.Preview.Video;

/// <summary>What <see cref="AnnexB.Normalise"/> made of one access unit. Its arrays are shared with the normaliser: read only.</summary>
/// <param name="Data">The access unit in AVCC framing (each NAL unit after its four-byte big-endian length), without SPS
/// and PPS; null with a <paramref name="Problem"/>.</param>
/// <param name="Keyframe">Whether the unit holds an IDR slice (NAL unit type 5).</param>
/// <param name="Avcc">A keyframe's decoder configuration record (one SPS, one PPS, four-byte lengths); null for a delta.</param>
/// <param name="NewSps">The SPS NAL unit when this keyframe gave the session an SPS it didn't have (its first, or a
/// changed one), for the caller's size check; else null.</param>
/// <param name="Problem">Why the unit can't be sent; null when it can.</param>
internal sealed record AnnexBUnit(byte[]? Data, bool Keyframe, byte[]? Avcc, byte[]? NewSps, string? Problem);

/// <summary>
/// One session's H.264 access units from Annex B (start codes, parameter sets in band) to what the stream sends: AVCC
/// framing with no parameter sets in band, and each keyframe's avcC built from the in-band SPS and PPS only, never from a
/// media type's sequence header, whose SPS can differ from the one the slices are coded with. The session's first unit
/// must be an IDR with its SPS and PPS; a later keyframe without them takes the session's pair. A unit carries at most
/// one SPS and one PPS, its PPS names that SPS and every slice names that PPS (a unit without parameter sets is checked
/// against the session's pair); parameter sets on a delta must be the session's own bytes. AUD, SEI and any other NAL
/// unit are kept as they are, emulation prevention bytes included. Stateful (the session's pair); one thread at a time.
/// Size-blind: the caller checks each new SPS's picture against its frame (<see cref="H264Sps.PictureSize"/>).
/// </summary>
internal sealed class AnnexB
{
    /// <summary>The longest SPS or PPS an avcC's 16-bit length holds.</summary>
    public const int MaxParameterSetLength = 0xFFFF;

    private const int SliceType = 1;
    private const int IdrType = 5;
    private const int SpsType = 7;
    private const int PpsType = 8;

    // A slice's first bytes, enough for its header's first three Exp-Golomb fields (at most 97 bits) with room for
    // emulation prevention bytes.
    private const int SliceHeaderBytes = 32;

    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    private readonly List<(int Start, int End)> _nals = [];

    // The session's pair, from its last keyframe that carried them, with their ids.
    private byte[]? _sps;
    private byte[]? _pps;
    private uint _spsId;
    private uint _ppsId;
    private uint _ppsSpsId;
    private byte[]? _avcc;

    /// <summary>One access unit; a unit with a problem changes nothing in the session.</summary>
    public AnnexBUnit Normalise(ReadOnlySpan<byte> annexB)
    {
        if (Split(annexB) is { } splitProblem)
            return Fail(splitProblem);
        if (_nals.Count == 0)
            return Fail("an empty access unit");

        int sps = -1, pps = -1, spsCount = 0, ppsCount = 0, length = 0;
        bool idr = false, slice = false;
        for (var i = 0; i < _nals.Count; i++)
        {
            var (start, end) = _nals[i];
            var header = annexB[start];
            if ((header & 0x80) != 0)
                return Fail($"a NAL unit with forbidden_zero_bit set (type {header & 0x1f})");
            switch (header & 0x1f)
            {
                case SpsType:
                    spsCount++;
                    sps = i;
                    continue;
                case PpsType:
                    ppsCount++;
                    pps = i;
                    continue;
                case IdrType:
                    idr = slice = true;
                    break;
                case SliceType:
                    slice = true;
                    break;
            }

            length += 4 + end - start;
        }

        if (!slice)
            return Fail("an access unit with no slice");
        if (spsCount > 1 || ppsCount > 1)
            return Fail($"parameter sets it can't describe: {spsCount} SPS and {ppsCount} PPS in one access unit");
        var carriedSps = sps < 0 ? default : Nal(annexB, sps);
        var carriedPps = pps < 0 ? default : Nal(annexB, pps);
        if (carriedSps.Length > MaxParameterSetLength)
            return Fail("an SPS over 65,535 bytes, which an avcC can't hold");
        if (carriedPps.Length > MaxParameterSetLength)
            return Fail("a PPS over 65,535 bytes, which an avcC can't hold");
        if (_sps == null && (!idr || sps < 0 || pps < 0))
            return Fail("the session's first frame isn't an IDR with its parameter sets");
        if (!idr && ((sps >= 0 && !carriedSps.SequenceEqual(_sps)) || (pps >= 0 && !carriedPps.SequenceEqual(_pps))))
            return Fail("parameter sets changed on a delta");

        // The ids: the carried ones, else the session's.
        var spsId = _spsId;
        if (sps >= 0 && !TryReadSpsId(carriedSps, out spsId))
            return Fail("parameter sets it can't describe: an SPS whose seq_parameter_set_id can't be read");
        var (ppsId, ppsSpsId) = (_ppsId, _ppsSpsId);
        if (pps >= 0 && !TryReadPpsIds(carriedPps, out ppsId, out ppsSpsId))
            return Fail("parameter sets it can't describe: a PPS whose ids can't be read");
        if (ppsSpsId != spsId)
            return Fail($"parameter sets it can't describe: its PPS names SPS {ppsSpsId}, not SPS {spsId}");
        foreach (var (start, end) in _nals)
        {
            if ((annexB[start] & 0x1f) is not (SliceType or IdrType))
                continue;
            if (!TryReadSlicePpsId(annexB[start..end], out var slicePpsId))
                return Fail("a slice header it can't read");
            if (slicePpsId != ppsId)
                return Fail($"parameter sets it can't describe: a slice names PPS {slicePpsId}, not PPS {ppsId}");
        }

        // Every NAL unit but the parameter sets, after its length, in one pass into one array.
        var data = new byte[length];
        var at = 0;
        foreach (var (start, end) in _nals)
        {
            if ((annexB[start] & 0x1f) is SpsType or PpsType)
                continue;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), (uint)(end - start));
            annexB[start..end].CopyTo(data.AsSpan(at + 4));
            at += 4 + end - start;
        }

        if (!idr)
            return new AnnexBUnit(data, false, null, null, null);

        byte[]? newSps = null;
        var changed = false;
        if (sps >= 0 && !carriedSps.SequenceEqual(_sps))
        {
            _sps = newSps = carriedSps.ToArray();
            _spsId = spsId;
            changed = true;
        }

        if (pps >= 0 && !carriedPps.SequenceEqual(_pps))
        {
            _pps = carriedPps.ToArray();
            (_ppsId, _ppsSpsId) = (ppsId, ppsSpsId);
            changed = true;
        }

        if (changed || _avcc == null)
            _avcc = BuildAvcc(_sps!, _pps!);
        return new AnnexBUnit(data, true, _avcc, newSps, null);
    }

    private static AnnexBUnit Fail(string problem) => new(null, false, null, null, problem);

    private ReadOnlySpan<byte> Nal(ReadOnlySpan<byte> annexB, int index) => annexB[_nals[index].Start.._nals[index].End];

    /// <summary>
    /// The NAL units' bounds into <see cref="_nals"/>: split on 3-byte start codes, each unit's trailing zeros trimmed
    /// (<c>trailing_zero_8bits</c>, and a 4-byte start code's leading zero), empty ones skipped. Null, or what is wrong.
    /// </summary>
    private string? Split(ReadOnlySpan<byte> annexB)
    {
        _nals.Clear();
        var first = annexB.IndexOf(StartCode);
        if ((first < 0 ? annexB : annexB[..first]).ContainsAnyExcept((byte)0))
            return "bytes before the first start code, which isn't Annex B";
        if (first < 0)
            return null;

        var start = first + StartCode.Length;
        while (true)
        {
            var next = annexB[start..].IndexOf(StartCode);
            var end = next < 0 ? annexB.Length : start + next;
            var trimmed = end;
            while (trimmed > start && annexB[trimmed - 1] == 0)
                trimmed--;
            if (trimmed > start)
                _nals.Add((start, trimmed));
            if (next < 0)
                return null;
            start = end + StartCode.Length;
        }
    }

    /// <summary>An SPS's <c>seq_parameter_set_id</c> (0–31), after its header, profile, constraint and level bytes.</summary>
    private static bool TryReadSpsId(ReadOnlySpan<byte> sps, out uint id)
    {
        id = 0;
        try
        {
            var r = new H264Sps.BitReader(H264Sps.Unescape(sps));
            r.Skip(32);
            id = r.Ue();
            return id <= 31;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A PPS's <c>pic_parameter_set_id</c> (0–255) and the <c>seq_parameter_set_id</c> (0–31) it names.</summary>
    private static bool TryReadPpsIds(ReadOnlySpan<byte> pps, out uint id, out uint spsId)
    {
        id = spsId = 0;
        try
        {
            var r = new H264Sps.BitReader(H264Sps.Unescape(pps));
            r.Skip(8);
            id = r.Ue();
            spsId = r.Ue();
            return id <= 255 && spsId <= 31;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The <c>pic_parameter_set_id</c> a slice header names, after <c>first_mb_in_slice</c> and <c>slice_type</c>.</summary>
    private static bool TryReadSlicePpsId(ReadOnlySpan<byte> slice, out uint ppsId)
    {
        ppsId = 0;
        try
        {
            var r = new H264Sps.BitReader(H264Sps.Unescape(slice[..Math.Min(slice.Length, SliceHeaderBytes)]));
            r.Skip(8);
            r.Ue(); // first_mb_in_slice
            r.Ue(); // slice_type
            ppsId = r.Ue();
            return ppsId <= 255;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>An AVCDecoderConfigurationRecord: version 1, the SPS's profile, compatibility and level, four-byte NAL lengths, one SPS, one PPS.</summary>
    private static byte[] BuildAvcc(byte[] sps, byte[] pps)
    {
        var avcc = new byte[11 + sps.Length + pps.Length];
        avcc[0] = 1;
        avcc[1] = sps[1];
        avcc[2] = sps[2];
        avcc[3] = sps[3];
        avcc[4] = 0xFF; // reserved bits, lengthSizeMinusOne 3
        avcc[5] = 0xE1; // reserved bits, one SPS
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(6), (ushort)sps.Length);
        sps.CopyTo(avcc, 8);
        var at = 8 + sps.Length;
        avcc[at] = 1; // one PPS
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(at + 1), (ushort)pps.Length);
        pps.CopyTo(avcc, at + 3);
        return avcc;
    }
}
