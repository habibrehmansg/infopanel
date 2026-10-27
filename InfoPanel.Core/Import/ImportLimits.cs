namespace InfoPanel.Import;

/// <summary>
/// The limits of an import: archive bodies and their entries, the 1.x XML files, the 1.x settings, the migration
/// record and the diagnostics an answer lists. Every number is an option, so tests can lower it. Media limits
/// (<c>mediaBytes</c> and the rest) and the document limits come from the workspace's <c>SpecLimits</c>.
/// </summary>
public sealed class ImportLimits
{
    public static ImportLimits Default { get; } = new();

    /// <summary>The largest archive body, of either kind (the host answers 413 over it).</summary>
    public long ArchiveBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>The most entries an archive may have (<c>archive.entries</c>).</summary>
    public int Entries { get; init; } = 10_000;

    /// <summary>
    /// The most bytes one request decompresses or reads, counted as they are read (entry headers aren't trusted); reads
    /// of a 1.x folder's assets count too (<c>archive.expanded</c>).
    /// </summary>
    public long ExpandedBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>The longest entry name, in UTF-8 bytes (<c>archive.name</c>).</summary>
    public int EntryNameBytes { get; init; } = 1024;

    /// <summary>The largest <c>Profile.xml</c>, <c>profiles.xml</c> or <c>settings.xml</c>.</summary>
    public int ProfileXmlBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The largest items XML (<c>DisplayItems.xml</c>, <c>profiles/{guid}.xml</c>).</summary>
    public int ItemsXmlBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>The deepest element nesting in any 1.x XML file (the root element is at depth 1).</summary>
    public int XmlDepth { get; init; } = 64;

    /// <summary>The most elements in any 1.x XML file.</summary>
    public int XmlElements { get; init; } = 500_000;

    /// <summary>The most profiles read from <c>profiles.xml</c>; the rest are left out with a warning.</summary>
    public int Profiles { get; init; } = 1_000;

    /// <summary>The most devices read per device family of <c>settings.xml</c>.</summary>
    public int DevicesPerFamily { get; init; } = 64;

    /// <summary>The most fields read per device.</summary>
    public int FieldsPerDevice { get; init; } = 64;

    /// <summary>The longest settings value read, in characters.</summary>
    public int SettingValueLength { get; init; } = 1024;

    /// <summary>The most hotkeys read.</summary>
    public int Hotkeys { get; init; } = 256;

    /// <summary>The most profile outcomes in the migration record.</summary>
    public int RecordOutcomes { get; init; } = 1_000;

    /// <summary>The largest migration record, in bytes as stored.</summary>
    public int RecordBytes { get; init; } = 64 * 1024;

    /// <summary>The most diagnostics one answer lists; one last <c>more</c> diagnostic counts the rest.</summary>
    public int Diagnostics { get; init; } = 1_000;
}
