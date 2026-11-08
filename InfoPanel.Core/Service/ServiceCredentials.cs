using System;
using System.Collections.Generic;
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

/// <summary>
/// The credential <see cref="ServiceCredentials.Put"/> replaced: it was <c>present</c>, kept for <paramref name="Address"/>
/// (the address in effect then), and other text than the one provided in its place. Until the request asking the service
/// to forget it ends, it is pending; it is dropped, and that request with it, the moment it may no longer be sent: the
/// credential is provided again, the address changes, or the engine stops.
/// </summary>
public sealed record ReplacedServiceCredential(string Address, string Credential)
{
    private readonly CancellationTokenSource _dropped = new();
    private string? _reason;

    /// <summary>Cancelled once it is dropped.</summary>
    internal CancellationToken Dropped => _dropped.Token;

    /// <summary>Why it was dropped, or null while it may still be sent.</summary>
    internal string? Reason => Volatile.Read(ref _reason);

    /// <summary>
    /// Drops it for <paramref name="reason"/>, once: its request, if it is still being made, is cancelled (the callbacks
    /// run elsewhere, so a caller holding a lock doesn't run them).
    /// </summary>
    internal void Drop(string reason)
    {
        if (Interlocked.CompareExchange(ref _reason, reason, null) is null)
            _ = _dropped.CancelAsync();
    }

    /// <summary>The address and the credential: two are equal when both are.</summary>
    public bool Equals(ReplacedServiceCredential? other) => other is not null && Address == other.Address && Credential == other.Credential;

    public override int GetHashCode() => HashCode.Combine(Address, Credential);

    /// <summary>The address only: never the credential.</summary>
    public override string ToString() => $"{nameof(ReplacedServiceCredential)} {{ Address = {Address} }}";
}

/// <summary>What <see cref="ServiceCredentials.MaySend"/> says of a replaced credential, at the last moment before sending.</summary>
internal enum ReleaseCheck
{
    /// <summary>It may be sent.</summary>
    Send,

    /// <summary>The address it was kept for is no longer in effect: dropped.</summary>
    OtherAddress,

    /// <summary>Dropped: <see cref="ReplacedServiceCredential.Reason"/> says why.</summary>
    Dropped,
}

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

    /// <summary>
    /// Under <see cref="_lock"/>: the replaced credentials whose request to be forgotten hasn't ended, so a credential
    /// provided again, or a change of address, drops them (<see cref="ReplacedServiceCredential.Drop"/>). Tracked by
    /// instance, not by value: one ending removes only itself, never a later one of the same address and text.
    /// </summary>
    private readonly HashSet<ReplacedServiceCredential> _releases = new(ReferenceEqualityComparer.Instance);

    /// <summary>Why a replaced credential was dropped: a client provided it again.</summary>
    internal const string InUseAgain = "the credential is in use again";

    /// <summary>Why a replaced credential was dropped: the address it was kept for is no longer in effect.</summary>
    internal const string AddressMoved = "the address changed";

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

    /// <summary>
    /// Raised, outside this object's lock, after the credential or the address it is for may have changed: one provided,
    /// removed, rejected, or the address in effect changed. Whoever holds a connection made with the credential checks
    /// whether it is still the one to use (<see cref="TryTake(out string?, out string?, out ServiceCredentialState, out long)"/>).
    /// </summary>
    public event Action? Changed;

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
    /// The credential's state, as <see cref="Status"/> reads it, and its generation, read together; never the credential.
    /// What a reader learned from a request sent with that generation is about the credential kept while it stays the same.
    /// </summary>
    internal (ServiceCredentialState State, long Generation) StateAndGeneration
    {
        get
        {
            lock (_lock)
            {
                var state = _held is not { } held || held.Address != _address() ? ServiceCredentialState.Absent
                    : held.Rejected ? ServiceCredentialState.Rejected : ServiceCredentialState.Present;
                return (state, _generation);
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

    /// <summary>The longest id of a credential, in characters.</summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// Whether <paramref name="id"/> is an id the engine keeps beside a credential: 1 to <see cref="MaxIdLength"/> ASCII
    /// letters, digits, <c>_</c> and <c>-</c>.
    /// </summary>
    public static bool IsWellFormedId(string? id)
    {
        if (id is null || id.Length is 0 or > MaxIdLength)
            return false;
        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The id the service gave the credential the engine keeps, as the client provided it beside it (a pairing pass's
    /// <c>device</c> names it, spec/docs/api.md "Pairing through the configured service"): null when none was provided,
    /// or no credential is <c>present</c> for the address in effect (none, rejected, or kept for another address).
    /// </summary>
    public string? Id
    {
        get
        {
            lock (_lock)
                return _held is { Rejected: false } held && held.Address == _address() ? held.Id : null;
        }
    }

    /// <summary>
    /// The address in effect and which credential is kept (a new generation each time one is provided or dropped), read at
    /// once: a pairing pass's check holds to the service it began with, and refuses the pass when either changed.
    /// </summary>
    public (string? Address, long Generation) Binding
    {
        get
        {
            lock (_lock)
                return (_address(), _generation);
        }
    }

    /// <summary>A fingerprint of a credential: the first 16 hexadecimal digits, in lower case, of the SHA-256 of its UTF-8 bytes.</summary>
    public static string Fingerprint(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];

    /// <summary>
    /// Keeps <paramref name="credential"/> for the address in effect, in place of any other, with the id the service gave it
    /// (<paramref name="id"/>, or none); its state is then present. Answers the credential it replaced, when that one was
    /// <c>present</c> for the same address and other text (read under the same lock as the write, so two calls racing each
    /// get their own), else null; it is pending until <see cref="Released"/>. A credential provided that a pending one
    /// holds drops that one (<see cref="InUseAgain"/>).
    /// </summary>
    /// <exception cref="ArgumentException">It, or the id, isn't well formed (<see cref="IsWellFormed"/>, <see cref="IsWellFormedId"/>).</exception>
    /// <exception cref="NoServiceException">There is no service address: nothing changed.</exception>
    /// <exception cref="ServiceStoreException">
    /// The store refused: the credential kept before stays; or it holds the new one but couldn't make that durable
    /// (<see cref="ServiceStoreException.Applied"/>): the new one is in effect, as a restart would find it.
    /// </exception>
    public ReplacedServiceCredential? Put(string credential, string? id = null)
    {
        if (!IsWellFormed(credential))
            throw new ArgumentException("The credential isn't an RFC 6750 token of at most 4,096 characters.", nameof(credential));
        if (id is not null && !IsWellFormedId(id))
            throw new ArgumentException("The credential's id isn't 1 to 64 letters, digits, '_' and '-'.", nameof(id));
        using var raise = new RaiseIfChanged(this);
        ReplacedServiceCredential? replaced;
        lock (_lock)
        {
            var address = _address() ?? throw new NoServiceException();
            var record = new StoredServiceCredential(address, credential, Rejected: false, id);
            replaced = _held is { Rejected: false } held && held.Address == address && held.Credential != credential
                ? new ReplacedServiceCredential(address, held.Credential)
                : null;
            try
            {
                _store.Write(record);
            }
            catch (ServiceStoreException ex) when (ex.Applied)
            {
                Keep(record);
                DropReleases(r => r.Address == address && r.Credential == credential, InUseAgain);
                throw;
            }

            Keep(record);
            // A replaced credential provided again is in use: its pending request to be forgotten is dropped.
            DropReleases(r => r.Address == address && r.Credential == credential, InUseAgain);
            if (replaced is not null)
                _releases.Add(replaced);
        }

        _logger.Information("A client provided a credential for the configured service");
        return replaced;
    }

    /// <summary>Removes the credential, if any.</summary>
    /// <exception cref="ServiceStoreException">
    /// The store couldn't remove it: it is kept as it was; or it removed it but couldn't make that durable
    /// (<see cref="ServiceStoreException.Applied"/>): none is kept, and the next removal finishes it.
    /// </exception>
    public void Delete()
    {
        using var raise = new RaiseIfChanged(this);
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
        var before = _generation;
        try
        {
            if (next != _address())
                DropReleases(r => r.Address != next, AddressMoved);
            if (next != _address() && (_held is not null || _stale))
            {
                Remove();
                _logger.Information("The service address changes; removed the credential provided for the previous one");
            }

            return new Held(this, before);
        }
        catch
        {
            // A removal that reached the store but couldn't be made durable is in effect: raised as any other.
            var moved = _generation != before;
            _lock.Exit();
            if (moved)
                Changed?.Invoke();
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
        {
            var address = _address();
            DropReleases(r => r.Address != address, AddressMoved);
            DropIfAnotherAddress(address);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Whether <paramref name="replaced"/> may be sent now, read under the store's lock at the last moment before sending:
    /// only while the address it was kept for is in effect, it isn't the credential kept, and it wasn't dropped. One that
    /// may not is dropped. A request already sent can't be recalled; one still being made is cancelled by the drop.
    /// </summary>
    internal ReleaseCheck MaySend(ReplacedServiceCredential replaced)
    {
        lock (_lock)
        {
            if (_address() != replaced.Address)
            {
                replaced.Drop(AddressMoved);
                _releases.Remove(replaced);
                return ReleaseCheck.OtherAddress;
            }

            if (_held is { } held && held.Address == replaced.Address && held.Credential == replaced.Credential)
            {
                replaced.Drop(InUseAgain);
                _releases.Remove(replaced);
            }

            return replaced.Reason is null ? ReleaseCheck.Send : ReleaseCheck.Dropped;
        }
    }

    /// <summary>
    /// The request asking the service to forget <paramref name="replaced"/> has ended: that instance is no longer pending
    /// (another of the same address and text, replaced later, still is).
    /// </summary>
    internal void Released(ReplacedServiceCredential replaced)
    {
        lock (_lock)
            _releases.Remove(replaced);
    }

    /// <summary>Under <see cref="_lock"/>: drops, for <paramref name="reason"/>, every pending replaced credential <paramref name="match"/> takes.</summary>
    private void DropReleases(Predicate<ReplacedServiceCredential> match, string reason) =>
        _releases.RemoveWhere(r =>
        {
            if (!match(r))
                return false;
            r.Drop(reason);
            return true;
        });

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
        Changed?.Invoke();
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

    /// <summary>Raises <see cref="Changed"/> when disposed, if the credential's generation moved since it was made.</summary>
    private readonly struct RaiseIfChanged : IDisposable
    {
        private readonly ServiceCredentials _owner;
        private readonly long _before;

        public RaiseIfChanged(ServiceCredentials owner)
        {
            _owner = owner;
            lock (owner._lock)
                _before = owner._generation;
        }

        public void Dispose()
        {
            long now;
            lock (_owner._lock)
                now = _owner._generation;
            if (now != _before)
                _owner.Changed?.Invoke();
        }
    }

    /// <summary>
    /// Holds <see cref="ServiceCredentials"/>' lock until disposed, once; then, outside it, raises <see cref="Changed"/>
    /// when the credential was removed meanwhile, whether the settings change went through or failed after the removal
    /// (a failed write raises neither <see cref="AddressChanged"/> nor the settings' change), so a connection made with
    /// it closes either way.
    /// </summary>
    private sealed class Held(ServiceCredentials owner, long before) : IDisposable
    {
        private ServiceCredentials? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner)
                return;
            var moved = owner._generation != before;
            owner._lock.Exit();
            if (moved)
                owner.Changed?.Invoke();
        }
    }
}
