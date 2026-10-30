using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace InfoPanel.Images;

/// <summary>
/// The bytes media providers fetched, in memory only (nothing is written to disk or the blob store), by a key of their
/// own, <c>fetched:sha256:{64 hex}</c>, which no asset id can be, so the media cache decodes them like assets while a
/// document's asset reference never reaches them. Bounded by bytes, the least recently used dropped first; a provider
/// whose bytes were dropped fetches them again.
/// </summary>
public sealed class FetchedMediaStore(long maxBytes = FetchedMediaStore.DefaultMaxBytes) : IMediaAssetStore
{
    /// <summary>The store's bound, 64 MiB (spec/docs/limits.md, process budgets).</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>What every key starts with.</summary>
    public const string Prefix = "fetched:";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[] Bytes)> _lru = new();
    private long _bytes;

    /// <summary>The bytes held.</summary>
    public long Bytes
    {
        get
        {
            lock (_lock) return _bytes;
        }
    }

    /// <summary>The key <paramref name="bytes"/> are kept under: their SHA-256.</summary>
    public static string KeyOf(ReadOnlySpan<byte> bytes) => Prefix + "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Keeps <paramref name="bytes"/> (not copied: the caller hands them over) and answers their key.</summary>
    public string Put(byte[] bytes)
    {
        var key = KeyOf(bytes);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return key;
            }
            _entries[key] = _lru.AddFirst((key, bytes));
            _bytes += bytes.Length;
            while (_bytes > maxBytes && _lru.Last is { } last && last != _lru.First)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes.Length;
            }
        }
        return key;
    }

    /// <summary>Whether <paramref name="key"/>'s bytes are still held.</summary>
    public bool Contains(string key)
    {
        lock (_lock) return _entries.ContainsKey(key);
    }

    public byte[]? Read(string hash)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(hash, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bytes;
        }
    }
}

/// <summary>
/// The media cache's store in the engine: fetched media by its <see cref="FetchedMediaStore.Prefix"/> keys, every other
/// key from the stored assets, each of its reads (a size, a video's stream read in place) from that store too.
/// </summary>
public sealed class EngineMediaStore(IMediaAssetStore assets, FetchedMediaStore fetched) : IMediaAssetStore
{
    public byte[]? Read(string hash) => StoreOf(hash).Read(hash);

    public long? Size(string hash) => StoreOf(hash).Size(hash);

    public System.IO.Stream? Open(string hash) => StoreOf(hash).Open(hash);

    private IMediaAssetStore StoreOf(string hash) =>
        hash.StartsWith(FetchedMediaStore.Prefix, StringComparison.Ordinal) ? fetched : assets;
}
