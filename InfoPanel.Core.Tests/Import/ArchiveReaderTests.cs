using System.IO.Compression;
using System.Text;
using InfoPanel.Import;
using Xunit;
using static InfoPanel.Core.Tests.Import.ZipKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// The guarded zip reader both archive kinds share: the container, entry names, links, encryption, methods, and
/// decompressed bytes counted as they are read.
/// </summary>
public class ArchiveReaderTests
{
    [Fact]
    public void NotAZip_IsArchiveZip()
    {
        Assert.Equal("archive.zip", Refused(() => Open("not a zip at all"u8.ToArray())).Code);
        Assert.Equal("archive.zip", Refused(() => Open([])).Code);
    }

    [Fact]
    public void Entries_OverTheLimit_AreArchiveEntries()
    {
        var entries = Enumerable.Range(0, 10_001).Select(i => ($"assets/{i}.png", new byte[] { 1 })).ToArray();
        var zip = Zip(CompressionLevel.NoCompression, entries);

        var error = Refused(() => Open(zip));

        Assert.Equal("archive.entries", error.Code);
        Assert.Contains("10001", error.Message);

        // 10,000 is allowed.
        using var reader = Open(Zip(CompressionLevel.NoCompression, entries[..10_000]));
        Assert.Equal(10_000, reader.Entries.Count);
    }

    [Fact]
    public void Entries_ClaimedByTheEndRecord_AreRefusedBeforeTheDirectoryIsRead()
    {
        // The end record claims more entries than the limit: refused from it, whatever the directory holds.
        var zip = ClaimingEntries(Zip(Text("Profile.xml", "<Profile />")), 10_001);

        Assert.Equal("archive.entries", Refused(() => Open(zip)).Code);
        Assert.Equal("archive.entries", Refused(() => Open(Zip(Text("a", "1"), Text("b", "2")), new ImportLimits { Entries = 1 })).Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Zip64_SentinelOffsetOrDisk_IsCheckedBeforeTheDirectoryIsRead(bool sentinelOffset, bool sentinelDisk)
    {
        // .NET reads the Zip64 record when the end record's directory offset or disk number is a sentinel, even with a
        // true count and size, and then reads as many entries as the Zip64 record declares. The pre-check reads it
        // whenever it is there.
        var zip = Zip(Text("Profile.xml", "<Profile />"));
        using (var reader = Open(WithZip64(zip, 1, sentinelOffset, sentinelDisk)))
            Assert.Single(reader.Entries);

        var error = Refused(() => Open(WithZip64(zip, 10_001, sentinelOffset, sentinelDisk)));

        Assert.Equal("archive.entries", error.Code);
        Assert.Contains("10001", error.Message);
    }

    [Fact]
    public void DirectoryRecords_AreWalked_WhateverTheEndRecordClaims()
    {
        // One record of 46 + 5 + 5,120 bytes, over the 4,096 a record may take, under an end record claiming a
        // directory of 0 bytes: the declared size bounds nothing .NET reads, so the records themselves are checked.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a.txt");
            entry.Comment = new string('c', 5 * 1024);
            using var stream = entry.Open();
            stream.Write([1]);
        }
        var zip = ClaimingDirectorySize(buffer.ToArray(), 0);

        Assert.Equal("archive.entries", Refused(() => Open(zip)).Code);
    }

    [Fact]
    public void DirectoryRecords_WithALongRawName_AreRefusedBeforeAnyZipArchiveExists()
    {
        // The pre-check alone, called directly: a raw name one byte over the limit under a claimed directory of 0 bytes.
        var zip = ClaimingDirectorySize(Zip(Text("assets/" + new string('a', 1024 - 7 + 1), "x")), 0);

        var error = Refused(() => ArchiveReader.CheckDirectory(new MemoryStream(zip), ImportLimits.Default));

        Assert.Equal("archive.name", error.Code);

        // At the limit the pre-check passes.
        ArchiveReader.CheckDirectory(new MemoryStream(ClaimingDirectorySize(Zip(Text("assets/" + new string('a', 1024 - 7), "x")), 0)), ImportLimits.Default);
    }

    [Fact]
    public void DirectoryRecord_RunningPastTheEndOfTheFile_IsArchiveZip()
    {
        // The only record claims a 4,000-byte comment in an archive of about 150 bytes.
        var zip = Zip(Text("a", "1"));
        var record = zip.Length - 22 - (46 + 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(record + 32), 4000);

        Assert.Equal("archive.zip", Refused(() => ArchiveReader.CheckDirectory(new MemoryStream(zip), ImportLimits.Default)).Code);
    }

    [Theory]
    [InlineData("../x", "'..' segment")]
    [InlineData("assets/../../x", "'..' segment")]
    [InlineData("assets\\..\\..\\x", "'..' segment")]
    [InlineData("./x", "'.' segment")]
    [InlineData("assets/./x", "'.' segment")]
    [InlineData("/abs", "rooted")]
    [InlineData("\\abs", "rooted")]
    [InlineData("\\\\srv\\x", "rooted")]
    [InlineData("C:\\x", "':'")]
    [InlineData("C:x", "':'")]
    [InlineData("assets/x.png:stream", "':'")]
    [InlineData("assets/a\0.png", "NUL")]
    [InlineData("assets//x.png", "empty segment")]
    [InlineData("assets\\\\x.png", "empty segment")]
    public void Names_AreChecked(string name, string reason)
    {
        var error = Refused(() => Open(Zip((name, [1]))));

        Assert.Equal("archive.name", error.Code);
        Assert.Contains(reason, error.Message);
        Assert.Equal(ImportRefusal.EntryPath(name), error.Errors[0].Path);
    }

    [Fact]
    public void Names_OverTheByteLimit_AreRefused()
    {
        // Counted in UTF-8 bytes: 512 two-byte characters are 1,024 bytes.
        var atLimit = "assets/" + new string('é', (1024 - 7) / 2);
        Assert.True(Encoding.UTF8.GetByteCount(atLimit) <= 1024);
        using (var reader = Open(Zip((atLimit, [1]))))
            Assert.Single(reader.Entries);

        var over = "assets/" + new string('é', 1024);
        var error = Refused(() => Open(Zip((over, [1]))));
        Assert.Equal("archive.name", error.Code);
        Assert.Contains("longer than 1024 UTF-8 bytes", error.Message);
    }

    [Fact]
    public void ExactDuplicates_AreRefused_CaseDuplicatesAreLeftToTheKind()
    {
        Assert.Equal("archive.duplicate", Refused(() => Open(Zip(Text("assets/a.png", "1"), Text("assets/a.png", "2")))).Code);

        using var reader = Open(Zip(Text("assets/a.png", "1"), Text("assets/A.png", "2"), Text("assets\\a.png", "3")));
        Assert.Equal(["assets/a.png", "assets/A.png", "assets\\a.png"], reader.Entries.Select(e => e.Name));
        Assert.All(reader.Entries, e => Assert.Equal(["assets", e.Name[7..]], e.Segments));
    }

    [Fact]
    public void SymbolicLinks_AreRefused()
    {
        var link = WithAttributes("assets/link.png", "/etc/passwd"u8.ToArray(), unchecked((int)0xA1FF0000));

        Assert.Equal("archive.link", Refused(() => Open(link)).Code);

        // A regular file with Unix permissions is fine.
        using var reader = Open(WithAttributes("assets/file.png", [1], unchecked((int)0x81A40000)));
        Assert.Single(reader.Entries);
    }

    [Fact]
    public void EncryptedEntries_AreRefused()
    {
        var zip = Encrypted(Zip(Text("Profile.xml", "<Profile />"), Text("DisplayItems.xml", "<ArrayOfDisplayItem />")), index: 1);

        var error = Refused(() => Open(zip));

        Assert.Equal("archive.encrypted", error.Code);
        Assert.Equal(ImportRefusal.EntryPath("DisplayItems.xml"), error.Errors[0].Path);
    }

    [Fact]
    public void UnknownMethods_AreArchiveMethod()
    {
        var zip = WithMethod(Zip(Text("Profile.xml", "<Profile />")), 12);
        using var reader = Open(zip);

        var error = Refused(() => reader.Read(reader.Entries[0], 1024));

        Assert.Equal("archive.method", error.Code);
    }

    [Fact]
    public void Bomb_WhoseHeaderUnderstatesItsSize_IsCountedAsRead()
    {
        // A stored entry's bytes are read by its compressed size, whatever its uncompressed size claims: 2 MiB that
        // claim 100 bytes. Only counting what is read keeps it within the request's budget.
        var zip = ClaimingSize(Zip(CompressionLevel.NoCompression, ("assets/bomb.png", new byte[2 * 1024 * 1024])), 100);
        using var reader = Open(zip, budget: new ImportBudget(1024 * 1024));
        Assert.Equal(100, reader.Entries[0].DeclaredLength);

        var error = Refused(() => reader.Read(reader.Entries[0], long.MaxValue));

        Assert.Equal("archive.expanded", error.Code);
    }

    [Fact]
    public void DeflateBomb_WhoseHeaderUnderstatesItsSize_NeverExpandsPastTheBudget()
    {
        // 4 MiB of zeros deflate to a few KiB; the headers claim 100 bytes. .NET stops a deflated entry at its claimed
        // size; whichever stops it first, no more than the budget is ever decompressed.
        var zip = ClaimingSize(Zip(("assets/bomb.png", new byte[4 * 1024 * 1024])), 100);
        var budget = new ImportBudget(1024 * 1024);
        using var reader = Open(zip, budget: budget);

        try
        {
            Assert.True(reader.Read(reader.Entries[0], long.MaxValue)!.Length <= 1024 * 1024);
        }
        catch (ImportRefusal refusal)
        {
            Assert.Equal("archive.expanded", refusal.Code);
        }
        Assert.True(budget.Remaining >= -81920);
    }

    [Fact]
    public void EntryWhoseHeaderOverstatesItsSize_IsReadAtItsRealSize()
    {
        // Ten real bytes claiming 900,000: two of them fit a 1,000,000-byte budget, since what counts is what is read.
        var zip = ClaimingSize(ClaimingSize(Zip(("a", new byte[10]), ("b", new byte[10])), 900_000, index: 0), 900_000, index: 1);
        using var reader = Open(zip, budget: new ImportBudget(1_000_000));

        Assert.Equal(10, reader.Read(reader.Entries[0], long.MaxValue)!.Length);
        Assert.Equal(10, reader.Read(reader.Entries[1], long.MaxValue)!.Length);
    }

    [Fact]
    public void Budget_IsSharedByEveryReadOfTheRequest()
    {
        var zip = Zip(CompressionLevel.NoCompression, ("a", new byte[600_000]), ("b", new byte[600_000]));
        using var reader = Open(zip, budget: new ImportBudget(1_000_000));

        Assert.Equal(600_000, reader.Read(reader.Entries[0], long.MaxValue)!.Length);
        Assert.Equal("archive.expanded", Refused(() => reader.Read(reader.Entries[1], long.MaxValue)).Code);
    }

    [Fact]
    public void Read_OverItsOwnCap_IsNull_AndStopsReading()
    {
        var zip = Zip(("a", new byte[10_000]), ("b", new byte[10]));
        var budget = new ImportBudget(1_000_000);
        using var reader = Open(zip, budget: budget);

        Assert.Null(reader.Read(reader.Entries[0], 9_999));
        Assert.Equal(new byte[10], reader.Read(reader.Entries[1], 10));
        Assert.Equal(new byte[10_000], reader.Read(reader.Entries[0], 10_000));
    }

    [Fact]
    public void DirectoryEntries_AreReadAsDirectories()
    {
        using var reader = Open(Zip(("assets/", []), ("assets\\sub\\", []), Text("assets/sub/a.png", "x")));

        Assert.Equal([true, true, false], reader.Entries.Select(e => e.IsDirectory));
        Assert.Equal(["assets", "sub"], reader.Entries[1].Segments);
    }

    [Fact]
    public void TempFile_Buffer_LivesInTheOsTempFolder_AndGoesWhenClosed()
    {
        string path;
        using (var file = TempFile.Buffer(new MemoryStream(new byte[1000]), maxBytes: 1000))
        {
            path = file.Name;
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar));
            Assert.Equal(0, file.Position);
            Assert.Equal(1000, file.Length);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.False(File.Exists(path));

        Assert.Throws<ImportBodyTooLargeException>(() => TempFile.Buffer(new MemoryStream(new byte[1001]), maxBytes: 1000));
    }
}
