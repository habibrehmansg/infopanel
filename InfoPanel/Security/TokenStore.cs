using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InfoPanel.Security;

/// <summary>What an integration token may do. <see cref="Edit"/> always comes with <see cref="Read"/>.</summary>
[Flags]
public enum TokenScopes
{
    None = 0,
    Read = 1,
    Edit = 2,
}

/// <summary>
/// Who made a request: the session token (<see cref="Session"/>, full access) or an integration token, with its id and
/// scopes. Set by <see cref="SecurityMiddleware"/> on every authenticated request (<see cref="Of"/>).
/// </summary>
public sealed record Caller(string Kind, string? TokenId, TokenScopes Scopes)
{
    public const string SessionKind = "session";
    public const string IntegrationKind = "integration";

    private const string Item = "InfoPanel.Caller";

    public static Caller Session { get; } = new(SessionKind, null, TokenScopes.Read | TokenScopes.Edit);

    public bool IsSession => Kind == SessionKind;

    /// <summary>Whether this caller may use an endpoint that needs <paramref name="scope"/>; only the session token has <see cref="ApiScope.Session"/>.</summary>
    public bool Allows(ApiScope scope) => IsSession || scope switch
    {
        ApiScope.Read => Scopes.HasFlag(TokenScopes.Read),
        ApiScope.Edit => Scopes.HasFlag(TokenScopes.Edit),
        _ => false,
    };

    /// <summary>The token's secret is <paramref name="token"/>: the session token's caller, an integration token's, or null.</summary>
    public static Caller? Authenticate(string? token, EngineRuntime runtime, TokenStore tokens) =>
        runtime.IsSessionToken(token) ? Session : tokens.Authenticate(token);

    public static Caller? Of(HttpContext context) => context.Items.TryGetValue(Item, out var caller) ? caller as Caller : null;

    public void SetOn(HttpContext context) => context.Items[Item] = this;
}

/// <summary>A stored integration token, without its secret: <see cref="Hash"/> is the SHA-256 of the secret's UTF-8 bytes.</summary>
public sealed record TokenRecord(string Id, string Name, TokenScopes Scopes, DateTimeOffset Created, DateTimeOffset? LastUsed, byte[] Hash)
{
    public string Kind => Caller.IntegrationKind;

    /// <summary>The token's members in the file that this engine doesn't know (a later engine's), as written, kept when it rewrites the file.</summary>
    internal IReadOnlyList<JsonMemberText>? Unknown { get; init; }
}

/// <summary>
/// A create that would take <c>tokens.json</c> over its cap, <c>tokensBytes</c> (spec/docs/limits.md): nothing was
/// created or written.
/// </summary>
public sealed class TokensFullException(long bytes, long limit)
    : IOException($"The stored tokens would be {bytes} bytes, over the limit of {limit} (tokensBytes).")
{
    /// <summary>The bytes the file would take.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>Its cap.</summary>
    public long Limit { get; } = limit;
}

/// <summary>
/// The integration tokens (spec/docs/api.md, "Authentication"), kept in <c>{data}/tokens.json</c>: each token's id,
/// name, kind, scopes, creation and last use, and the SHA-256 of its secret, never the secret, written with
/// <see cref="AtomicFile.WritePrivate"/> (0600 on Unix, in the 0700 data folder). A secret is <see cref="SecretPrefix"/>
/// and 256 random bits in base64url; it is answered once, by <see cref="Create"/>, and never logged. A presented token is
/// hashed and compared with every stored hash in constant time. <see cref="Revoke"/> removes a token at once and raises
/// <see cref="Revoked"/>, so open connections authenticated with it can close.
/// <para>The file is read tolerantly: members this engine doesn't know, in the file and in each token, are ignored and
/// kept, as written (<see cref="EngineJson"/>), whenever it is rewritten; the file, kept members included, is held to
/// <see cref="MaxFileBytes"/>, read only when it is a regular file, and written compact. Whatever a later engine adds
/// that an earlier one must enforce (a rule a token has to meet) comes with a capability in the data folder's marker,
/// so an earlier engine stops before it reads this file (docs/2.0/data-folder.md). A kind or scope it doesn't know
/// still stops it.</para>
/// </summary>
public sealed partial class TokenStore
{
    /// <summary>The prefix of every integration token's secret, so a leaked one is recognisable.</summary>
    public const string SecretPrefix = "ipi_";

    public const int MaxNameLength = 64;

    /// <summary>
    /// The most bytes of <c>tokens.json</c>, the members it keeps included, <c>tokensBytes</c> (spec/docs/limits.md): a
    /// larger file isn't read (the engine stops), and a create that would take it over is refused
    /// (<see cref="TokensFullException"/>), nothing written. About 80,000 tokens.
    /// </summary>
    public static int MaxFileBytes => SpecLimits.Default.TokensBytes;

    private const int SecretBytes = 32;

    /// <summary>How long a token's last use may be behind before the file is rewritten for it.</summary>
    internal static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    /// <summary>Compact, and read at one depth, the formats' <c>jsonDepth</c>, as every engine file is.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = EngineJson.MaxDepth,
    };

    /// <summary>The members of the file and of a token that this engine reads (the serializer matches them in any case).</summary>
    private static readonly string[] FileMembers = ["tokens"];

    private static readonly string[] TokenMembers = ["id", "name", "kind", "scopes", "created", "lastUsed", "sha256"];

    private readonly string _path;
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _logger;
    private readonly Lock _lock = new();
    /// <summary>
    /// Replaced, never changed, under <see cref="_lock"/>; read without it. The tokens as they are in the file, but for
    /// uses whose write failed (kept here so a failing write is tried again a minute later, not by every request).
    /// </summary>
    private volatile TokenRecord[] _records;

    /// <summary>
    /// The tokens exactly as the file holds them (as read, or as last written), under <see cref="_lock"/>. A create or
    /// revoke writes from these, never from <see cref="_records"/>, so a use that couldn't be written never makes a
    /// revoke's file larger than the one on disk: a revoke can always be written, whatever its size.
    /// </summary>
    private TokenRecord[] _saved;

    /// <summary>The file's top-level members this engine doesn't know, written back as they were read.</summary>
    private readonly IReadOnlyList<JsonMemberText>? _unknown;

    private TokenStore(string path, string root, TimeProvider time, Serilog.ILogger logger, TokenRecord[] records, IReadOnlyList<JsonMemberText>? unknown)
    {
        _path = path;
        _root = root;
        _time = time;
        _logger = logger;
        _records = records;
        _saved = records;
        _unknown = unknown;
    }

    /// <summary>Raised with a token's id after it is revoked.</summary>
    public event Action<string>? Revoked;

    /// <summary>Tests: called for every stored hash a presented token is compared with.</summary>
    internal Action? Compared;

    /// <summary>Tests: called before every write of the file; an exception thrown here is the write failing.</summary>
    internal Action? Saving;

    /// <summary>
    /// Reads <paramref name="path"/> (none yet: no tokens) and removes temp files an interrupted write left beside it.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file can't be read as tokens, is larger than <see cref="MaxFileBytes"/>, or something else is at its path (a
    /// folder, a FIFO, a link whose target can't be reached); it is left as it is.
    /// </exception>
    public static TokenStore Open(string path, string root, TimeProvider time, Serilog.ILogger logger)
    {
        RemoveTempFiles(path, logger);
        TokenRecord[] records;
        IReadOnlyList<JsonMemberText>? unknown;
        try
        {
            (records, unknown) = Parse(RegularFile.ReadAtMost(path, MaxFileBytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && RegularFile.StateOf(path) == PathState.Absent)
        {
            // Only a path with nothing at all is no tokens yet: a link to a drive that isn't there is tokens that can't be read.
            (records, unknown) = ([], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException
            or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"{path} can't be read ({ex.Message}). Fix it, or delete it to revoke every integration token.", ex);
        }

        return new TokenStore(path, root, time, logger, records, unknown);
    }

    /// <summary>Every token, oldest first.</summary>
    public IReadOnlyList<TokenRecord> List() => _records;

    /// <summary>Creates and stores a token; the secret is in the answer only.</summary>
    /// <exception cref="TokensFullException">The file would be over its cap, <c>tokensBytes</c>; nothing was created.</exception>
    /// <exception cref="IOException">The file could not be written; nothing was created.</exception>
    public (TokenRecord Record, string Secret) Create(string name, TokenScopes scopes)
    {
        if (scopes.HasFlag(TokenScopes.Edit))
            scopes |= TokenScopes.Read;
        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var created = Truncate(_time.GetUtcNow());
        var record = new TokenRecord(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), name, scopes, created, null, Hash(secret));
        lock (_lock)
        {
            TokenRecord[] next = [.. _saved, record];
            Save(next);
            _saved = next;
            _records = [.. _records, record];
        }

        _logger.Information("Created integration token {TokenId} ({TokenName}) with scopes {Scopes}", record.Id, name, ScopeNames(scopes));
        return (record, secret);
    }

    /// <summary>Removes a token: false when there is none with <paramref name="id"/>.</summary>
    /// <exception cref="IOException">The file could not be written; the token is kept.</exception>
    /// <remarks>
    /// The file written is the one on disk without the token (<see cref="_saved"/>), so it is never larger than the file
    /// already there, which is within the cap: a revoke is never refused for it.
    /// </remarks>
    public bool Revoke(string id)
    {
        TokenRecord? removed;
        lock (_lock)
        {
            removed = _records.FirstOrDefault(r => r.Id == id);
            if (removed == null)
                return false;
            var next = _saved.Where(r => r.Id != id).ToArray();
            Save(next);
            _saved = next;
            _records = _records.Where(r => r.Id != id).ToArray();
        }

        _logger.Information("Revoked integration token {TokenId} ({TokenName})", removed.Id, removed.Name);
        Revoked?.Invoke(id);
        return true;
    }

    /// <summary>Whether a token with <paramref name="id"/> exists (it hasn't been revoked).</summary>
    public bool IsActive(string id) => _records.Any(r => r.Id == id);

    /// <summary>
    /// The caller for an integration token's secret, or null. The secret's hash is compared with every stored hash in
    /// constant time; a match records the token's use (written to the file at most once per <see cref="LastUsedResolution"/>).
    /// </summary>
    public Caller? Authenticate(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return null;

        var hash = Hash(secret);
        TokenRecord? found = null;
        // Every stored hash, without stopping at a match, so the time taken doesn't depend on which token matched.
        foreach (var record in _records)
        {
            Compared?.Invoke();
            if (CryptographicOperations.FixedTimeEquals(hash, record.Hash))
                found = record;
        }

        if (found == null)
            return null;
        Touch(found);
        return new Caller(Caller.IntegrationKind, found.Id, found.Scopes);
    }

    private void Touch(TokenRecord record)
    {
        var now = Truncate(_time.GetUtcNow());
        if (record.LastUsed is { } seen && now - seen < LastUsedResolution)
            return;

        var id = record.Id;
        lock (_lock)
        {
            var index = Array.FindIndex(_records, r => r.Id == id);
            if (index < 0 || _records[index].LastUsed is { } last && now - last < LastUsedResolution)
                return;

            var next = (TokenRecord[])_records.Clone();
            next[index] = next[index] with { LastUsed = now };
            try
            {
                Save(next);
                _saved = next;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The use is still allowed. The file is unchanged, and so is what a create or revoke writes from
                // (_saved); the time is kept only in _records, so the write is tried again a minute later rather than on
                // every request while it fails. Past the cap (TokensFullException) it is the same: nothing is written.
                _logger.Warning(ex, "Could not record the use of integration token {TokenId} in {Path}", id, _path);
            }

            _records = next;
        }
    }

    /// <exception cref="TokensFullException">The file would be over <see cref="MaxFileBytes"/>; it is unchanged.</exception>
    /// <exception cref="IOException">The file couldn't be written; it is unchanged.</exception>
    private void Save(TokenRecord[] records)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tokens");
            foreach (var r in records)
            {
                var token = new StoredToken(r.Id, r.Name, r.Kind, ScopeNames(r.Scopes), Format(r.Created),
                    r.LastUsed is { } used ? Format(used) : null, Convert.ToHexStringLower(r.Hash));
                writer.WriteRawValue(EngineJson.Append(JsonSerializer.SerializeToUtf8Bytes(token, Json), r.Unknown), skipInputValidation: true);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = EngineJson.Append(stream.ToArray(), _unknown);
        if (bytes.Length > MaxFileBytes)
            throw new TokensFullException(bytes.Length, MaxFileBytes);
        Saving?.Invoke();
        AtomicFile.WritePrivate(_path, bytes, _root);
    }

    /// <summary>
    /// The tokens in the file, and the members of the file and of each token this engine doesn't know, as written (in a
    /// file the serializer read, so a well-formed object within jsonDepth).
    /// </summary>
    private static (TokenRecord[] Records, IReadOnlyList<JsonMemberText>? Unknown) Parse(byte[] bytes)
    {
        var file = JsonSerializer.Deserialize<TokenFile>(bytes, Json) ?? throw new JsonException("The file is null.");
        if (file.Tokens == null)
            throw new JsonException("No \"tokens\" array.");

        var members = EngineJson.Members(bytes);
        var stored = members.LastOrDefault(m => IsKnown(m, FileMembers));
        List<byte[]> elements = stored is null ? [] : EngineJson.Elements(stored.Value);
        if (elements.Count != file.Tokens.Count)
            throw new JsonException("The tokens can't be told apart.");

        var records = new List<TokenRecord>();
        for (var i = 0; i < file.Tokens.Count; i++)
        {
            var token = file.Tokens[i];
            if (token == null || token.Id == null || !TokenId().IsMatch(token.Id) || records.Any(r => r.Id == token.Id))
                throw new JsonException("A token has a missing, malformed or repeated id.");
            if (token.Kind != Caller.IntegrationKind)
                throw new JsonException($"Token {token.Id} has the kind '{token.Kind}'.");
            if (token.Name == null || !IsValidName(token.Name))
                throw new JsonException($"Token {token.Id} has a malformed name.");
            if (token.Sha256 == null || !Sha256Hex().IsMatch(token.Sha256))
                throw new JsonException($"Token {token.Id} has a malformed sha256.");
            if (token.Created == null)
                throw new JsonException($"Token {token.Id} has no creation time.");

            var scopes = TokenScopes.None;
            foreach (var name in token.Scopes ?? [])
                scopes |= ParseScope(name) ?? throw new JsonException($"Token {token.Id} has the unknown scope '{name}'.");
            if (!scopes.HasFlag(TokenScopes.Read))
                throw new JsonException($"Token {token.Id} has no read scope.");

            records.Add(new TokenRecord(token.Id, token.Name, scopes, ParseTime(token.Created),
                token.LastUsed is { } used ? ParseTime(used) : null, Convert.FromHexString(token.Sha256))
            {
                Unknown = Unknown(EngineJson.Members(elements[i]), TokenMembers),
            });
        }

        return ([.. records], Unknown(members, FileMembers));
    }

    /// <summary>Whether the serializer reads <paramref name="member"/> as one of <paramref name="known"/> (it matches names in any case).</summary>
    private static bool IsKnown(JsonMemberText member, string[] known) =>
        member.Name is { } name && known.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<JsonMemberText>? Unknown(List<JsonMemberText> members, string[] known) =>
        members.Where(m => !IsKnown(m, known)).ToList() is { Count: > 0 } unknown ? unknown : null;

    /// <summary>A token name: 1 to <see cref="MaxNameLength"/> letters, digits, spaces, dots, underscores and hyphens, not starting or ending with a space.</summary>
    public static bool IsValidName(string name) => TokenName().IsMatch(name);

    /// <summary><c>read</c> or <c>edit</c>; null for anything else.</summary>
    public static TokenScopes? ParseScope(string? name) => name switch
    {
        "read" => TokenScopes.Read,
        "edit" => TokenScopes.Edit,
        _ => null,
    };

    /// <summary>The scopes' names, in the order <c>read</c>, <c>edit</c>.</summary>
    public static List<string> ScopeNames(TokenScopes scopes)
    {
        var names = new List<string>();
        if (scopes.HasFlag(TokenScopes.Read))
            names.Add("read");
        if (scopes.HasFlag(TokenScopes.Edit))
            names.Add("edit");
        return names;
    }

    /// <summary>A time as the API and the file write it: UTC to the second, <c>2026-10-27T09:30:00Z</c>.</summary>
    public static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RemoveTempFiles(string path, Serilog.ILogger logger)
    {
        var folder = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".";
        foreach (var entry in RegularFile.List(folder, new ListingBudget(RegularFile.MaxListing, $"Listing {folder}")))
        {
            var temp = entry.FullName;
            var name = entry.Name;
            if (entry is not FileInfo || !name.StartsWith(prefix, StringComparison.Ordinal) || !TempPart().IsMatch(name[(prefix.Length - 1)..]))
                continue;
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Could not remove {Path}", temp);
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\z")]
    internal static partial Regex TokenId();

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex("^\\.[0-9a-f]{32}\\.tmp\\z")]
    private static partial Regex TempPart();

    [GeneratedRegex("^[A-Za-z0-9._-]([A-Za-z0-9 ._-]{0,62}[A-Za-z0-9._-])?\\z")]
    private static partial Regex TokenName();

    private sealed record TokenFile(List<StoredToken?>? Tokens);

    private sealed record StoredToken(string? Id, string? Name, string? Kind, List<string>? Scopes, string? Created, string? LastUsed, string? Sha256);
}
