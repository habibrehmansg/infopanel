using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// <see cref="Workspace.Serialize"/>, which every checkpoint, save, create and export runs on the whole document: what it
/// allocates for a large document, and its output, byte for byte, for a set of documents.
/// </summary>
public sealed class SerializeTests
{
    /// <summary>
    /// Serialising a large document (about 5 MiB) allocates at most three times its size on the calling thread: the
    /// output itself and what writing it needs, not several copies of the document.
    /// </summary>
    [Fact]
    public void SerializingALargeDocumentAllocatesAtMostThreeTimesItsSize()
    {
        var generation = Large();
        var size = Workspace.Serialize(generation).Length;
        Assert.True(size > 5_000_000, $"{size} bytes");

        var total = AllocatedBy(() => Workspace.Serialize(generation));
        // For the record, the stages serialising has (or had) on their own: the model written as a JSON value
        // (SpecJson.Write), and the canonical form's walk over such a value without its output (CanonicalJson.Size).
        var element = SpecJson.Write(generation.Document);
        var model = AllocatedBy(() => SpecJson.Write(generation.Document));
        var walk = AllocatedBy(() => CanonicalJson.Size(element));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"document {Mib(size)} MiB; Serialize allocates {Mib(total)} MiB ({(double)total / size:F1}x); " +
            $"on their own, SpecJson.Write {Mib(model)} MiB, the canonical walk {Mib(walk)} MiB");
        Assert.True(total <= 3L * size,
            $"serialising a {size}-byte document allocated {total} bytes ({(double)total / size:F1}x its size; " +
            $"on their own, SpecJson.Write {model}, the canonical walk {walk})");
    }

    /// <summary>
    /// The output is unchanged, byte for byte: the SHA-256 of each document's serialised form as the engine wrote it
    /// before serialising was made cheaper. The documents: the large one above; one whose opaque member names need
    /// escapes, sort differently by UTF-16 code unit than by UTF-8 byte, or are not ASCII, with numbers and strings that
    /// are not yet canonical; every valid example document in <c>spec/examples</c>; and every reference document (the 1.x
    /// all-kinds profile and the widgets among them).
    /// </summary>
    [Fact]
    public void TheSerialisedFormIsUnchanged()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["large"] = Hash(Workspace.Serialize(Large())),
            ["names"] = Hash(Workspace.Serialize(Load(Names()))),
        };
        foreach (var (name, path) in Documents())
            actual[name] = Hash(Workspace.Serialize(Load(JsonDocument.Parse(File.ReadAllBytes(path), SpecJson.DocumentOptions).RootElement.Clone())));

        var lines = string.Join("\n", actual.Select(p => $"[\"{p.Key}\"] = \"{p.Value}\","));
        TestContext.Current.TestOutputHelper?.WriteLine(lines);
        Assert.Equal(Expected, actual);
    }

    private static readonly SortedDictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["examples/desk.json"] = "5e5fd8f723b0985b7e48f04bb55b6ebda239473d74533516bb97d60f5a02a92a",
        ["examples/empty-group.json"] = "2b7ce6b75afa487865e01645010d1f0a141866657b07dd704b490fed54a9bd09",
        ["examples/escaped-origin.json"] = "67841a3184bf31f0cc9fc647d931f64a03931a110d0879cd575af771b4a33d3b",
        ["examples/json-depth.json"] = "c64eca4ace4e4849ecaea9f91c26203c7bd6532284f63c98551fc9359d286abc",
        ["examples/locale-unknown.json"] = "38ea6d0195bd1f4a73ec74e29212a39677b06435b44968249c90aeb585fb307a",
        ["examples/locale.json"] = "c415b41d7a6e57f4d19e88dcf8cf7001c1d80ae8d98e0b680971f0d2512dea24",
        ["examples/minimal.json"] = "7e3cc447365e03b8b39269a7557c997db9ba003fe0a20362bccf05c2d92daad9",
        ["examples/window-click-through.json"] = "6678edb0bfca63314a9fa4451955ccacf71d1d2c748389f8a08f2b4e580aeee5",
        ["examples/window-desktop.json"] = "52ffe920b90f91939203019b876886258d3f9832c45233410fda4b4f7f8c8b95",
        ["examples/window-normal.json"] = "009b61ae2d2c4350c324f42d4498e0e3da51e54508d46e5f9be9ddff5ce5a467",
        ["examples/window-spots.json"] = "ef4373934afa05a6f0e6261a50c92479f4bad0e022890db5abbd609f44aa2a10",
        ["large"] = "877faa9cabb1b74f370d86e4426af4ed2014463c4399c254eff579f507b6711d",
        ["names"] = "16145218bc5f4c9cd447950fae65d51be3f7d02216357a4a4cdf3e1920c115b5",
        ["reference/v1/all-kinds.document.json"] = "1323a41a9b921cb22daedcced011e2eb7cac5ac8209b1ce62b38f26834fc77c6",
        ["reference/v1/bar-horizontal-gradient.document.json"] = "034dbc4459537ddcdd936684d869756adc482ac87517eba91ec33437e964292c",
        ["reference/v1/bar-rotated-gradient.document.json"] = "c8bb54bdb4a1829e15248d5df0707287c64024c1c91d7409b13e7ed2b81851e1",
        ["reference/v1/bar-vertical-rounded.document.json"] = "ba739ede1dce33d0e66265e2ca417ff3fe6e77c4b9ed49b7e0c52a6a7cd4e898",
        ["reference/v1/calendar.document.json"] = "7634c3811e14072edeb406a2bab10f67f34cef9fcefa0a64f845c49fac3286bf",
        ["reference/v1/clock.document.json"] = "dfa10f31a856c53531bdb0e7e0c7e82fd7899ed910e9b265f5b8a1b1c4899eb3",
        ["reference/v1/composite-scale1.document.json"] = "f22555120685bcbd66d372ad942bbfde2db05eee10c511abada0ff4ff911a2f2",
        ["reference/v1/composite-scale2.document.json"] = "8e7c451095b2d2901ec96cf66dbf39810bd6a40f53565b1f4d9883e4c15368e2",
        ["reference/v1/donut-span270-rotation90.document.json"] = "8759dd90114148db1a8f0bc7bb6478e029bb4d67a043782693056550c8e42c8e",
        ["reference/v1/gauge-middle-frame.document.json"] = "a33aaba9d7c23a46c04b6e6b6a3764f4b876f54ab2b633f0d7eded6dd5e4922b",
        ["reference/v1/gradients-all.document.json"] = "daccbd7256b5a04842795618c4727494b63a9b303873f3440e6af85b522c6e55",
        ["reference/v1/graph-histogram.document.json"] = "e047dab345a0c68da0338339be3f0486e7dadae43be58ed563c4358f1b6bff71",
        ["reference/v1/graph-line-fill.document.json"] = "852f25d563b7f17e8a92cccadd8c62e2853ac89a7d220cbc4ecf4638ebbde469",
        ["reference/v1/group-nested-hidden-child.document.json"] = "28ddb8079f4375cb6ea558d4965ae9cb04b66c0aef5b7a88e3d5850daff69e7d",
        ["reference/v1/http-image-placeholder.document.json"] = "b3e8d72061d89b5bbbf23ad0afdc994c9922fc5c64e558f372db91516d060e72",
        ["reference/v1/image-gif-frame0.document.json"] = "76db8cab39136e1a85549fa535806697fadca8537261afb9e8ac73b32c17f88c",
        ["reference/v1/image-missing.document.json"] = "2b4879ead32095fe53121462520d39a1d455a5ec2b71743bcba63efdafb8af19",
        ["reference/v1/image-png.document.json"] = "4c6b859357b4bee25a32e51a4ec65c5bf6c5847b112fbe7444d3b7e4621c665c",
        ["reference/v1/image-svg.document.json"] = "c3eee48ce97c90e9bbb244f0011538d5d87792c9d5c5f79cbba10dede6fdf70a",
        ["reference/v1/sensor-image-hidden.document.json"] = "db13d6e6d9d8eefd114bb095f267b1f6e3db340ad63165d2c4c3dabf036a7803",
        ["reference/v1/sensor-image-shown.document.json"] = "7c911812eab4a20e5a8f11e9b62d6a45d166864a989767d9d1ed02f0b95fbc56",
        ["reference/v1/sensor-thresholds-precision-unit.document.json"] = "80856a249db1b3bfbbaa052e58d111cbdfb2a762dbb8d9b46704e836b6207669",
        ["reference/v1/shapes-all.document.json"] = "2b4858741d2f272eedcd4c4967f2942530b70c55f1cdbc000920cc984e838a2f",
        ["reference/v1/shapes-rotated-gradients.document.json"] = "10473ab4a708104f9c55296894bb06653890b7e8869a8f86329c80d71c2b2cf7",
        ["reference/v1/table-glow.document.json"] = "a559c036a7236eff4e6a91fca8efb5b41c52f932a7cc688bfe963407e6936aff",
        ["reference/v1/table-missing-column.document.json"] = "c3ef07c83f594e4545bb2d3629d0fbf355e671a96a418f2cbc45e072700cb430",
        ["reference/v1/table-rows-0.document.json"] = "87d9d11426482bb86621cf8a36b7b19c7abe4439f9940dde4fca985b63ac4c69",
        ["reference/v1/table-single-line-no-ellipsis.document.json"] = "e7248e145e43b67a9fdcdb52691c4758eea52e6e8c7e34d21bc40c22a32adaf4",
        ["reference/v1/table-underline-zero-width.document.json"] = "7b3bed182e5335cb80d636d45dd8c0d4b6084f21dd74eb82bf9b30c2d69ac0f9",
        ["reference/v1/table-wrap-off.document.json"] = "47250bd1baa34dba3e4cd6f8373fe23227fb3f324aa3ba9ad6a5215d5f08b215",
        ["reference/v1/table.document.json"] = "0ea917518e3b0a16f4eb172a8d7de3e2457ae9e07ef7793aafbf78843361b3af",
        ["reference/v1/text-center-box.document.json"] = "b3982f1d4e4c37550f6116d6844cc3bc3db9d6a47b6ab661c0e068f2d1fb6f71",
        ["reference/v1/text-glow-hard-light.document.json"] = "ba71b7ad7f86f62aa5a7f28f9ef0b627d77bf544dbed6ca9a3e29ce3c74a2da5",
        ["reference/v1/text-glow-marquee.document.json"] = "8ff1de5ca3054470b0f3f610b311949d3b2eaa311f662a86a6b8f6c56052f07a",
        ["reference/v1/text-glow-overlay.document.json"] = "565d6f6afa9f62cc7fc21637b503767260e40753451b082ff4c19b7dedb27b6f",
        ["reference/v1/text-glow-plus.document.json"] = "13e85d71c76d1cecf20c7fc686be4686044bade623e219c31556522a007b8f0a",
        ["reference/v1/text-glow-screen.document.json"] = "dcdc4ed7e073d02f0310610489b0dc181e7f48a2f17b6f0f6272754d5a147102",
        ["reference/v1/text-glow-soft-light.document.json"] = "d1f3a9bb43f22a63ba9ece9b2f88bad870f4b38a42468d5a928a10cfb6ee299a",
        ["reference/v1/text-glow.document.json"] = "bc45eef45b50286979555a612549bdbf34b183d71c0a2973d20990bf84f10ee2",
        ["reference/v1/text-left.document.json"] = "f2d995173448e5ff512fe13320b033192639981b4274edb7b23b9d3936f8f0f8",
        ["reference/v1/text-marquee-t0.document.json"] = "df0af1da5d1a04c2c2a65040b11b0639e730470798c847dd8939eb03501aff37",
        ["reference/v1/text-right-auto.document.json"] = "7159617ebbced13359fde4c8661babc5588ba866d8b52587b4d8bf58b8c1bc53",
        ["reference/v1/text-rotated-30.document.json"] = "53f7215cbd3a45ca0c66df1fcfe62566d0fc9ac50490e0bd0c8bd7b19e31d8dd",
        ["reference/widgets/peak-bar.document.json"] = "15b7a4a7a5993cf1bcb8ecf307d646a7658c579e699314b952968ba65962cd99",
        ["reference/widgets/segment-gauge.document.json"] = "f9032d9040d47c8dcfe8f2ae80ab21c6053b6356dce88fe570bf854963f1f799",
        ["reference/widgets/sparkline-card.document.json"] = "8526b8484b97dd2c343342c7ccb73f84b51d8d8ddfc37144fe2f24524301ab07",
    };

    /// <summary>600 scenes of 190 rectangles each: about 5.17 MiB serialised.</summary>
    private static Generation Large()
    {
        var items = new JsonObject[600];
        for (var i = 0; i < items.Length; i++)
        {
            var nodes = new JsonArray();
            for (var n = 0; n < 190; n++)
                nodes.Add(new JsonObject { ["kind"] = "rect", ["w"] = 20, ["h"] = 10, ["fill"] = "#ff0000" });
            items[i] = Scene($"i{i}", i % 100, 10, new JsonObject { ["format"] = "infopanel.scene/1", ["nodes"] = nodes });
        }
        return Load(Doc(items));
    }

    /// <summary>A document whose opaque <c>origin</c> holds member names and values the canonical form must order and rewrite.</summary>
    private static JsonElement Names()
    {
        var origin = JsonNode.Parse("""
            {
              "kind": "import",
              "\uFB33": 1, "\uD83D\uDE00": 2, "z": 3, "Z": 4, "\u00e9": 5, "e\u0301": 6, "a\"b": 7, "a\\b": 8, "a\u0001": 9,
              "\u0041lpha": 10, "": 11, "aa": 12, "a": 13, "a b": 21, "a!": 22, "a#": 23, "A": 24, "\u007f": 14, "\u0080": 15, "\uffff": 16,
              "דּx": 17, "😀x": 18, "éx": 19, "€": 20,
              "numbers": [1.0, 1e2, -0, 0.000001, 1e-7, 123456789012345678, 1.5e300, 0.1, 100, -1.25E+3],
              "strings": ["\u00e9\n\t\"\\\u001f\u2028", "plain", "\uD83D\uDE00", "\/", "😀 דּ"],
              "nested": {"b": {"\uD83D\uDE00": [], "\uFB33": {}}, "a": [{"y": 1, "x": 2}]}
            }
            """)!;
        return Doc(Scene("a", 1.0, 2.50, extra: new JsonObject { ["origin"] = origin }), Group("g", Scene("c", 1e1, -0.0)));
    }

    private static IEnumerable<(string Name, string Path)> Documents()
    {
        var examples = Path.Combine(Path.GetDirectoryName(TestPaths.ProjectDirectory)!, "spec", "examples", "document", "valid");
        foreach (var path in Directory.GetFiles(examples, "*.json"))
            yield return ("examples/" + Path.GetFileName(path), path);
        foreach (var folder in new[] { "v1", "widgets" })
            foreach (var path in Directory.GetFiles(Path.Combine(TestPaths.ProjectDirectory, "Scenes", "Reference", folder), "*.json"))
                yield return ($"reference/{folder}/" + Path.GetFileName(path), path);
    }

    private static Generation Load(JsonElement document)
    {
        var load = Generation.Load(document);
        Assert.True(load.Generation is not null, string.Join("; ", load.Check.Errors.Select(e => e.Message)));
        return load.Generation;
    }

    private static long AllocatedBy(Action action)
    {
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string Mib(long bytes) => (bytes / 1048576.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
