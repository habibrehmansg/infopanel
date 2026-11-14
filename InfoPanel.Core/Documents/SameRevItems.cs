using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Documents;

/// <summary>
/// A commit result's <c>items</c>, every one at the result's revision, as the results kept for <c>opId</c> retries
/// (<see cref="OpLog"/>) hold them: an array of the ids (the document's own id strings, shared) and that one revision,
/// in place of a dictionary. Enumerates in the ids' order, as the dictionary did, so it is written as the same JSON.
/// A lookup searches the ids; a caller looking up many builds its own set.
/// </summary>
internal sealed class SameRevItems(string[] ids, long rev) : IReadOnlyDictionary<string, long>
{
    public long this[string key] => ContainsKey(key) ? rev : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => ids;

    public IEnumerable<long> Values => Enumerable.Repeat(rev, ids.Length);

    public int Count => ids.Length;

    public bool ContainsKey(string key) => Array.IndexOf(ids, key) >= 0;

    public bool TryGetValue(string key, out long value)
    {
        var found = ContainsKey(key);
        value = found ? rev : 0;
        return found;
    }

    public IEnumerator<KeyValuePair<string, long>> GetEnumerator()
    {
        foreach (var id in ids) yield return new(id, rev);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
