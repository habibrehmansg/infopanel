using System.Diagnostics;
using System.Text.RegularExpressions;
using InfoPanel.Preview.Video;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The SPS rewrite that makes an H.264 stream say "no reordering", on three real parameter sets (1600×960,
/// level 4.0). Each output is read back by ffmpeg's own parser (<c>trace_headers</c>), not by the rewriter's; skipped
/// without ffmpeg.
/// </summary>
public partial class H264SpsTests
{
    /// <summary>(a) ffmpeg's <c>h264_videotoolbox</c>, <c>-profile:v baseline</c>: no VUI at all.</summary>
    internal const string VideoToolboxNoVuiSps = "27420028ab403203cc80";
    internal const string VideoToolboxNoVuiPps = "28ce3c80";

    /// <summary>(b) A VideoToolbox session's avcC, as the engine sets the session up: a VUI with the BT.709 colour description, no restriction.</summary>
    internal const string NativeAvcc = "AUIAKP/hAA4nQgAoq0AyA801AQEBAgEABCjOPIA=";

    /// <summary>(c) x264 baseline (ultrafast, zerolatency): already has the restriction; has an emulation prevention byte.</summary>
    internal const string X264Sps = "6742c028da01901e6c0440000003004000000f23c60ca8";
    internal const string X264Pps = "68ce0fc8";

    // Records from ffmpeg's mp4 muxer over libx264 (testsrc, 3 frames); the avcC box's payload, high-profile extension and all.

    /// <summary>
    /// (d) High profile with SPS scaling lists: x264's (<c>-pix_fmt yuv420p -profile:v high -bf 0 -x264-params cqm=jvt</c>,
    /// 640×360) with lists put into its SPS (x264 puts its matrices in the PPS only): list 0 and 6 explicit (16 and 64
    /// entries), 2 and 7 "use the default", the rest absent. ffmpeg reads 82 <c>delta_scale</c> and every later field as
    /// x264 wrote it.
    /// </summary>
    internal const string HighScalingListsAvcc = "AWQAHv/hADRnZAAerZR0dhDiMVFIREoiYpDcnivk/J/J+T5PNyZJIRsgFAX/LgIgAAADACAAAAeB4sXJAQAHaOvDyzACwP34+AA=";

    /// <summary>(e) NAL HRD: <c>-pix_fmt yuv420p -profile:v baseline -b:v 1M -maxrate 1M -bufsize 2M -x264-params nal-hrd=vbr</c>, 640×360.</summary>
    internal const string NalHrdAvcc = "AULAHv/hACRnQsAe2QCgL/lwEQAAAwABAAADADxgYAD0JAAehKbDAHixckABAARoy4yy";

    /// <summary>(f) Cropping at an odd size, 4:4:4 (profile 244, separate_colour_plane path): <c>-pix_fmt yuv444p -bf 0</c>, 321×241.</summary>
    internal const string Cropped444Avcc = "AfQADf/hABxn9AANkZZAVCHhCEMBEAAAAwAQAAADA8DxQqSAAQAGaOvDxEhE//j4AA==";

    /// <summary>(g) A restriction that reorders: <c>-pix_fmt yuv420p -profile:v main -bf 2</c>, 640×360 (max_num_reorder_frames 2).</summary>
    internal const string ReorderingAvcc = "AU1AHv/hABhnTUAe7KBQF/y4CIAAAAMAgAAAHgeLFssBAAVo6+PLIA==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutFfmpeg() => TestFfmpeg.SkipWithout();

    /// <summary>An avcC record holding one SPS and one PPS (hex NAL units).</summary>
    private static byte[] Avcc(string spsHex, string ppsHex)
    {
        var sps = Convert.FromHexString(spsHex);
        var pps = Convert.FromHexString(ppsHex);
        return [1, sps[1], sps[2], sps[3], 0xff, 0xe1, (byte)(sps.Length >> 8), (byte)sps.Length, .. sps, 1, (byte)(pps.Length >> 8), (byte)pps.Length, .. pps];
    }

    /// <summary>The record's SPS and PPS NAL units and the bytes after the PPS list.</summary>
    internal static (byte[] Sps, byte[] Pps, byte[] Tail) Split(byte[] avcc)
    {
        Assert.Equal(1, avcc[5] & 0x1f);
        var spsLength = (avcc[6] << 8) | avcc[7];
        var sps = avcc[8..(8 + spsLength)];
        var at = 8 + spsLength;
        Assert.Equal(1, avcc[at]);
        var ppsLength = (avcc[at + 1] << 8) | avcc[at + 2];
        var pps = avcc[(at + 3)..(at + 3 + ppsLength)];
        return (sps, pps, avcc[(at + 3 + ppsLength)..]);
    }

    [GeneratedRegex(@"\]\s+\d+\s+(\S+)\s+[01]+ = (-?\d+)\s*$")]
    private static partial Regex TraceField();

    /// <summary>The SPS's fields in order, (name, value), as ffmpeg's <c>trace_headers</c> parses the record's parameter sets.</summary>
    internal static async Task<List<(string Name, long Value)>> TraceSps(byte[] avcc, CancellationToken ct)
    {
        var (sps, pps, _) = Split(avcc);
        var folder = Directory.CreateTempSubdirectory("infopanel-sps-");
        var file = Path.Combine(folder.FullName, "sets.h264");
        await File.WriteAllBytesAsync(file, [0, 0, 0, 1, .. sps, 0, 0, 0, 1, .. pps], ct);
        var info = new ProcessStartInfo(TestFfmpeg.Path,
            ["-hide_banner", "-nostdin", "-loglevel", "debug", "-f", "h264", "-i", file, "-c", "copy", "-bsf:v", "trace_headers", "-f", "framecrc", "-"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ffmpeg = Process.Start(info)!;
        var stdout = ffmpeg.StandardOutput.ReadToEndAsync(ct);
        var stderr = await ffmpeg.StandardError.ReadToEndAsync(ct);
        await ffmpeg.WaitForExitAsync(ct);
        await stdout;
        File.Delete(file);
        folder.Delete();
        Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg couldn't read the parameter sets: {stderr}");

        var fields = new List<(string, long)>();
        var inSps = false;
        foreach (var line in stderr.Split('\n'))
        {
            if (!line.Contains("[trace_headers @"))
                continue;
            if (line.EndsWith("Sequence Parameter Set", StringComparison.Ordinal))
            {
                Assert.False(inSps || fields.Count > 0, "one SPS");
                inSps = true;
                continue;
            }

            if (line.EndsWith("Picture Parameter Set", StringComparison.Ordinal))
                inSps = false;
            if (inSps && TraceField().Match(line) is { Success: true } match)
                fields.Add((match.Groups[1].Value, long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.NotEmpty(fields);
        return fields;
    }

    private static long Field(List<(string Name, long Value)> fields, string name) => fields.Single(f => f.Name == name).Value;

    /// <summary>
    /// The fields up to (not including) <paramref name="name"/>; the alignment bits after the stop bit aren't fields that
    /// matter.
    /// </summary>
    private static List<(string Name, long Value)> Before(List<(string Name, long Value)> fields, string name) =>
        fields.TakeWhile(f => f.Name != name).ToList();

    /// <summary>
    /// What every rewrite must hold: same header bytes, PPS and tail; the SPS says no reordering through ffmpeg's parser;
    /// and rewriting again changes nothing.
    /// </summary>
    private static async Task<(List<(string Name, long Value)> Before, List<(string Name, long Value)> After, byte[] Output)> RewriteAndTrace(byte[] input)
    {
        var output = H264Sps.RewriteAvcc(input, out var problem);
        Assert.Null(problem);
        Assert.Equal(input[..5], output[..5]); // version, profile, compatibility, level, length size
        var (_, ppsIn, restIn) = Split(input);
        var (_, ppsOut, restOut) = Split(output);
        Assert.Equal(ppsIn, ppsOut);
        Assert.Equal(restIn, restOut);

        var before = await TraceSps(input, Ct);
        var after = await TraceSps(output, Ct);
        Assert.Equal(1, Field(after, "vui_parameters_present_flag"));
        Assert.Equal(1, Field(after, "bitstream_restriction_flag"));
        Assert.Equal(0, Field(after, "max_num_reorder_frames"));
        Assert.Equal(Field(after, "max_num_ref_frames"), Field(after, "max_dec_frame_buffering"));
        Assert.Equal(Field(before, "max_num_ref_frames"), Field(after, "max_num_ref_frames"));
        // Nothing after the restriction but the stop bit and its alignment.
        Assert.Equal(["rbsp_stop_one_bit"], after.SkipWhile(f => f.Name != "max_dec_frame_buffering").Skip(1)
            .Select(f => f.Name).Where(n => n != "rbsp_alignment_zero_bit"));
        Assert.Equal(1, Field(after, "rbsp_stop_one_bit"));

        // Idempotent: the rewritten record is returned as it is.
        var again = H264Sps.RewriteAvcc(output, out var againProblem);
        Assert.Null(againProblem);
        Assert.Same(output, again);
        return (before, after, output);
    }

    /// <summary>The restriction's other fields: x264's, when the SPS had none.</summary>
    private static void AssertX264RestrictionDefaults(List<(string Name, long Value)> after)
    {
        Assert.Equal(1, Field(after, "motion_vectors_over_pic_boundaries_flag"));
        Assert.Equal(0, Field(after, "max_bytes_per_pic_denom"));
        Assert.Equal(0, Field(after, "max_bits_per_mb_denom"));
        Assert.Equal(11, Field(after, "log2_max_mv_length_horizontal"));
        Assert.Equal(11, Field(after, "log2_max_mv_length_vertical"));
    }

    /// <summary>
    /// The restriction's other fields when the rewrite adds it: they promise nothing the encoder hasn't, so motion vector
    /// lengths are E.2.1's inferred 15, not a search range.
    /// </summary>
    private static void AssertAddedRestrictionDefaults(List<(string Name, long Value)> after)
    {
        Assert.Equal(1, Field(after, "motion_vectors_over_pic_boundaries_flag"));
        Assert.Equal(0, Field(after, "max_bytes_per_pic_denom"));
        Assert.Equal(0, Field(after, "max_bits_per_mb_denom"));
        Assert.Equal(15, Field(after, "log2_max_mv_length_horizontal"));
        Assert.Equal(15, Field(after, "log2_max_mv_length_vertical"));
    }

    [Fact]
    public async Task VideoToolboxSpsWithoutVui_GetsAVuiWithOnlyTheRestriction()
    {
        SkipWithoutFfmpeg();
        var (before, after, _) = await RewriteAndTrace(Avcc(VideoToolboxNoVuiSps, VideoToolboxNoVuiPps));
        Assert.Equal(0, Field(before, "vui_parameters_present_flag"));
        Assert.Equal(0, Field(before, "pic_order_cnt_type"));
        Assert.Equal(1, Field(before, "max_num_ref_frames"));

        // Every field before the VUI is as it was.
        Assert.Equal(Before(before, "vui_parameters_present_flag"), Before(after, "vui_parameters_present_flag"));
        // The new VUI's only set flag is bitstream_restriction_flag.
        Assert.Equal(
            [("aspect_ratio_info_present_flag", 0L), ("overscan_info_present_flag", 0L), ("video_signal_type_present_flag", 0L),
             ("chroma_loc_info_present_flag", 0L), ("timing_info_present_flag", 0L), ("nal_hrd_parameters_present_flag", 0L),
             ("vcl_hrd_parameters_present_flag", 0L), ("pic_struct_present_flag", 0L)],
            after.SkipWhile(f => f.Name != "vui_parameters_present_flag").Skip(1).TakeWhile(f => f.Name != "bitstream_restriction_flag"));
        AssertAddedRestrictionDefaults(after);
        Assert.Equal(1, Field(after, "max_dec_frame_buffering"));
    }

    [Fact]
    public async Task NativeSessionSps_KeepsItsVuiAndColourDescription_AndGainsTheRestriction()
    {
        SkipWithoutFfmpeg();
        var input = Convert.FromBase64String(NativeAvcc);
        var (before, after, output) = await RewriteAndTrace(input);
        Assert.Equal(1, Field(before, "vui_parameters_present_flag"));
        Assert.Equal(0, Field(before, "bitstream_restriction_flag"));
        Assert.Equal(0, Field(before, "pic_order_cnt_type"));

        // Every SPS and VUI field before the restriction is as it was, the colour description among them.
        Assert.Equal(Before(before, "bitstream_restriction_flag"), Before(after, "bitstream_restriction_flag"));
        Assert.Equal(1, Field(after, "colour_description_present_flag"));
        Assert.Equal(1, Field(after, "colour_primaries"));
        Assert.Equal(1, Field(after, "transfer_characteristics"));
        Assert.Equal(1, Field(after, "matrix_coefficients"));
        Assert.Equal(5, Field(after, "video_format"));
        Assert.Equal(0, Field(after, "video_full_range_flag"));
        AssertAddedRestrictionDefaults(after);
        Assert.Equal(1, Field(after, "max_dec_frame_buffering"));

        // The codec string (avcC bytes 1-3) is the same.
        Assert.Equal(input[1..4], output[1..4]);
    }

    [Fact]
    public async Task X264Sps_AlreadySaysIt_IsReturnedAsItIs()
    {
        SkipWithoutFfmpeg();
        var input = Avcc(X264Sps, X264Pps);
        var output = H264Sps.RewriteAvcc(input, out var problem);
        Assert.Null(problem);
        Assert.Same(input, output);

        // And it does say it, through ffmpeg's parser.
        var fields = await TraceSps(output, Ct);
        Assert.Equal(1, Field(fields, "bitstream_restriction_flag"));
        Assert.Equal(0, Field(fields, "max_num_reorder_frames"));
        Assert.Equal(Field(fields, "max_num_ref_frames"), Field(fields, "max_dec_frame_buffering"));
        AssertX264RestrictionDefaults(fields);
    }

    [Fact]
    public void Escape_PutsBackWhatUnescapeTakesOut()
    {
        var x264 = Convert.FromHexString(X264Sps);
        var rbsp = H264Sps.Unescape(x264);
        Assert.Equal(x264.Length - 1, rbsp.Length); // its emulation prevention byte (00 00 03 00)
        Assert.Equal(x264, H264Sps.Escape(rbsp));

        byte[] zeros = [0x67, 0, 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0];
        var escaped = H264Sps.Escape(zeros);
        Assert.Equal(Convert.FromHexString("67000003000003010000030200000303000004000003"), escaped);
        Assert.Equal(zeros, H264Sps.Unescape(escaped));
    }

    [Fact]
    public void UnparseableInput_IsReturnedAsItIs_WithAReason()
    {
        // A cut SPS: the native one without its last bytes.
        var native = Convert.FromBase64String(NativeAvcc);
        var (sps, pps, _) = Split(native);
        var cut = Avcc(Convert.ToHexString(sps[..9]), Convert.ToHexString(pps));
        Assert.Same(cut, H264Sps.RewriteAvcc(cut, out var cutProblem));
        Assert.NotNull(cutProblem);

        // A profile the parser doesn't know.
        var unknownProfile = sps.ToArray();
        unknownProfile[1] = 200;
        var unknown = Avcc(Convert.ToHexString(unknownProfile), Convert.ToHexString(pps));
        Assert.Same(unknown, H264Sps.RewriteAvcc(unknown, out var unknownProblem));
        Assert.Contains("profile_idc 200", unknownProblem);

        // Not an avcC record, and one that ends inside its SPS.
        byte[] notAvcc = [0, 0, 0, 1, 0x67];
        Assert.Same(notAvcc, H264Sps.RewriteAvcc(notAvcc, out var notProblem));
        Assert.NotNull(notProblem);
        var shortRecord = native[..12];
        Assert.Same(shortRecord, H264Sps.RewriteAvcc(shortRecord, out var shortProblem));
        Assert.NotNull(shortProblem);

        // Never throws, whatever the bytes.
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = native.ToArray();
            bytes[8 + random.Next(sps.Length - 1) + 1] = (byte)random.Next(256);
            var result = H264Sps.RewriteAvcc(bytes, out var p);
            Assert.True(ReferenceEquals(result, bytes) || p == null);
        }
    }

    /// <summary>
    /// A record whose SPS already says it comes back as it is, without a problem; and the rewriter's parse agrees with
    /// ffmpeg's on every value it reads (a field it skipped wrongly would leave it reading the wrong bits).
    /// </summary>
    private static async Task<List<(string Name, long Value)>> AssertReturnedAsItIs_AndParsedAsFfmpegDoes(byte[] input)
    {
        var output = H264Sps.RewriteAvcc(input, out var problem);
        Assert.Null(problem);
        Assert.Same(input, output);

        var fields = await TraceSps(input, Ct);
        Assert.Equal(1, Field(fields, "bitstream_restriction_flag"));
        Assert.Equal(0, Field(fields, "max_num_reorder_frames"));
        Assert.Equal(Field(fields, "max_num_ref_frames"), Field(fields, "max_dec_frame_buffering"));

        var layout = H264Sps.Parse(H264Sps.Unescape(Split(input).Sps));
        Assert.Null(layout.Problem);
        Assert.True(layout.VuiPresent);
        Assert.True(layout.HasRestriction);
        Assert.Equal(Field(fields, "max_num_ref_frames"), layout.MaxNumRefFrames);
        Assert.Equal(Field(fields, "motion_vectors_over_pic_boundaries_flag") == 1, layout.MotionVectorsOverPicBoundaries);
        Assert.Equal(Field(fields, "max_bytes_per_pic_denom"), layout.MaxBytesPerPicDenom);
        Assert.Equal(Field(fields, "max_bits_per_mb_denom"), layout.MaxBitsPerMbDenom);
        Assert.Equal(Field(fields, "log2_max_mv_length_horizontal"), layout.Log2MaxMvLengthHorizontal);
        Assert.Equal(Field(fields, "log2_max_mv_length_vertical"), layout.Log2MaxMvLengthVertical);
        Assert.Equal(Field(fields, "max_num_reorder_frames"), layout.MaxNumReorderFrames);
        Assert.Equal(Field(fields, "max_dec_frame_buffering"), layout.MaxDecFrameBuffering);
        return fields;
    }

    [Fact]
    public async Task HighProfileSpsWithScalingLists_IsParsedThroughThem()
    {
        SkipWithoutFfmpeg();
        var input = Convert.FromBase64String(HighScalingListsAvcc);
        var fields = await AssertReturnedAsItIs_AndParsedAsFfmpegDoes(input);
        Assert.Equal(100, Field(fields, "profile_idc"));
        Assert.Equal(1, Field(fields, "seq_scaling_matrix_present_flag"));
        Assert.Equal([1L, 0, 1, 0, 0, 0, 1, 1], Enumerable.Range(0, 8).Select(i => Field(fields, $"seq_scaling_list_present_flag[{i}]")));
        Assert.Equal(16 + 1 + 64 + 1, fields.Count(f => f.Name.StartsWith("delta_scale", StringComparison.Ordinal)));
        // The record's high-profile extension: 4:2:0, 8 bits, no SPS extensions.
        Assert.Equal([0xfd, 0xf8, 0xf8, 0x00], Split(input).Tail);
    }

    [Fact]
    public async Task SpsWithNalHrd_IsParsedThroughIt()
    {
        SkipWithoutFfmpeg();
        var fields = await AssertReturnedAsItIs_AndParsedAsFfmpegDoes(Convert.FromBase64String(NalHrdAvcc));
        Assert.Equal(1, Field(fields, "nal_hrd_parameters_present_flag"));
        Assert.Equal(0, Field(fields, "vcl_hrd_parameters_present_flag"));
        Assert.Equal(0, Field(fields, "cpb_cnt_minus1"));
        Assert.Equal(1, Field(fields, "timing_info_present_flag"));
        Assert.Contains(fields, f => f.Name == "low_delay_hrd_flag");
    }

    [Fact]
    public async Task CroppedHigh444Sps_IsParsedThroughIt()
    {
        SkipWithoutFfmpeg();
        var input = Convert.FromBase64String(Cropped444Avcc);
        var fields = await AssertReturnedAsItIs_AndParsedAsFfmpegDoes(input);
        Assert.Equal(244, Field(fields, "profile_idc"));
        Assert.Equal(3, Field(fields, "chroma_format_idc"));
        Assert.Equal(1, Field(fields, "frame_cropping_flag"));
        Assert.Equal(15, Field(fields, "frame_crop_right_offset"));
        Assert.Equal(15, Field(fields, "frame_crop_bottom_offset"));
        Assert.Equal([0xff, 0xf8, 0xf8, 0x00], Split(input).Tail);
    }

    [Fact]
    public async Task ExistingRestrictionThatReorders_IsRewrittenToZero_KeepingItsOtherFields()
    {
        SkipWithoutFfmpeg();
        var (before, after, _) = await RewriteAndTrace(Convert.FromBase64String(ReorderingAvcc));
        Assert.Equal(1, Field(before, "bitstream_restriction_flag"));
        Assert.Equal(2, Field(before, "max_num_reorder_frames"));
        Assert.Equal(4, Field(before, "max_num_ref_frames"));

        // Every field before max_num_reorder_frames is as it was: x264's motion vector lengths, not the added defaults.
        Assert.Equal(Before(before, "max_num_reorder_frames"), Before(after, "max_num_reorder_frames"));
        Assert.Equal(10, Field(after, "log2_max_mv_length_horizontal"));
        Assert.Equal(10, Field(after, "log2_max_mv_length_vertical"));
        Assert.Equal(4, Field(after, "max_dec_frame_buffering"));
    }

    [Fact]
    public void RecordWithoutAWholePpsListOrExtension_IsReturnedAsItIs_WithAReason()
    {
        var native = Convert.FromBase64String(NativeAvcc);
        var high = Convert.FromBase64String(HighScalingListsAvcc);
        var highEnd = high.Length - 4; // where its extension starts
        byte[] twoPpsCounted = [.. native];
        twoPpsCounted[22] = 2;
        byte[] oneExtensionCounted = [.. high];
        oneExtensionCounted[^1] = 1;
        byte[] extensionLengthTooLong = [.. high[..^1], 1, 0, 9, 0x67];

        foreach (var (name, bytes, why) in new (string, byte[], string)[]
                 {
                     ("no PPS count (the SPS needs rewriting)", native[..22], "PPS count"),
                     ("the last PPS byte missing", native[..^1], "inside a PPS"),
                     ("two PPS counted, one there", twoPpsCounted, "inside its PPS list"),
                     ("no PPS count (the SPS is already right)", high[..(highEnd - 10)], "PPS count"),
                     ("an extension cut short", high[..^1], "inside its high-profile extension"),
                     ("one SPS extension counted, none there", oneExtensionCounted, "inside its SPS extension list"),
                     ("an SPS extension longer than the record", extensionLengthTooLong, "inside an SPS extension"),
                 })
        {
            var output = H264Sps.RewriteAvcc(bytes, out var problem);
            Assert.True(ReferenceEquals(bytes, output), name);
            Assert.True(problem?.Contains(why, StringComparison.Ordinal) == true, $"{name}: {problem}");
        }

        // A whole extension with an SPS extension in it is kept, and so are bytes after a Baseline record's PPS list.
        byte[] withExtension = [.. high[..^1], 1, 0, 2, 0x6d, 0x00];
        Assert.Same(withExtension, H264Sps.RewriteAvcc(withExtension, out var withProblem));
        Assert.Null(withProblem);
        byte[] baselineTail = [.. native, 0xaa];
        var rewritten = H264Sps.RewriteAvcc(baselineTail, out var tailProblem);
        Assert.Null(tailProblem);
        Assert.Equal(0xaa, rewritten[^1]);
    }

    [Fact]
    public void SpsWithAZeroByteAfterItsTrailingBits_IsReturnedAsItIs_WithAReason()
    {
        // The native SPS (its stop bit mid-byte) and the VideoToolbox one (its stop bit a byte's first bit), each with a
        // zero byte more and its length to match.
        var native = Split(Convert.FromBase64String(NativeAvcc));
        Assert.Equal("27420028AB403203CD3501010102", Convert.ToHexString(native.Sps));
        foreach (var sps in new[] { Convert.ToHexString(native.Sps) + "00", VideoToolboxNoVuiSps + "00" })
        {
            var input = Avcc(sps, Convert.ToHexString(native.Pps));
            Assert.Same(input, H264Sps.RewriteAvcc(input, out var problem));
            Assert.Contains("doesn't end where its fields do", problem);
        }

        // Without the extra byte both are rewritten.
        foreach (var sps in new[] { Convert.ToHexString(native.Sps), VideoToolboxNoVuiSps })
        {
            var input = Avcc(sps, Convert.ToHexString(native.Pps));
            Assert.NotSame(input, H264Sps.RewriteAvcc(input, out var problem));
            Assert.Null(problem);
        }
    }

    /// <summary>The stream's colour: BT.709 primaries, transfer and matrix, limited range, written in the VUI.</summary>
    private static void AssertBt709Limited(List<(string Name, long Value)> fields)
    {
        Assert.Equal(1, Field(fields, "vui_parameters_present_flag"));
        Assert.Equal(1, Field(fields, "video_signal_type_present_flag"));
        Assert.Equal(0, Field(fields, "video_full_range_flag"));
        Assert.Equal(1, Field(fields, "colour_description_present_flag"));
        Assert.Equal(1, Field(fields, "colour_primaries"));
        Assert.Equal(1, Field(fields, "transfer_characteristics"));
        Assert.Equal(1, Field(fields, "matrix_coefficients"));
    }

    [Fact]
    public async Task AnX264Avcc_ThatAlreadySaysNoReordering_GainsOnlyBt709LimitedRange_ForAStream()
    {
        // x264 writes a VUI with its restriction but nothing of colour: the stream's colour is written into it, the
        // rest of its VUI kept field for field.
        var input = Avcc(X264Sps, X264Pps);
        var output = H264Sps.RewriteForStream(input, out var problem);
        Assert.Null(problem);
        Assert.Same(output, H264Sps.RewriteForStream(output, out var again));
        Assert.Null(again);
        SkipWithoutFfmpeg();
        var before = await TraceSps(input, Ct);
        var after = await TraceSps(output, Ct);
        Assert.Equal(0, Field(before, "video_signal_type_present_flag"));
        AssertBt709Limited(after);
        Assert.Equal(5, Field(after, "video_format"));
        Assert.Equal(Before(before, "video_signal_type_present_flag"), Before(after, "video_signal_type_present_flag"));
        Assert.Equal(Before(before.SkipWhile(f => f.Name != "chroma_loc_info_present_flag").ToList(), "rbsp_stop_one_bit"),
            Before(after.SkipWhile(f => f.Name != "chroma_loc_info_present_flag").ToList(), "rbsp_stop_one_bit"));
        Assert.Equal(0, Field(after, "max_num_reorder_frames"));
        AssertX264RestrictionDefaults(after);
    }

    [Fact]
    public async Task ARecordWithoutAVui_GetsBt709LimitedRange_AndTheRestriction_ForAStream()
    {
        var input = Avcc(VideoToolboxNoVuiSps, VideoToolboxNoVuiPps);
        var output = H264Sps.RewriteForStream(input, out var problem);
        Assert.Null(problem);
        Assert.Same(output, H264Sps.RewriteForStream(output, out var again));
        Assert.Null(again);
        SkipWithoutFfmpeg();
        var before = await TraceSps(input, Ct);
        var after = await TraceSps(output, Ct);
        Assert.Equal(Before(before, "vui_parameters_present_flag"), Before(after, "vui_parameters_present_flag"));
        AssertBt709Limited(after);
        Assert.Equal(5, Field(after, "video_format"));
        Assert.Equal(1, Field(after, "bitstream_restriction_flag"));
        Assert.Equal(0, Field(after, "max_num_reorder_frames"));
        Assert.Equal(Field(after, "max_num_ref_frames"), Field(after, "max_dec_frame_buffering"));
    }

    /// <summary>
    /// The native record's SPS (bytes 8–21) with one colour field changed: its primaries, transfer and matrix are bytes
    /// 18, 19 and 20, and its full-range flag is bit 1 of byte 17.
    /// </summary>
    public static TheoryData<int, byte, string> OtherColours => new()
    {
        { 18, 5, "colour_primaries 5" },
        { 19, 6, "transfer_characteristics 6" },
        { 20, 6, "matrix_coefficients 6" },
        { 17, 0x37, "video_full_range_flag 1" },
    };

    [Theory]
    [MemberData(nameof(OtherColours))]
    public void ARecordThatSaysAnotherColour_OrFullRange_IsRefused_ForAStream(int at, byte value, string said)
    {
        var input = NativeWith(a =>
        {
            a[at] = value;
            return a;
        });
        Assert.Same(input, H264Sps.RewriteForStream(input, out var problem));
        Assert.NotNull(problem);
        Assert.Contains("isn't BT.709 limited range", problem);
        Assert.Contains(said, problem);
    }

    [Theory]
    [InlineData(ReorderingAvcc, 77)]
    [InlineData(HighScalingListsAvcc, 100)]
    [InlineData(Cropped444Avcc, 244)]
    public void ARecordThatIsntBaseline_IsRefused_ForAStream(string base64, int profile)
    {
        // Rewriting it would declare "no reordering" for a stream that may reorder (Main has B-frames).
        var input = Convert.FromBase64String(base64);
        var output = H264Sps.RewriteForStream(input, out var problem);
        Assert.Same(input, output);
        Assert.Equal($"the profile isn't Baseline (profile_idc {profile})", problem);
    }

    [Fact]
    public void AnSpsThatIsntBaseline_InABaselineRecord_IsRefused_ForAStream()
    {
        var input = Convert.FromBase64String(ReorderingAvcc);
        input[1] = H264Sps.BaselineProfile;
        Assert.Same(input, H264Sps.RewriteForStream(input, out var problem));
        Assert.Equal("an SPS's profile isn't Baseline (profile_idc 77)", problem);
    }

    /// <summary>The native record's parts: header (6 bytes), its one SPS (14 bytes, with its 2-byte length), then its PPS list.</summary>
    private static byte[] NativeWith(Func<byte[], byte[]> change) => change(Convert.FromBase64String(NativeAvcc));

    public static TheoryData<string, string> UnusableRecords => new()
    {
        // Both parameter-set lists empty.
        { "01420028FFE000", "the avcC record has no SPS" },
        { Convert.ToHexString(NativeWith(a => [.. a[..22], 0])), "the avcC record has no PPS" },
        { Convert.ToHexString(NativeWith(a => [.. a[..22], 1, 0, 0])), "the avcC record has an empty PPS" },
        { Convert.ToHexString(NativeWith(a => { a[25] = 0x21; return a; })), "the avcC record's PPS list holds a NAL unit of type 1" },
        { Convert.ToHexString(NativeWith(a => { a[4] = 0xFD; return a; })), "the avcC record's NAL lengths are 2 bytes, not 4" },
        // A PPS that is only its NAL header: it ends before its pic_parameter_set_id.
        { "01420028FFE1000E27420028AB403203CD350101010201000168", "the avcC record has a PPS that ends early" },
        // A PPS naming seq_parameter_set_id 32 (ids go to 31).
        { Convert.ToHexString(NativeWith(a => [.. a[..22], 1, 0, 3, 0x28, 0x82, 0x18])), "the avcC record has a PPS whose seq_parameter_set_id is out of range" },
    };

    [Theory]
    [MemberData(nameof(UnusableRecords))]
    public void ARecordADecoderCantBeConfiguredWith_IsRefused_ForAStream(string hex, string why)
    {
        var input = Convert.FromHexString(hex);
        Assert.Same(input, H264Sps.RewriteForStream(input, out var problem));
        Assert.Equal(why, problem);
    }

    [Fact]
    public void ABaselineRecord_IsRewritten_ForAStream()
    {
        var input = Convert.FromBase64String(NativeAvcc);
        var output = H264Sps.RewriteForStream(input, out var problem);
        Assert.Null(problem);
        Assert.Equal(H264Sps.RewriteAvcc(input, out _), output);
        Assert.NotEqual(input, output);
    }
}
