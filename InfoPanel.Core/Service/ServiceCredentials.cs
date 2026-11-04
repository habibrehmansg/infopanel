using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Serilog;

namespace InfoPanel.Service;

/// <summary>The state of the credential a client provides for the configured service.</summary>
public enum ServiceCredentialState
{
    /// <summary>The engine keeps none.</summary>
    Absent,

    /// <summary>The engine keeps one and sends it with its requests to the service.</summary>
    Present,

    /// <summary>The engine keeps one the service refused (401), and sends no request until a client provides another.</summary>
    Rejected,
}

/// <summary>The configured service's address in effect, the credential's state, where it is kept and a fingerprint of the stored credential.</summary>
public sealed record ServiceStatus(string? Address, ServiceCredentialState Credential, string? Store, string? Fingerprint);

/// <summary>A credential was provided while the engine has no service address: it keeps none (the API's 409 <c>noService</c>).</summary>
public sealed class NoServiceException() : InvalidOperationException("The engine has no service address, so it keeps no credential.");

/// <summary>
/// The credential a client provides for the configured service (spec/docs/api.md, "The configured service"): kept in one
/// store (<see cref="IServiceCredentialStore"/>), never in the settings, never logged, never answered; the API reads its
/// state and a fingerprint of it (<see cref="Status"/>). It belongs to the address in effect when it was provided: a change
/// of address removes it before the change is stored, and is refused when it can't (<see cref="AddressChanging"/>), so no
/// record outlives its address; at start-up, one kept for another address than the one in effect (a change made while the
/// engine was stopped, a build's default included) is removed. A 401 from the service (<see cref="Reject"/>) marks it
/// rejected, kept beside it so it lasts across restarts, until a client provides another; a 401 to a request sent with an
/// earlier credential, the same text provided again included, changes nothing. What the engine keeps follows what the
/// store holds: a write or removal that reached the file but couldn't be made durable is in effect.
/// <see cref="ServiceClient"/> sends it; nothing else reads it.
/// </summary>
public sealed class ServiceCredentials
{
    /// <summary>The longest credential the engine keeps, in characters (ASCII, so bytes).</summary>
    public const int MaxLength = 4096;

    private readonly IServiceCredentialStore _store;
    private readonly Func<string?> _address;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    /// <summary>The record the store holds, as last read or written; under <see cref="_lock"/>.</summary>
    private StoredServiceCredential? _held;

    /// <summary>
    /// Under <see cref="_lock"/>: which credential <see cref="_held"/> is, a new one each time it is provided or dropped. A
    /// request carries the one it was sent with, so a late 401 rejects only that one.
    /// </summary>
    private long _generation;

    /// <summary>
    /// Under <see cref="_lock"/>: the store may hold a record the engine doesn't keep (one for another address it couldn't
    /// remove, one it couldn't read, a removal not yet durable). It is removed before any change of address, which is
    /// refused when that fails, and replaced by the next credential provided.
    /// </summary>
    private bool _stale;

    /// <param name="store">Where the credential is kept.</param>
    /// <param name="address">The service address in effect, normalised (<see cref="ServiceAddress"/>), or null without one.</param>
    public ServiceCredentials(IServiceCredentialStore store, Func<string?> address, ILogger? logger = null)
    {
        _store = store;
        _address = address;
        _logger = logger ?? Log.ForContext<ServiceCredentials>();
        lock (_lock)
        {
            try
            {
                _held = store.Read();
            }
            catch (ServiceStoreException ex)
            {
                // Never the credential's text: the store's reason only.
                _logger.Warning("The stored service credential couldn't be read ({Reason}); the engine keeps none until a client provides one", ex.Message);
                _held = null;
                _stale = true;
            }

            DropIfAnotherAddress(_address());
        }
    }

    /// <summary>The store's name (<c>private</c>, <c>protected</c>).</summary>
    public string StoreName => _store.Name;

    /// <summary>The service address in effect, or null.</summary>
    public string? Address => _address();

    /// <summary>The address in effect, the credential's state, the store holding it and its fingerprint; never the credential.</summary>
    public ServiceStatus Status
    {
        get
        {
            lock (_lock)
            {
                var address = _address();
                if (_held is not { } held || held.Address != address)
                    return new ServiceStatus(address, ServiceCredentialState.Absent, null, null);
                return new ServiceStatus(address, held.Rejected ? ServiceCredentialState.Rejected : ServiceCredentialState.Present, _store.Name, Fingerprint(held.Credential));
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="credential"/> is one the engine keeps: an RFC 6750 token (letters, digits, <c>-._~+/</c>,
    /// then any <c>=</c>), 1 to <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool IsWellFormed(string? credential)
    {
        if (credential is null || credential.Length is 0 or > MaxLength)
            return false;
        var end = credential.Length;
        while (end > 0 && credential[end - 1] == '=')
            end--;
        if (end == 0)
            return false;
        for (var i = 0; i < end; i++)
        {
            if (credential[i] is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~' or '+' or '/'))
                return false;
        }

        return true;
    }

    /// <summary>A fingerprint of a credential: the first 16 hexadecimal digits, in lower case, of the SHA-256 of its UTF-8 bytes.</summary>
    public static string Fingerprint(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];

    /// <summary>Keeps <paramref name="credential"/> for the address in effect, in place of any other; its state is then present.</summary>
    /// <exception cref="ArgumentException">It isn't well formed (<see cref="IsWellFormed"/>).</exception>
    /// <exception cref="NoServiceException">There is no service address: nothing changed.</exception>
    /// <exception cref="ServiceStoreException">
    /// The store refused: the credential kept before stays; or it holds the new one but couldn't make that durable
    /// (<see cref="ServiceStoreException.Applied"/>): the new one is in effect, as a restart would find it.
    /// </exception>
    public void Put(string credential)
    {
        if (!IsWellFormed(credential))
            throw new ArgumentException("The credential isn't an RFC 6750 token of at most 4,096 characters.", nameof(credential));
        lock (_lock)
        {
            var address = _address() ?? throw new NoServiceException();
            var record = new StoredServiceCredential(address, credential, Rejected: false);
            try
            {
                _store.Write(record);
            }
            catch (ServiceStoreException ex) when (ex.Applied)
            {
                Keep(record);
                throw;
            }

            Keep(record);
        }

        _logger.Information("A client provided a credential for the configured service");
    }

    /// <summary>Removes the credential, if any.</summary>
    /// <exception cref="ServiceStoreException">
    /// The store couldn't remove it: it is kept as it was; or it removed it but couldn't make that durable
    /// (<see cref="ServiceStoreException.Applied"/>): none is kept, and the next removal finishes it.
    /// </exception>
    public void Delete()
    {
        lock (_lock)
        {
            var had = _held is not null;
            Remove();
            if (had)
                _logger.Information("Removed the credential for the configured service, as a client asked");
        }
    }

    /// <summary>
    /// The address in effect is about to become <paramref name="next"/> (a settings change, before it is stored, under the
    /// settings' lock). When it changes, a credential kept for it, or a record the store may still hold, is removed first;
    /// when that fails this throws and the change must be refused, so no record outlives its address. The scope returned
    /// holds this object's lock until it is disposed, once the change is in effect or has failed, so no credential is
    /// provided for the old address meanwhile. Asked only for a change of the address in effect: a change that keeps it
    /// mustn't wait for this object's lock, which a store's write holds.
    /// </summary>
    /// <exception cref="ServiceStoreException">The store couldn't remove it, or couldn't make the removal durable.</exception>
    public IDisposable AddressChanging(string? next)
    {
        _lock.Enter();
        try
        {
            if (next != _address() && (_held is not null || _stale))
            {
                Remove();
                _logger.Information("The service address changes; removed the credential provided for the previous one");
            }

            return new Held(_lock);
        }
        catch
        {
            _lock.Exit();
            throw;
        }
    }

    /// <summary>
    /// The address in effect may have changed (a settings change): a credential provided for another address is removed.
    /// <see cref="AddressChanging"/> has already removed it when the settings change went through it.
    /// </summary>
    public void AddressChanged()
    {
        lock (_lock)
            DropIfAnotherAddress(_address());
    }

    /// <summary>
    /// The address in effect and the credential to send to it, when a request may be made: false, with no credential,
    /// when there is no address, none is kept, or it was rejected.
    /// </summary>
    internal bool TryTake(out string? address, out string? credential, out ServiceCredentialState state) =>
        TryTake(out address, out credential, out state, out _);

    /// <summary>
    /// As <see cref="TryTake(out string?, out string?, out ServiceCredentialState)"/>, with the credential's
    /// <paramref name="generation"/>, which a 401 to the request names (<see cref="Reject"/>). The address is read once:
    /// the credential is checked against that address and is for it alone, whatever the settings publish meanwhile.
    /// </summary>
    internal bool TryTake(out string? address, out string? credential, out ServiceCredentialState state, out long generation)
    {
        lock (_lock)
        {
            address = _address();
            DropIfAnotherAddress(address);
            generation = _generation;
            state = _held is null ? ServiceCredentialState.Absent : _held.Rejected ? ServiceCredentialState.Rejected : ServiceCredentialState.Present;
            credential = state == ServiceCredentialState.Present ? _held!.Credential : null;
            return address is not null && credential is not null;
        }
    }

    /// <summary>
    /// The service refused the credential of <paramref name="generation"/> (401): marked rejected, kept so across restarts,
    /// unless it was replaced or removed since (provided again, even with the same text, or for another address). A store
    /// that can't keep the mark still has it in effect for this run.
    /// </summary>
    internal void Reject(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _held is not { Rejected: false } held)
                return;
            _held = held with { Rejected = true };
            try
            {
                _store.Write(_held);
            }
            catch (ServiceStoreException ex)
            {
                _logger.Warning("Couldn't keep the service credential's rejection across a restart ({Reason})", ex.Message);
            }
        }

        _logger.Warning("The configured service refused the credential (401); the engine sends it no request until a client provides another");
    }

    /// <summary>Under <see cref="_lock"/>: <paramref name="record"/> is what the store holds now, a new credential.</summary>
    private void Keep(StoredServiceCredential record)
    {
        _held = record;
        _generation++;
        _stale = false;
    }

    /// <summary>
    /// Under <see cref="_lock"/>: removes the record. A removal that reached the file but couldn't be made durable leaves
    /// none kept, the record stale, and throws; one that didn't leaves everything as it was and throws.
    /// </summary>
    private void Remove()
    {
        try
        {
            _store.Delete();
        }
        catch (ServiceStoreException ex) when (ex.Applied)
        {
            Forget(stale: true);
            throw;
        }

        Forget(stale: false);
    }

    /// <summary>Under <see cref="_lock"/>: no credential kept; <paramref name="stale"/>, the store may still hold one.</summary>
    private void Forget(bool stale)
    {
        _held = null;
        _generation++;
        _stale = stale;
    }

    /// <summary>Under <see cref="_lock"/>: removes a credential kept for an address other than <paramref name="address"/>.</summary>
    private void DropIfAnotherAddress(string? address)
    {
        if (_held is not { } held || held.Address == address)
            return;
        try
        {
            _store.Delete();
            Forget(stale: false);
            _logger.Information("The service address changed; removed the credential provided for the previous one");
        }
        catch (ServiceStoreException ex)
        {
            // Never sent all the same: it belongs to another address. A change of address is refused until it is removed,
            // and the next start removes it again.
            Forget(stale: true);
            _logger.Warning("The service address changed; couldn't remove the credential provided for the previous one ({Reason})", ex.Message);
        }
    }

    /// <summary>Holds <see cref="ServiceCredentials"/>' lock until disposed, once.</summary>
    private sealed class Held(Lock held) : IDisposable
    {
        private Lock? _held = held;

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Exit();
    }
}
