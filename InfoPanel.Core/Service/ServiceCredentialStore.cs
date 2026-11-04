using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Serilog;

namespace InfoPanel.Service;

/// <summary>
/// What a store keeps: the credential a client provided, the service address it was provided for (a change of address,
/// even one made while the engine was stopped, drops it), and whether the service refused it (401), which lasts across
/// restarts until a client provides another.
/// </summary>
public sealed record StoredServiceCredential(string Address, string Credential, bool Rejected)
{
    /// <summary>The most bytes a stored record takes before a store encrypts it: a credential and an address at their limits, with room.</summary>
    public const int MaxBytes = 16_384;

    /// <summary>The record as a store keeps it: <c>{"address":…,"credential":…,"rejected":…}</c> in UTF-8.</summary>
    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("address", Address);
            writer.WriteString("credential", Credential);
            writer.WriteBoolean("rejected", Rejected);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The record <paramref name="bytes"/> hold, or null when they aren't one (members it doesn't know are ignored).</summary>
    public static StoredServiceCredential? FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 4 });
            using var json = JsonDocument.ParseValue(ref reader);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("credential", out var credential) || credential.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("rejected", out var rejected) || rejected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var value = credential.GetString()!;
            return ServiceCredentials.IsWellFormed(value) ? new StoredServiceCredential(address.GetString()!, value, rejected.GetBoolean()) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Never the credential: a record's text names only its address and state.</summary>
    public override string ToString() => $"StoredServiceCredential {{ Address = {Address}, Rejected = {Rejected} }}";
}

/// <summary>The store couldn't keep, read or remove the credential (the API's 503 <c>storeUnavailable</c>).</summary>
/// <param name="applied">
/// The file was replaced or removed, and only flushing its folder failed: what a restart reads is the change, so the
/// change is in effect; the store's next write or removal flushes the folder again.
/// </param>
public sealed class ServiceStoreException(string message, Exception? inner = null, bool applied = false) : Exception(message, inner)
{
    /// <summary>The change reached the file; only making it durable failed.</summary>
    public bool Applied { get; } = applied;
}

/// <summary>
/// Where the engine keeps the credential a client provides for the configured service (<see cref="ServiceCredentials"/>):
/// one record at most, never in the settings.
/// </summary>
public interface IServiceCredentialStore
{
    /// <summary>The store's name in the API's <c>serviceState</c>: <c>private</c>, <c>protected</c> (or <c>keychain</c>).</summary>
    string Name { get; }

    /// <summary>The record kept, or null when there is none.</summary>
    /// <exception cref="ServiceStoreException">There is one the store can't read.</exception>
    StoredServiceCredential? Read();

    /// <summary>Keeps <paramref name="record"/> in place of any other, durably before it returns.</summary>
    /// <exception cref="ServiceStoreException">
    /// The store refused: the record it had is kept; or, <see cref="ServiceStoreException.Applied"/>, it holds
    /// <paramref name="record"/> but couldn't make that durable.
    /// </exception>
    void Write(StoredServiceCredential record);

    /// <summary>Removes the record, if any, durably before it returns: a removal not yet durable is finished.</summary>
    /// <exception cref="ServiceStoreException">
    /// The store couldn't remove it; or, <see cref="ServiceStoreException.Applied"/>, it removed it but couldn't make that
    /// durable.
    /// </exception>
    void Delete();
}

/// <summary>
/// A store that keeps the record in one file of the data folder, written with <see cref="AtomicFile"/> (a temp file,
/// flushed, renamed over it, its folder flushed); the temp files an interrupted write left are removed when it opens. On
/// macOS and Linux (<see cref="ForPrivateFile"/>) the file is the record itself, created 0600, in the 0700 data folder:
/// <c>service-credential</c>, <c>private</c>. On Windows (<see cref="ForProtectedFile"/>) it is the record encrypted for
/// the current user with DPAPI (<c>CryptProtectData</c>, no prompt ever), created with an access list for that user only:
/// <c>service-credential.bin</c>, <c>protected</c>. A write or removal whose folder flush failed is remembered, and the
/// next one flushes the folder again, a removal with nothing left to remove included. One caller at a time
/// (<see cref="ServiceCredentials"/> holds its lock).
/// </summary>
public sealed class FileServiceCredentialStore : IServiceCredentialStore
{
    /// <summary>The file a private store keeps the record in, in the data folder.</summary>
    public const string PrivateFileName = DataPaths.ServiceCredentialFileName;

    /// <summary>The file a protected store keeps the encrypted record in, in the data folder.</summary>
    public const string ProtectedFileName = DataPaths.ServiceCredentialProtectedFileName;

    /// <summary>The most bytes the file may hold: the largest record, encrypted, with room.</summary>
    private const int MaxFileBytes = 65_536;

    private readonly string _path;
    private readonly string? _root;
    private readonly Func<string, FileStream>? _createTemp;
    private readonly Func<byte[], byte[]> _seal;
    private readonly Func<byte[], byte[]> _open;
    private readonly IStorageFaults? _faults;

    /// <summary>The last write or removal changed the file but couldn't flush its folder.</summary>
    private bool _unflushed;

    private FileServiceCredentialStore(string name, string path, string? root, Func<string, FileStream>? createTemp, Func<byte[], byte[]> seal,
        Func<byte[], byte[]> open, ILogger? logger, IStorageFaults? faults = null)
    {
        Name = name;
        _path = Path.GetFullPath(path);
        _root = root;
        _createTemp = createTemp;
        _seal = seal;
        _open = open;
        _faults = faults;
        RemoveTempFiles(_path, logger ?? Log.ForContext<FileServiceCredentialStore>());
    }

    /// <summary>The private store, <c>{data}/service-credential</c> (0600): macOS and Linux.</summary>
    public static FileServiceCredentialStore ForPrivateFile(string dataFolder, ILogger? logger = null) => ForPrivateFile(dataFolder, logger, faults: null);

    /// <summary>The private store, its disk steps failed by <paramref name="faults"/> (tests).</summary>
    internal static FileServiceCredentialStore ForPrivateFile(string dataFolder, ILogger? logger, IStorageFaults? faults) =>
        new("private", Path.Combine(dataFolder, PrivateFileName), dataFolder, createTemp: null, seal: b => b, open: b => b, logger, faults);

    /// <summary>
    /// The protected store, <c>{data}/service-credential.bin</c>, encrypted for the current user with DPAPI: Windows.
    /// <paramref name="createTemp"/> creates the temp file with a private access list (the host's Windows file privacy).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static FileServiceCredentialStore ForProtectedFile(string dataFolder, Func<string, FileStream>? createTemp, ILogger? logger = null) =>
        new("protected", Path.Combine(dataFolder, ProtectedFileName), dataFolder, createTemp, UserDataProtection.Protect, UserDataProtection.Unprotect, logger);

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The file the record is kept in.</summary>
    public string FilePath => _path;

    /// <inheritdoc/>
    public StoredServiceCredential? Read()
    {
        byte[] bytes;
        try
        {
            switch (RegularFile.StateOf(_path))
            {
                case PathState.Absent:
                    return null;
                case PathState.Folder:
                    throw new ServiceStoreException("A folder is where the credential's file belongs.");
            }

            bytes = _open(RegularFile.ReadAtMost(_path, MaxFileBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicFailure)
        {
            throw new ServiceStoreException($"The credential's file couldn't be read: {ex.Message}", ex);
        }

        return StoredServiceCredential.FromBytes(bytes) ?? throw new ServiceStoreException("The credential's file doesn't hold a credential.");
    }

    /// <inheritdoc/>
    public void Write(StoredServiceCredential record)
    {
        var replaced = false;
        try
        {
            var sealedBytes = _seal(record.ToBytes());
            AtomicFile.Write(_path, sealedBytes, _root, _faults, replaced: () => replaced = true, ownerOnly: true, createTemp: _createTemp);
            _unflushed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure)
        {
            if (!replaced)
                throw new ServiceStoreException($"The credential couldn't be stored: {ex.Message}", ex);
            _unflushed = true;
            throw new ServiceStoreException($"The credential was stored, but couldn't be made to last: {ex.Message}", ex, applied: true);
        }
    }

    /// <inheritdoc/>
    public void Delete()
    {
        // Whether the file is gone: from then on a failure is only the folder's flush.
        var removed = false;
        try
        {
            if (RegularFile.StateOf(_path) != PathState.Absent)
            {
                _faults?.Before(StorageStep.DeleteFile, _path);
                File.Delete(_path);
            }
            else if (!_unflushed)
            {
                return;
            }

            removed = true;

            AtomicFile.SyncFolder(Path.GetDirectoryName(_path)!, _faults);
            _unflushed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!removed)
                throw new ServiceStoreException($"The credential couldn't be removed: {ex.Message}", ex);
            _unflushed = true;
            throw new ServiceStoreException($"The credential was removed, but couldn't be made to last: {ex.Message}", ex, applied: true);
        }
    }

    /// <summary>Removes <c>{file}.{32 hex}.tmp</c> files an interrupted write left: they may hold a credential.</summary>
    private static void RemoveTempFiles(string path, ILogger logger)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        IEnumerable<string> temps;
        try
        {
            if (RegularFile.StateOf(folder) != PathState.Folder)
                return;
            temps = RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}"))
                .Where(t => t is FileInfo && AtomicFile.IsTempFile(t.Name, target => target == name))
                .Select(t => t.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Couldn't look for temp files beside {File}", path);
            return;
        }

        foreach (var temp in temps)
        {
            try
            {
                File.Delete(temp);
                logger.Information("Removed {Temp}, left by an interrupted write of the service credential", temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Couldn't remove {Temp}, left by an interrupted write of the service credential", temp);
            }
        }
    }
}

/// <summary>DPAPI refused to encrypt or decrypt (its Win32 error in the message).</summary>
public sealed class CryptographicFailure(string message) : Exception(message);

/// <summary>
/// Windows' data protection for the current user (DPAPI), by hand: <c>CryptProtectData</c> and <c>CryptUnprotectData</c>
/// with <c>CRYPTPROTECT_UI_FORBIDDEN</c>, so it never shows anything, and a fixed entropy naming what the bytes are for.
/// What it encrypts only this user, on this computer, can decrypt.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class UserDataProtection
{
    private const int UiForbidden = 0x1;

    private static readonly byte[] Entropy = "InfoPanel service credential 1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    public static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    public static byte[] Unprotect(byte[] sealedBytes) => Transform(sealedBytes, protect: false);

    private static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* inputBytes = input)
        fixed (byte* entropyBytes = Entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (IntPtr)inputBytes };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (IntPtr)entropyBytes };
            var output = default(DataBlob);
            var done = protect
                ? CryptProtectData(&inBlob, IntPtr.Zero, &entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, &output)
                : CryptUnprotectData(&inBlob, IntPtr.Zero, &entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, &output);
            if (!done)
                throw new CryptographicFailure($"{(protect ? "CryptProtectData" : "CryptUnprotectData")} failed (error {Marshal.GetLastPInvokeError()}).");
            try
            {
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    // The plain text is wiped before it goes back to the system.
                    if (!protect)
                        new Span<byte>((void*)output.Data, output.Size).Clear();
                    LocalFree(output.Data);
                }
            }
        }
    }

    // BOOL CryptProtectData(DATA_BLOB *pDataIn, LPCWSTR szDataDescr, DATA_BLOB *pOptionalEntropy, PVOID pvReserved,
    //     CRYPTPROTECT_PROMPTSTRUCT *pPromptStruct, DWORD dwFlags, DATA_BLOB *pDataOut)
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(DataBlob* dataIn, IntPtr description, DataBlob* entropy, IntPtr reserved,
        IntPtr prompt, int flags, DataBlob* dataOut);

    // BOOL CryptUnprotectData(DATA_BLOB *pDataIn, LPWSTR *ppszDataDescr, DATA_BLOB *pOptionalEntropy, PVOID pvReserved,
    //     CRYPTPROTECT_PROMPTSTRUCT *pPromptStruct, DWORD dwFlags, DATA_BLOB *pDataOut)
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(DataBlob* dataIn, IntPtr description, DataBlob* entropy, IntPtr reserved,
        IntPtr prompt, int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
