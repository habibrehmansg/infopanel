using InfoPanel.Documents;
using InfoPanel.Hosting;
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
}

/// <summary>
/// The integration tokens (spec/docs/api.md, "Authentication"), kept in <c>{data}/tokens.json</c>: each token's id,
/// name, kind, scopes, creation and last use, and the SHA-256 of its secret, never the secret, written with
/// <see cref="AtomicFile.WritePrivate"/> (0600 on Unix, in the 0700 data folder). A secret is <see cref="SecretPrefix"/>
/// and 256 random bits in base64url; it is answered once, by <see cref="Create"/>, and never logged. A presented token is
/// hashed and compared with every stored hash in constant time. <see cref="Revoke"/> removes a token at once and raises
/// <see cref="Revoked"/>, so open connections authenticated with it can close.
/// </summary>
public sealed partial class TokenStore
{
    /// <summary>The prefix of every integration token's secret, so a leaked one is recognisable.</summary>
    public const string SecretPrefix = "ipi_";

    public const int MaxNameLength = 64;

    private const int SecretBytes = 32;

    /// <summary>How long a token's last use may be behind before the file is rewritten for it.</summary>
    internal static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _logger;
    private readonly Lock _lock = new();
    /// <summary>Replaced, never changed, under <see cref="_lock"/>; read without it.</summary>
    private volatile TokenRecord[] _records;

    private TokenStore(string path, string root, TimeProvider time, Serilog.ILogger logger, TokenRecord[] records)
    {
        _path = path;
        _root = root;
        _time = time;
        _logger = logger;
        _records = records;
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
    /// The file can't be read as tokens, or something else is at its path (a folder); it is left as it is.
    /// </exception>
    public static TokenStore Open(string path, string root, TimeProvider time, Serilog.ILogger logger)
    {
        RemoveTempFiles(path, logger);
        TokenRecord[] records;
        try
        {
            records = Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only a file that isn't there is no tokens yet.
            records = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new InvalidDataException(
                $"{path} can't be read ({ex.Message}). Fix it, or delete it to revoke every integration token.", ex);
        }

        return new TokenStore(path, root, time, logger, records);
    }

    /// <summary>Every token, oldest first.</summary>
    public IReadOnlyList<TokenRecord> List() => _records;

    /// <summary>Creates and stores a token; the secret is in the answer only.</summary>
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
            TokenRecord[] next = [.. _records, record];
            Save(next);
            _records = next;
        }

        _logger.Information("Created integration token {TokenId} ({TokenName}) with scopes {Scopes}", record.Id, name, ScopeNames(scopes));
        return (record, secret);
    }

    /// <summary>Removes a token: false when there is none with <paramref name="id"/>.</summary>
    /// <exception cref="IOException">The file could not be written; the token is kept.</exception>
    public bool Revoke(string id)
    {
        TokenRecord? removed;
        lock (_lock)
        {
            removed = _records.FirstOrDefault(r => r.Id == id);
            if (removed == null)
                return false;
            var next = _records.Where(r => r.Id != id).ToArray();
            Save(next);
            _records = next;
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The use is still allowed, and its time is kept in memory, so the write is tried again a minute later
                // (or by the next create or revoke) rather than on every request while it fails.
                _logger.Warning(ex, "Could not record the use of integration token {TokenId} in {Path}", id, _path);
            }

            _records = next;
        }
    }

    private void Save(TokenRecord[] records)
    {
        var file = new TokenFile([.. records.Select(r => new StoredToken(r.Id, r.Name, r.Kind, ScopeNames(r.Scopes), Format(r.Created),
            r.LastUsed is { } used ? Format(used) : null, Convert.ToHexStringLower(r.Hash)))]);
        Saving?.Invoke();
        AtomicFile.WritePrivate(_path, JsonSerializer.SerializeToUtf8Bytes(file, Json), _root);
    }

    private static TokenRecord[] Parse(byte[] bytes)
    {
        var file = JsonSerializer.Deserialize<TokenFile>(bytes, Json) ?? throw new JsonException("The file is null.");
        if (file.Tokens == null)
            throw new JsonException("No \"tokens\" array.");

        var records = new List<TokenRecord>();
        foreach (var stored in file.Tokens)
        {
            if (stored == null || stored.Id == null || !TokenId().IsMatch(stored.Id) || records.Any(r => r.Id == stored.Id))
                throw new JsonException("A token has a missing, malformed or repeated id.");
            if (stored.Kind != Caller.IntegrationKind)
                throw new JsonException($"Token {stored.Id} has the kind '{stored.Kind}'.");
            if (stored.Name == null || !IsValidName(stored.Name))
                throw new JsonException($"Token {stored.Id} has a malformed name.");
            if (stored.Sha256 == null || !Sha256Hex().IsMatch(stored.Sha256))
                throw new JsonException($"Token {stored.Id} has a malformed sha256.");
            if (stored.Created == null)
                throw new JsonException($"Token {stored.Id} has no creation time.");

            var scopes = TokenScopes.None;
            foreach (var name in stored.Scopes ?? [])
                scopes |= ParseScope(name) ?? throw new JsonException($"Token {stored.Id} has the unknown scope '{name}'.");
            if (!scopes.HasFlag(TokenScopes.Read))
                throw new JsonException($"Token {stored.Id} has no read scope.");

            records.Add(new TokenRecord(stored.Id, stored.Name, scopes, ParseTime(stored.Created),
                stored.LastUsed is { } used ? ParseTime(used) : null, Convert.FromHexString(stored.Sha256)));
        }

        return [.. records];
    }

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
        if (!Directory.Exists(folder))
            return;

        var prefix = Path.GetFileName(path) + ".";
        foreach (var temp in Directory.EnumerateFiles(folder, prefix + "*" + AtomicFile.TempSuffix))
        {
            var name = Path.GetFileName(temp);
            if (!TempPart().IsMatch(name[(prefix.Length - 1)..]))
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
