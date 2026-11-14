using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// A commit result's items as <see cref="SameRevItems"/> read and write exactly as the dictionary of the same ids at one
/// revision: enumeration order, counts, keys, values, lookups, misses, and the result's JSON.
/// </summary>
public sealed class SameRevItemsTests
{
    private const long Rev = 42;

    public static TheoryData<string[]> Ids => new()
    {
        Array.Empty<string>(),
        new[] { "a" },
        new[] { "zeta", "alpha", "m", "b2", "b10", "Alpha" },
    };

    private static (SameRevItems Items, Dictionary<string, long> Dictionary) Both(string[] ids)
    {
        var dictionary = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in ids) dictionary[id] = Rev;
        return (new SameRevItems([.. ids], Rev), dictionary);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void It_enumerates_counts_and_lists_as_the_dictionary(string[] ids)
    {
        var (items, dictionary) = Both(ids);
        Assert.Equal(dictionary.ToList(), items.ToList());
        Assert.Equal(dictionary.Count, items.Count);
        Assert.Equal(dictionary.Keys.ToList(), items.Keys.ToList());
        Assert.Equal(dictionary.Values.ToList(), items.Values.ToList());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void It_looks_up_hits_and_misses_as_the_dictionary(string[] ids)
    {
        var (items, dictionary) = Both(ids);
        foreach (var id in ids.Append("missing").Append("ALPHA"))
        {
            Assert.Equal(dictionary.ContainsKey(id), items.ContainsKey(id));
            Assert.Equal(dictionary.TryGetValue(id, out var expected), items.TryGetValue(id, out var actual));
            Assert.Equal(expected, actual);
            if (dictionary.ContainsKey(id)) Assert.Equal(dictionary[id], items[id]);
            else
            {
                Assert.Throws<KeyNotFoundException>(() => dictionary[id]);
                Assert.Throws<KeyNotFoundException>(() => items[id]);
                Assert.Equal(0, actual);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void A_result_holding_it_is_written_as_the_same_json(string[] ids)
    {
        var (items, dictionary) = Both(ids);
        CommitResult Result(IReadOnlyDictionary<string, long> i) => new()
        {
            Rev = Rev, Items = i, Removed = ["gone"], ProfileRev = Rev, Dirty = true, CanUndo = true, CanRedo = false,
        };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Result(dictionary), SpecJson.Options),
            JsonSerializer.SerializeToUtf8Bytes(Result(items), SpecJson.Options));
        Assert.Equal(SpecJson.Write(Result(dictionary)).GetRawText(), SpecJson.Write(Result(items)).GetRawText());
    }
}
