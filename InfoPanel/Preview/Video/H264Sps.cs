namespace InfoPanel.Preview.Video;

/// <summary>
/// Makes an H.264 stream say "no frame reordering". Without a VUI <c>bitstream_restriction</c>, a decoder must
/// assume pictures may come out of decode order by up to the whole decoded picture buffer, and a hardware decoder then
/// holds every frame back that many frames (at level 4.0 and 1600×960, five frames: 165 ms at 30 fps). The rewrite sets
/// <c>bitstream_restriction_flag</c> with <c>max_num_reorder_frames</c> 0 and <c>max_dec_frame_buffering</c> =
/// <c>max_num_ref_frames</c>, and copies every other field bit for bit (H.264 7.3.2.1.1 and E.1.1). The caller guarantees
/// a stream that never reorders: its output order is its decode order (no B-frames; the native session sets
/// <c>AllowFrameReordering</c> false, and <see cref="RewriteForStream"/> refuses any profile but Baseline). <see cref="RewriteAvcc"/> doesn't check that: it writes <c>max_num_reorder_frames</c> 0 whatever the
/// SPS said, an existing restriction's included. Pure: no I/O, never throws on bad input.
/// </summary>
internal static class H264Sps
{
    /// <summary>
    /// The restriction's other fields when the SPS has none to keep, each promising nothing the encoder hasn't: motion
    /// vectors may point outside the picture, no limit on bytes per picture or bits per macroblock, and motion vector
    /// lengths up to 2^15 quarter samples, the value E.2.1 infers when the restriction is absent (no bound beyond the level's).
    /// </summary>
    internal const bool DefaultMotionVectorsOverPicBoundaries = true;
    internal const uint DefaultMaxBytesPerPicDenom = 0;
    internal const uint DefaultMaxBitsPerMbDenom = 0;
    internal const uint DefaultLog2MaxMvLength = 15;

    /// <summary>
    /// The avcC a stream sends: <see cref="RewriteAvcc"/>, refused (returned as it is, with <paramref name="problem"/>)
    /// unless the record and every SPS in it are Baseline, so "no reordering" is never declared for a stream that may
    /// reorder; unless it is a record a decoder can be configured with: four-byte NAL lengths (the stream's framing), at
    /// least one SPS and at least one PPS, each PPS holding its ids; and unless every SPS says the stream's colour, BT.709
    /// primaries, transfer and matrix in limited range, or says nothing of it (then the rewrite writes it into the VUI).
    /// </summary>
    public static byte[] RewriteForStream(byte[] avcc, out string? problem)
    {
        // The rewrite declares "no reordering" whatever the SPS said, so it runs only on what can't reorder: Baseline,
        // the profile without B-frames, in the record and in every SPS.
        if (avcc.Length < 7 || avcc[0] != 1)
        {
            problem = $"not an avcC record ({avcc.Length} bytes)";
            return avcc;
        }

        if (avcc[1] != BaselineProfile)
        {
            problem = $"the profile isn't Baseline (profile_idc {avcc[1]})";
            return avcc;
        }

        var count = avcc[5] & 0x1f;
        var at = 6;
        for (var i = 0; i < count && at + 3 < avcc.Length; i++)
        {
            var length = (avcc[at] << 8) | avcc[at + 1];
            if (length >= 2 && avcc[at + 3] != BaselineProfile)
            {
                problem = $"an SPS's profile isn't Baseline (profile_idc {avcc[at + 3]})";
                return avcc;
            }

            at += 2 + length;
        }

        if ((avcc[4] & 0x03) != 3)
        {
            problem = $"the avcC record's NAL lengths are {(avcc[4] & 0x03) + 1} bytes, not 4";
            return avcc;
        }

        if (count == 0)
        {
            problem = "the avcC record has no SPS";
            return avcc;
        }

        var output = RewriteAvcc(avcc, out problem, bt709: true);
        if (problem != null)
            return avcc;

        // The envelope is whole (RewriteAvcc checked it): at least one PPS, and every one a PPS that holds its ids.
        at = 6;
        for (var i = 0; i < (output[5] & 0x1f); i++)
            at += 2 + ((output[at] << 8) | output[at + 1]);
        var ppsCount = output[at++];
        if (ppsCount == 0)
        {
            problem = "the avcC record has no PPS";
            return avcc;
        }

        for (var i = 0; i < ppsCount; i++)
        {
            var length = (output[at] << 8) | output[at + 1];
            if (length == 0)
            {
                problem = "the avcC record has an empty PPS";
                return avcc;
            }

            if ((output[at + 2] & 0x1f) != 8)
            {
                problem = $"the avcC record's PPS list holds a NAL unit of type {output[at + 2] & 0x1f}";
                return avcc;
            }

            if (CheckPpsIds(output.AsSpan(at + 2, length)) is { } ppsProblem)
            {
                problem = ppsProblem;
                return avcc;
            }

            at += 2 + length;
        }

        return output;
    }

    /// <summary>
    /// Null when a PPS NAL unit holds its first two fields within its bytes (7.3.2.2: <c>pic_parameter_set_id</c> 0–255
    /// and <c>seq_parameter_set_id</c> 0–31, Exp-Golomb coded after the NAL header); otherwise what is wrong. A decoder
    /// can't be configured with one that ends before them.
    /// </summary>
    private static string? CheckPpsIds(ReadOnlySpan<byte> pps)
    {
        try
        {
            var r = new BitReader(Unescape(pps));
            r.Skip(8); // NAL unit header
            if (r.Ue() > 255)
                return "the avcC record has a PPS whose pic_parameter_set_id is out of range";
            if (r.Ue() > 31)
                return "the avcC record has a PPS whose seq_parameter_set_id is out of range";
            return null;
        }
        catch (InvalidOperationException)
        {
            return "the avcC record has a PPS that ends early";
        }
    }

    /// <summary>H.264's Baseline profile (<c>profile_idc</c> 66): no B-frames, so output order is decode order.</summary>
    public const byte BaselineProfile = 66;

    /// <summary>
    /// <paramref name="avcc"/> (an AVCDecoderConfigurationRecord) with every SPS rewritten: same profile, compatibility,
    /// level and length-size bytes, the PPS and any bytes after them as they were. Returns <paramref name="avcc"/> itself
    /// when every SPS already says it, or when the record or an SPS can't be parsed; then <paramref name="problem"/> says
    /// why (null when the record was rewritten or already right). The record must hold its whole PPS list and, for a
    /// profile other than Baseline, Main or Extended, either end there or hold a whole high-profile extension (ISO/IEC
    /// 14496-15: chroma format, bit depths, the SPS extension list); bytes after that are kept as they are. With
    /// <paramref name="bt709"/>, every SPS must also say BT.709 limited range or nothing of colour (<see cref="RewriteSps"/>).
    /// </summary>
    public static byte[] RewriteAvcc(byte[] avcc, out string? problem, bool bt709 = false)
    {
        problem = null;
        try
        {
            if (avcc.Length < 7 || avcc[0] != 1)
            {
                problem = $"not an avcC record ({avcc.Length} bytes)";
                return avcc;
            }

            var output = new List<byte>(avcc.Length + 16);
            output.AddRange(avcc.AsSpan(0, 5));
            var count = avcc[5] & 0x1f;
            output.Add(avcc[5]);
            var at = 6;
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                if (at + 2 > avcc.Length)
                {
                    problem = "the avcC record ends inside its SPS list";
                    return avcc;
                }

                var length = (avcc[at] << 8) | avcc[at + 1];
                if (at + 2 + length > avcc.Length)
                {
                    problem = "the avcC record ends inside an SPS";
                    return avcc;
                }

                var sps = avcc.AsSpan(at + 2, length);
                var rewritten = RewriteSps(sps, out var spsProblem, bt709);
                if (spsProblem != null)
                {
                    problem = spsProblem;
                    return avcc;
                }

                var nal = rewritten ?? sps.ToArray();
                if (nal.Length > 0xFFFF)
                {
                    problem = "the rewritten SPS is too long for the avcC record";
                    return avcc;
                }

                changed |= rewritten != null;
                output.Add((byte)(nal.Length >> 8));
                output.Add((byte)nal.Length);
                output.AddRange(nal);
                at += 2 + length;
            }

            var envelopeProblem = CheckAfterSpsList(avcc, at);
            if (envelopeProblem != null)
            {
                problem = envelopeProblem;
                return avcc;
            }

            if (!changed)
                return avcc;
            // The PPS list and whatever follows it (a high profile's extension), as they were.
            output.AddRange(avcc.AsSpan(at));
            return output.ToArray();
        }
        catch (Exception ex)
        {
            problem = $"the avcC record couldn't be rewritten ({ex.Message})";
            return avcc;
        }
    }

    /// <summary>
    /// Null when the record's bytes from <paramref name="at"/> (just after its SPS list) are a whole PPS list and, for a
    /// high profile, nothing more or a whole extension; otherwise what is wrong.
    /// </summary>
    private static string? CheckAfterSpsList(byte[] avcc, int at)
    {
        if (at >= avcc.Length)
            return "the avcC record ends before its PPS count";
        var count = avcc[at++];
        for (var i = 0; i < count; i++)
        {
            if (at + 2 > avcc.Length)
                return "the avcC record ends inside its PPS list";
            var length = (avcc[at] << 8) | avcc[at + 1];
            if (at + 2 + length > avcc.Length)
                return "the avcC record ends inside a PPS";
            at += 2 + length;
        }

        // ISO/IEC 14496-15 5.3.3.1: a profile other than 66, 77 and 88 may carry chroma_format, bit_depth_luma_minus8,
        // bit_depth_chroma_minus8 (a byte each) and numOfSequenceParameterSetExt, then that many length-prefixed NAL units.
        if (at == avcc.Length || avcc[1] is 66 or 77 or 88)
            return null;
        if (at + 4 > avcc.Length)
            return "the avcC record ends inside its high-profile extension";
        var extensions = avcc[at + 3];
        at += 4;
        for (var i = 0; i < extensions; i++)
        {
            if (at + 2 > avcc.Length)
                return "the avcC record ends inside its SPS extension list";
            var length = (avcc[at] << 8) | avcc[at + 1];
            if (at + 2 + length > avcc.Length)
                return "the avcC record ends inside an SPS extension";
            at += 2 + length;
        }

        return null;
    }

    /// <summary>
    /// One SPS NAL unit (header byte first, emulation prevention bytes in) rewritten to say "no reordering"; null when it
    /// already says it, or when it can't be parsed (then <paramref name="problem"/> says why). With
    /// <paramref name="bt709"/>, it must also say BT.709 primaries, transfer and matrix in limited range: where its VUI
    /// says nothing of colour the rewrite writes that (<c>video_signal_type</c>, its <c>video_format</c> kept, else 5,
    /// unspecified), and an SPS that says another colour or full range is a problem.
    /// </summary>
    public static byte[]? RewriteSps(ReadOnlySpan<byte> nal, out string? problem, bool bt709 = false)
    {
        problem = null;
        try
        {
            if (nal.Length < 4 || (nal[0] & 0x1f) != 7)
            {
                problem = nal.Length == 0 ? "an empty SPS" : $"not an SPS (NAL unit type {nal[0] & 0x1f}, {nal.Length} bytes)";
                return null;
            }

            var rbsp = Unescape(nal);
            var layout = Parse(rbsp);
            if (layout.Problem != null)
            {
                problem = layout.Problem;
                return null;
            }

            if (bt709 && ((layout.SignalPresent && layout.FullRange)
                          || (layout.ColourPresent && (layout.Primaries, layout.Transfer, layout.Matrix) != (Bt709, Bt709, Bt709))))
            {
                problem = $"the SPS's colour isn't BT.709 limited range (colour_primaries {layout.Primaries}, transfer_characteristics " +
                          $"{layout.Transfer}, matrix_coefficients {layout.Matrix}, video_full_range_flag {(layout.FullRange ? 1 : 0)})";
                return null;
            }

            // Colour to write: the VUI says nothing of it.
            var writeColour = bt709 && !layout.ColourPresent;
            if (!writeColour && layout.HasRestriction && layout.MaxNumReorderFrames == 0 && layout.MaxDecFrameBuffering == layout.MaxNumRefFrames)
                return null;

            var writer = new BitWriter(rbsp.Length + 8);
            if (layout.VuiPresent && writeColour)
            {
                // Everything before video_signal_type_present_flag and between its fields and bitstream_restriction_flag,
                // bit for bit; the signal type written in between.
                writer.CopyBits(rbsp, 0, layout.SignalFlagBit);
                WriteBt709(writer, layout.SignalPresent ? layout.VideoFormat : UnspecifiedVideoFormat);
                writer.CopyBits(rbsp, layout.SignalEndBit, layout.RestrictionFlagBit);
            }
            else if (layout.VuiPresent)
            {
                // Everything before bitstream_restriction_flag, bit for bit.
                writer.CopyBits(rbsp, layout.RestrictionFlagBit);
            }
            else
            {
                writer.CopyBits(rbsp, layout.VuiFlagBit);
                writer.Bit(true); // vui_parameters_present_flag
                writer.Bit(false); // aspect_ratio_info_present_flag
                writer.Bit(false); // overscan_info_present_flag
                if (writeColour)
                    WriteBt709(writer, UnspecifiedVideoFormat);
                else
                    writer.Bit(false); // video_signal_type_present_flag
                writer.Bit(false); // chroma_loc_info_present_flag
                writer.Bit(false); // timing_info_present_flag
                writer.Bit(false); // nal_hrd_parameters_present_flag
                writer.Bit(false); // vcl_hrd_parameters_present_flag
                writer.Bit(false); // pic_struct_present_flag
            }

            writer.Bit(true); // bitstream_restriction_flag
            writer.Bit(layout.HasRestriction ? layout.MotionVectorsOverPicBoundaries : DefaultMotionVectorsOverPicBoundaries);
            writer.Ue(layout.HasRestriction ? layout.MaxBytesPerPicDenom : DefaultMaxBytesPerPicDenom);
            writer.Ue(layout.HasRestriction ? layout.MaxBitsPerMbDenom : DefaultMaxBitsPerMbDenom);
            writer.Ue(layout.HasRestriction ? layout.Log2MaxMvLengthHorizontal : DefaultLog2MaxMvLength);
            writer.Ue(layout.HasRestriction ? layout.Log2MaxMvLengthVertical : DefaultLog2MaxMvLength);
            writer.Ue(0); // max_num_reorder_frames
            writer.Ue(layout.MaxNumRefFrames); // max_dec_frame_buffering
            writer.TrailingBits();
            return Escape(writer.ToArray());
        }
        catch (Exception ex)
        {
            problem = $"the SPS couldn't be rewritten ({ex.Message})";
            return null;
        }
    }

    /// <summary>The colour primaries, transfer characteristics and matrix coefficients of BT.709 (H.264 Tables E-3 to E-5).</summary>
    private const uint Bt709 = 1;

    /// <summary><c>video_format</c> 5: unspecified (Table E-2).</summary>
    private const uint UnspecifiedVideoFormat = 5;

    /// <summary>video_signal_type: present, <paramref name="videoFormat"/>, limited range, BT.709 primaries, transfer and matrix.</summary>
    private static void WriteBt709(BitWriter writer, uint videoFormat)
    {
        writer.Bit(true); // video_signal_type_present_flag
        writer.Bits(videoFormat, 3);
        writer.Bit(false); // video_full_range_flag
        writer.Bit(true); // colour_description_present_flag
        writer.Bits(Bt709, 8); // colour_primaries
        writer.Bits(Bt709, 8); // transfer_characteristics
        writer.Bits(Bt709, 8); // matrix_coefficients
    }

    /// <summary>The NAL unit's bytes without its emulation prevention bytes (the 3 of every <c>00 00 03</c>).</summary>
    internal static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var output = new List<byte>(nal.Length);
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return output.ToArray();
    }

    /// <summary>The RBSP with emulation prevention bytes put in: a 3 after any two zero bytes followed by 0–3, or ending it.</summary>
    internal static byte[] Escape(ReadOnlySpan<byte> rbsp)
    {
        var output = new List<byte>(rbsp.Length + 4);
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        if (zeros >= 2)
            output.Add(3);
        return output.ToArray();
    }

    /// <summary>Where the fields the rewrite needs are in an unescaped SPS, and their values.</summary>
    internal sealed class Layout
    {
        public string? Problem;
        public uint MaxNumRefFrames;
        public int VuiFlagBit;
        public bool VuiPresent;
        public int SignalFlagBit;
        public int SignalEndBit;
        public bool SignalPresent;
        public uint VideoFormat;
        public bool FullRange;
        public bool ColourPresent;

        // Unspecified (2) when the SPS doesn't say.
        public uint Primaries = 2;
        public uint Transfer = 2;
        public uint Matrix = 2;
        public int RestrictionFlagBit;
        public bool HasRestriction;
        public bool MotionVectorsOverPicBoundaries;
        public uint MaxBytesPerPicDenom;
        public uint MaxBitsPerMbDenom;
        public uint Log2MaxMvLengthHorizontal;
        public uint Log2MaxMvLengthVertical;
        public uint MaxNumReorderFrames;
        public uint MaxDecFrameBuffering;
    }

    /// <summary>Parses an unescaped SPS NAL unit (header byte first) through to its trailing bits.</summary>
    internal static Layout Parse(byte[] rbsp)
    {
        var layout = new Layout();
        var r = new BitReader(rbsp);
        r.Skip(8); // NAL unit header
        var profile = r.Bits(8);
        r.Skip(8); // constraint_set flags, reserved_zero_2bits
        r.Skip(8); // level_idc
        r.Ue(); // seq_parameter_set_id
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            var chromaFormat = r.Ue();
            if (chromaFormat > 3)
                return Fail(layout, $"chroma_format_idc {chromaFormat}");
            if (chromaFormat == 3)
                r.Skip(1); // separate_colour_plane_flag
            r.Ue(); // bit_depth_luma_minus8
            r.Ue(); // bit_depth_chroma_minus8
            r.Skip(1); // qpprime_y_zero_transform_bypass_flag
            if (r.Flag()) // seq_scaling_matrix_present_flag
            {
                var lists = chromaFormat != 3 ? 8 : 12;
                for (var i = 0; i < lists; i++)
                {
                    if (r.Flag())
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
                }
            }
        }
        else if (profile is not (66 or 77 or 88))
        {
            return Fail(layout, $"profile_idc {profile}, which this rewrite doesn't know");
        }

        r.Ue(); // log2_max_frame_num_minus4
        var pocType = r.Ue();
        if (pocType == 0)
        {
            r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            r.Skip(1); // delta_pic_order_always_zero_flag
            r.Se(); // offset_for_non_ref_pic
            r.Se(); // offset_for_top_to_bottom_field
            var cycle = r.Ue();
            if (cycle > 255)
                return Fail(layout, $"num_ref_frames_in_pic_order_cnt_cycle {cycle}");
            for (var i = 0u; i < cycle; i++)
                r.Se(); // offset_for_ref_frame
        }
        else if (pocType != 2)
        {
            return Fail(layout, $"pic_order_cnt_type {pocType}");
        }

        layout.MaxNumRefFrames = r.Ue();
        if (layout.MaxNumRefFrames > 16)
            return Fail(layout, $"max_num_ref_frames {layout.MaxNumRefFrames}");
        r.Skip(1); // gaps_in_frame_num_value_allowed_flag
        r.Ue(); // pic_width_in_mbs_minus1
        r.Ue(); // pic_height_in_map_units_minus1
        if (!r.Flag()) // frame_mbs_only_flag
            r.Skip(1); // mb_adaptive_frame_field_flag
        r.Skip(1); // direct_8x8_inference_flag
        if (r.Flag()) // frame_cropping_flag
        {
            for (var i = 0; i < 4; i++)
                r.Ue();
        }

        layout.VuiFlagBit = r.Position;
        layout.VuiPresent = r.Flag();
        if (layout.VuiPresent)
        {
            if (r.Flag()) // aspect_ratio_info_present_flag
            {
                if (r.Bits(8) == 255) // aspect_ratio_idc: Extended_SAR
                    r.Skip(32); // sar_width, sar_height
            }

            if (r.Flag()) // overscan_info_present_flag
                r.Skip(1); // overscan_appropriate_flag
            layout.SignalFlagBit = r.Position;
            if (r.Flag()) // video_signal_type_present_flag
            {
                layout.SignalPresent = true;
                layout.VideoFormat = r.Bits(3);
                layout.FullRange = r.Flag();
                if (r.Flag()) // colour_description_present_flag
                {
                    layout.ColourPresent = true;
                    layout.Primaries = r.Bits(8);
                    layout.Transfer = r.Bits(8);
                    layout.Matrix = r.Bits(8);
                }
            }

            layout.SignalEndBit = r.Position;

            if (r.Flag()) // chroma_loc_info_present_flag
            {
                r.Ue();
                r.Ue();
            }

            if (r.Flag()) // timing_info_present_flag
                r.Skip(65); // num_units_in_tick, time_scale, fixed_frame_rate_flag
            var nalHrd = r.Flag();
            if (nalHrd && !SkipHrd(ref r))
                return Fail(layout, "NAL HRD parameters out of range");
            var vclHrd = r.Flag();
            if (vclHrd && !SkipHrd(ref r))
                return Fail(layout, "VCL HRD parameters out of range");
            if (nalHrd || vclHrd)
                r.Skip(1); // low_delay_hrd_flag
            r.Skip(1); // pic_struct_present_flag
            layout.RestrictionFlagBit = r.Position;
            layout.HasRestriction = r.Flag();
            if (layout.HasRestriction)
            {
                layout.MotionVectorsOverPicBoundaries = r.Flag();
                layout.MaxBytesPerPicDenom = r.Ue();
                layout.MaxBitsPerMbDenom = r.Ue();
                layout.Log2MaxMvLengthHorizontal = r.Ue();
                layout.Log2MaxMvLengthVertical = r.Ue();
                layout.MaxNumReorderFrames = r.Ue();
                layout.MaxDecFrameBuffering = r.Ue();
            }
        }

        // rbsp_trailing_bits: a one, then zeros to the end. Anything else means a field this parse got wrong.
        if (!r.TrailingBits())
            return Fail(layout, "the SPS doesn't end where its fields do");
        return layout;
    }

    private static Layout Fail(Layout layout, string why)
    {
        layout.Problem = $"SPS not rewritten: {why}";
        return layout;
    }

    private static void SkipScalingList(ref BitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
                next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    /// <summary>E.1.2 hrd_parameters(); false when cpb_cnt_minus1 is out of range.</summary>
    private static bool SkipHrd(ref BitReader r)
    {
        var cpbCount = r.Ue();
        if (cpbCount > 31)
            return false;
        r.Skip(8); // bit_rate_scale, cpb_size_scale
        for (var i = 0u; i <= cpbCount; i++)
        {
            r.Ue(); // bit_rate_value_minus1
            r.Ue(); // cpb_size_value_minus1
            r.Skip(1); // cbr_flag
        }

        r.Skip(20); // the four delay and offset lengths
        return true;
    }

    /// <summary>Reads bits, most significant first; throws past the end (<see cref="RewriteSps"/> catches it).</summary>
    private ref struct BitReader(byte[] data)
    {
        private readonly byte[] _data = data;

        public int Position { get; private set; }

        public bool Flag()
        {
            if (Position >= _data.Length * 8)
                throw new InvalidOperationException("the SPS ends early");
            var bit = (_data[Position >> 3] >> (7 - (Position & 7))) & 1;
            Position++;
            return bit != 0;
        }

        public uint Bits(int count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (Flag() ? 1u : 0u);
            return value;
        }

        public void Skip(int count)
        {
            if (Position + count > _data.Length * 8)
                throw new InvalidOperationException("the SPS ends early");
            Position += count;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (!Flag())
            {
                if (++zeros > 31)
                    throw new InvalidOperationException("an Exp-Golomb code over 32 bits");
            }

            return (uint)((1UL << zeros) - 1 + Bits(zeros));
        }

        public int Se()
        {
            var k = Ue();
            return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }

        /// <summary>
        /// Whether the rest is exactly rbsp_trailing_bits (7.3.2.11): a one, then zeros up to the byte boundary, and there
        /// the end. A whole zero byte more is not trailing bits.
        /// </summary>
        public bool TrailingBits()
        {
            if (Position >= _data.Length * 8 || !Flag())
                return false;
            while ((Position & 7) != 0)
            {
                if (Flag())
                    return false;
            }

            return Position == _data.Length * 8;
        }
    }

    private sealed class BitWriter(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity);
        private int _bits;

        public void Bit(bool value)
        {
            if ((_bits & 7) == 0)
                _bytes.Add(0);
            if (value)
                _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
            _bits++;
        }

        public void Bits(ulong value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                Bit(((value >> i) & 1) != 0);
        }

        public void Ue(uint value)
        {
            var coded = (ulong)value + 1;
            var length = 64 - System.Numerics.BitOperations.LeadingZeroCount(coded);
            Bits(0, length - 1);
            Bits(coded, length);
        }

        /// <summary>The first <paramref name="count"/> bits of <paramref name="source"/>.</summary>
        public void CopyBits(byte[] source, int count) => CopyBits(source, 0, count);

        /// <summary>The bits of <paramref name="source"/> from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
        public void CopyBits(byte[] source, int from, int to)
        {
            for (var i = from; i < to; i++)
                Bit(((source[i >> 3] >> (7 - (i & 7))) & 1) != 0);
        }

        /// <summary>rbsp_trailing_bits: a one, then zeros to the byte boundary.</summary>
        public void TrailingBits()
        {
            Bit(true);
            while ((_bits & 7) != 0)
                Bit(false);
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
