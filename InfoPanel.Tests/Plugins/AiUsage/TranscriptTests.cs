using System.Text;
using InfoPanel.AiUsage;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// Claude Code's tokens and cost from its transcripts, on hand-written fixtures in a home of their own.
public sealed class TranscriptTests : IDisposable
{
    private readonly FixtureHome _home = new();
    private static DateTimeOffset Now => Fixtures.Now;

    public void Dispose() => _home.Dispose();

    private TranscriptScanner Scanner() => new(_home.Environment);

    private IReadOnlyList<ClaudeFolder> Found() => Folders.FindClaude(_home.Environment);

    private TokenReading Combined(TranscriptScanner scanner) => scanner.Read(Found()).Combined;

    /// <summary>What <paramref name="input"/> … <paramref name="output"/> tokens of Opus 5.5 cost at its list prices ($4 in, $20 out, $0.20 a cache read).</summary>
    private static double Opus55(long input, long write5m, long write1h, long read, long output) =>
        (input * 4 + write5m * 4 * 1.25 + write1h * 4 * 2.0 + read * 0.20 + output * 20) / 1_000_000;

    [Fact]
    public void One_message_written_on_several_lines_counts_once()
    {
        var claude = _home.ClaudeFolder(".claude");
        // Claude Code writes a message once per content block, each line with the same ids and usage.
        var line = Fixtures.Assistant(Now.AddMinutes(-30), "a", input: 100, read: 1000, output: 50);
        _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.User(Now.AddMinutes(-31)), line, line, line]);

        var tokens = Combined(Scanner());

        Assert.Equal(1150, tokens.TokensToday);
        Assert.Equal(1150, tokens.Tokens7d);
        Assert.Null(tokens.Note);
    }

    [Fact]
    public void A_resumed_sessions_repeat_in_another_file_counts_once()
    {
        var claude = _home.ClaudeFolder(".claude");
        var first = Fixtures.Assistant(Now.AddMinutes(-60), "a", input: 100, output: 50);
        var second = Fixtures.Assistant(Now.AddMinutes(-20), "b", input: 10, output: 5);
        _home.Transcript(claude, "-fixture-project/session-1.jsonl", [first]);
        // A resumed session's file starts with the messages before it.
        _home.Transcript(claude, "-fixture-project/session-2.jsonl", [first, second]);

        Assert.Equal(165, Combined(Scanner()).TokensToday);
    }

    [Fact]
    public void Subagents_transcripts_count_too()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-10), "main", input: 100, output: 0)]);
        _home.Transcript(claude, "-fixture-project/session/subagents/agent-1.jsonl", [Fixtures.Assistant(Now.AddMinutes(-9), "agent", model: "claude-sonnet-5-5", input: 20, output: 0)]);

        Assert.Equal(120, Combined(Scanner()).TokensToday);
    }

    [Fact]
    public void A_line_still_being_written_counts_once_it_ends()
    {
        var claude = _home.ClaudeFolder(".claude");
        var path = _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-10), "a", input: 100, output: 0)]);
        var line = Fixtures.Assistant(Now.AddMinutes(-1), "b", input: 7, output: 3) + "\n";
        var half = line.Length / 2;
        _home.Append(path, line[..half]);
        var scanner = Scanner();

        Assert.Equal(100, Combined(scanner).TokensToday);
        _home.Clock.Now = _home.Clock.Now.AddSeconds(15);
        _home.Append(path, line[half..]);
        Assert.Equal(110, Combined(scanner).TokensToday);
        // Read again later, nothing counts twice.
        _home.Clock.Now = _home.Clock.Now.AddSeconds(15);
        _home.Append(path, Fixtures.Assistant(Now.AddMinutes(-1), "b", input: 7, output: 3) + "\n");
        Assert.Equal(110, Combined(scanner).TokensToday);
    }

    [Fact]
    public void Today_starts_at_local_midnight_and_7_days_at_now_less_a_week()
    {
        var claude = _home.ClaudeFolder(".claude");
        var midnight = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(8));
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now - TimeSpan.FromDays(7) - TimeSpan.FromMinutes(2), "older", input: 1000, output: 0), // before the 7 days
            Fixtures.Assistant(Now - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(2), "week", input: 100, output: 0),
            Fixtures.Assistant(midnight.AddMinutes(-1), "yesterday", input: 10, output: 0),
            Fixtures.Assistant(midnight.AddMinutes(1), "today", input: 1, output: 0),
        ]);

        var tokens = Combined(Scanner());

        Assert.Equal(1, tokens.TokensToday);
        Assert.Equal(111, tokens.Tokens7d);
    }

    [Fact]
    public void Five_minute_and_one_hour_cache_writes_and_reads_are_priced_apart()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
            [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, write5m: 2000, write1h: 3000, read: 10000, output: 500)]);

        var tokens = Combined(Scanner());

        Assert.Equal(16500, tokens.TokensToday);
        Assert.Equal(Opus55(1000, 2000, 3000, 10000, 500), tokens.CostToday!.Value, 9);
        Assert.Equal(0.05, tokens.Cost7d!.Value, 9);
    }

    [Fact]
    public void Fast_mode_costs_the_models_fast_prices()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now.AddMinutes(-5), "standard", input: 1000, output: 500),
            Fixtures.Assistant(Now.AddMinutes(-4), "fast", input: 1000, output: 500, speed: "fast"),
        ]);

        var tokens = Combined(Scanner());

        Assert.Equal(Opus55(1000, 0, 0, 0, 500) * 3, tokens.CostToday!.Value, 9);
    }

    [Fact]
    public void An_unknown_model_counts_in_tokens_and_is_left_out_of_the_cost_by_name()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now.AddMinutes(-5), "known", input: 1000, output: 500),
            Fixtures.Assistant(Now.AddMinutes(-4), "unknown", model: "claude-future-9", input: 1000, output: 500),
        ]);

        var tokens = Combined(Scanner());

        Assert.Equal(3000, tokens.TokensToday);
        Assert.Equal(Opus55(1000, 0, 0, 0, 500), tokens.CostToday!.Value, 9);
        Assert.Equal("the cost leaves out claude-future-9: no price known", tokens.Note);
    }

    [Fact]
    public void Claude_Codes_synthetic_messages_count_for_nothing_and_name_no_model()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 10, output: 5),
            Fixtures.SyntheticAssistant(Now.AddMinutes(-4), "00000000-0000-4000-8000-0000000000c1"),
        ]);

        var tokens = Combined(Scanner());

        Assert.Equal(15, tokens.TokensToday);
        Assert.Null(tokens.Note);
    }

    [Fact]
    public void A_20_MB_line_is_read_through_in_bounded_memory()
    {
        var claude = _home.ClaudeFolder(".claude");
        var big = Fixtures.Assistant(Now.AddMinutes(-5), "big", input: 1, output: 2, text: new string('x', 20 * 1024 * 1024));
        _home.Transcript(claude, "-fixture-project/session.jsonl", [big, Fixtures.Assistant(Now.AddMinutes(-4), "after", input: 10, output: 0)]);
        var scanner = Scanner();
        var folders = Found();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var tokens = scanner.Read(folders).Combined;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(13, tokens.TokensToday);
        Assert.True(_home.Files.BytesRead > 20 * 1024 * 1024);
        Assert.True(allocated < 2 * 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void Assistant_lines_without_usage_read_format_changed()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 10, output: 5),
            Fixtures.AssistantWithoutUsage(Now.AddMinutes(-4), "b"),
        ]);

        var tokens = Combined(Scanner());

        Assert.Null(tokens.TokensToday);
        Assert.Null(tokens.CostToday);
        Assert.Equal("Claude Code's transcript format changed (message.usage missing); InfoPanel may need an update", tokens.Note);
    }

    [Fact]
    public void A_file_last_written_30_days_ago_is_never_opened()
    {
        var claude = _home.ClaudeFolder(".claude");
        var old = _home.Transcript(claude, "-fixture-project/old.jsonl", [Fixtures.Assistant(Now.AddDays(-30), "old", input: 1000, output: 0)], Now.AddDays(-30));
        _home.Transcript(claude, "-fixture-project/old/subagents/agent-1.jsonl", [Fixtures.Assistant(Now.AddDays(-30), "old-agent", input: 1000, output: 0)], Now.AddDays(-30));
        _home.Transcript(claude, "-fixture-project/new.jsonl", [Fixtures.Assistant(Now.AddMinutes(-1), "new", input: 10, output: 0)]);
        var opened = new List<string>();
        _home.Files.BeforeOpen = opened.Add;

        var tokens = Combined(Scanner());

        Assert.Equal(10, tokens.Tokens7d);
        Assert.Equal([Path.Combine(claude, "projects", "-fixture-project", "new.jsonl")], opened.Distinct());
        Assert.DoesNotContain(old, opened);
        Assert.Equal(1, _home.Files.Stats);
    }

    [Fact]
    public void The_plugin_shows_tokens_and_cost_per_folder_and_combined_and_reads_them_again_every_15_seconds()
    {
        var a = _home.ClaudeFolder(".claude");
        var b = _home.ClaudeFolder(".claude-work");
        var path = _home.Transcript(a, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, output: 500)]);
        _home.Transcript(b, "-other/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "b", input: 2000, output: 0)]);
        var plugin = new AiUsagePlugin(_home.Environment);
        plugin.Initialize();
        plugin.Load([]);

        plugin.Update();

        Assert.Equal(1500, plugin.ClaudeFolderEntries["profile-.claude"].TokensToday.Value);
        Assert.Equal(2000, plugin.ClaudeFolderEntries["profile-.claude-work"].Tokens7d.Value);
        Assert.Equal((float)Opus55(1000, 0, 0, 0, 500), plugin.ClaudeFolderEntries["profile-.claude"].CostToday.Value, 6);
        Assert.Equal(3500, plugin.Claude.TokensToday.Value);
        Assert.Equal(3500, plugin.Claude.Tokens7d.Value);
        Assert.Equal((float)Opus55(3000, 0, 0, 0, 500), plugin.Claude.Cost7d.Value, 6);
        // The limits' status as it was: tokens with nothing to say add nothing to it.
        Assert.Equal(LimitsReader.NotConnected, plugin.Claude.Status.Value);

        _home.Append(path, Fixtures.Assistant(Now.AddMinutes(-1), "c", input: 100, output: 0) + "\n");
        _home.Clock.Now = _home.Clock.Now.AddSeconds(5);
        plugin.Update();
        Assert.Equal(3500, plugin.Claude.TokensToday.Value);
        _home.Clock.Now = _home.Clock.Now.AddSeconds(10);
        plugin.Update();
        Assert.Equal(3600, plugin.Claude.TokensToday.Value);
        Assert.Equal(1600, plugin.ClaudeFolderEntries["profile-.claude"].TokensToday.Value);
    }

    [Fact]
    public void Each_folder_counts_its_own_and_the_combined_set_sums_them()
    {
        var a = _home.ClaudeFolder(".claude");
        var b = _home.ClaudeFolder(".claude-work");
        _home.Transcript(a, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, output: 500)]);
        _home.Transcript(b, "-other/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "b", input: 2000, output: 0)]);

        var reading = Scanner().Read(Found());

        Assert.Equal(1500, reading.Folders[a].TokensToday);
        Assert.Equal(2000, reading.Folders[b].TokensToday);
        Assert.Equal(3500, reading.Combined.TokensToday);
        Assert.Equal(Opus55(3000, 0, 0, 0, 500), reading.Combined.Cost7d!.Value, 9);
    }

    [Fact]
    public void The_reader_takes_only_usage_fields_whatever_the_order_nesting_or_chunks()
    {
        var lines = new StringBuilder();
        lines.Append(Fixtures.User(Now)).Append('\n');
        // Keys in another order, text holding braces, quotes and escapes, a nested "usage" elsewhere.
        lines.Append("""{"type":"assistant","timestamp":"2026-11-10T01:00:00.000Z","toolUseResult":{"usage":{"input_tokens":5}},"message":{"content":[{"type":"text","text":"a } ] \" \\ { [ é"}],"usage":{"output_tokens":2,"input_tokens":3,"cache_read_input_tokens":4,"cache_creation_input_tokens":6,"cache_creation":{"ephemeral_1h_input_tokens":6}},"model":"claude-opus-5-5","id":"msg_x"},"requestId":null}""").Append('\n');
        lines.Append("not json\n[1,2]\n");
        var bytes = Encoding.UTF8.GetBytes(lines.ToString());

        foreach (var size in new[] { 1, 7, 64, bytes.Length })
        {
            var found = new List<UsageLine>();
            var reader = new UsageLineReader();
            for (var at = 0; at < bytes.Length; at += size)
                reader.Feed(bytes.AsSpan(at, Math.Min(size, bytes.Length - at)), found.Add);
            var line = Assert.Single(found);
            Assert.Equal(new UsageLine("2026-11-10T01:00:00.000Z", "msg_x", null, "claude-opus-5-5", null, 3, 6, 4, 2, null, 6, null), line);
            Assert.True(reader.AtLineStart);
        }
    }

    [Fact]
    public void No_message_text_is_kept_once_a_file_is_read()
    {
        var claude = _home.ClaudeFolder(".claude");
        const string marker = "fixture-marker-7f3a";
        var user = $$"""{"parentUuid":null,"type":"user","message":{"role":"user","content":"please look at {{marker}}"},"cwd":"/fixture/{{marker}}","timestamp":"{{Fixtures.Iso(Now.AddMinutes(-6))}}"}""";
        _home.Transcript(claude, "-fixture-project/session.jsonl", [user, Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 10, output: 5)]);
        var scanner = Scanner();

        Assert.Equal(15, Combined(scanner).TokensToday);

        // Every field the scanner keeps, but its fixed read buffer, holds no trace of the text.
        var found = Retained.Find(scanner, marker, skip: ["_buffer"]);
        Assert.True(found == null, $"the text is kept in {found}");
    }

    [Fact]
    public void A_file_rewritten_with_other_first_bytes_is_read_again_from_its_start()
    {
        var claude = _home.ClaudeFolder(".claude");
        var path = _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 100, output: 0)]);
        var scanner = Scanner();
        Assert.Equal(100, Combined(scanner).TokensToday);

        // Rewritten (the same file, longer): its first line as long as before but another message.
        _home.Clock.Now = _home.Clock.Now.AddSeconds(15);
        _home.Transcript(claude, "-fixture-project/session.jsonl",
            [Fixtures.Assistant(Now.AddMinutes(-5), "b", input: 100, output: 0), Fixtures.Assistant(Now.AddMinutes(-4), "c", input: 1, output: 0)]);

        Assert.Equal(201, Combined(scanner).TokensToday);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_listing_that_takes_the_whole_rounds_time_still_gets_the_tokens_read_in_a_few_rounds()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 10, output: 5)]);
        var folders = Found();
        var scanner = new TranscriptScanner(_home.Environment, budget: TranscriptBudget.Default with { TimePerRound = TimeSpan.FromSeconds(1) });
        // Each entry a listing looks at takes longer than a whole round.
        _home.Files.OnEntry = _ => _home.Clock.Now = _home.Clock.Now.AddSeconds(2);

        long? tokens = null;
        for (var round = 0; round < 10 && tokens == null; round++)
            tokens = scanner.Read(folders).Combined.TokensToday;

        Assert.Equal(15, tokens);
    }

    [Fact]
    public void Two_folders_holding_the_same_transcripts_each_count_them_and_the_combined_set_once()
    {
        var a = _home.ClaudeFolder(".claude");
        var b = _home.ClaudeFolder(".claude-copy");
        var line = Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, output: 500);
        _home.Transcript(a, "-fixture-project/session.jsonl", [line]);
        _home.Transcript(b, "-fixture-project/session.jsonl", [line]);

        var reading = Scanner().Read(Found());

        Assert.Equal(1500, reading.Folders[a].TokensToday);
        Assert.Equal(1500, reading.Folders[b].TokensToday);
        Assert.Equal(1500, reading.Combined.TokensToday);
    }

    [Fact]
    public void A_folder_swapped_for_another_holding_the_same_transcripts_keeps_the_totals()
    {
        var a = _home.ClaudeFolder(".claude");
        var b = _home.ClaudeFolder(".claude-copy");
        var line = Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, output: 500);
        _home.Transcript(a, "-fixture-project/session.jsonl", [line]);
        _home.Transcript(b, "-fixture-project/session.jsonl", [line]);
        var scanner = Scanner();

        Assert.Equal(1500, scanner.Read(Found().Where(f => f.Path == a).ToList()).Combined.TokensToday);
        var reading = scanner.Read(Found().Where(f => f.Path == b).ToList());

        Assert.Equal(1500, reading.Folders[b].TokensToday);
        Assert.Equal(1500, reading.Combined.TokensToday);
    }

    [Fact]
    public void A_message_refused_while_the_messages_kept_are_full_counts_once_old_ones_are_forgotten()
    {
        var claude = _home.ClaudeFolder(".claude");
        var path = _home.Transcript(claude, "-fixture-project/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 10, output: 5)]);
        var scanner = new TranscriptScanner(_home.Environment, budget: TranscriptBudget.Default with { Messages = 1 });
        Assert.Equal(15, Combined(scanner).TokensToday);

        _home.Clock.Now = _home.Clock.Now.AddDays(9);
        _home.Append(path, Fixtures.Assistant(_home.Clock.Now.AddMinutes(-1), "b", input: 7, output: 3) + "\n");
        var tokens = Combined(scanner);

        Assert.Equal(10, tokens.TokensToday);
        Assert.Null(tokens.Note);
    }

    [Fact]
    public void An_unlisted_model_version_has_no_price_and_a_dated_listed_one_has_its_models()
    {
        var claude = _home.ClaudeFolder(".claude");
        _home.Transcript(claude, "-fixture-project/session.jsonl",
        [
            Fixtures.Assistant(Now.AddMinutes(-5), "dated", model: "claude-opus-5-5-20261101", input: 1000, output: 500),
            Fixtures.Assistant(Now.AddMinutes(-4), "unlisted", model: "claude-opus-5-6", input: 1000, output: 500),
        ]);

        var tokens = Combined(Scanner());

        Assert.Equal(3000, tokens.TokensToday);
        Assert.Equal(Opus55(1000, 0, 0, 0, 500), tokens.CostToday!.Value, 9);
        Assert.Equal("the cost leaves out claude-opus-5-6: no price known", tokens.Note);
        Assert.Null(Prices.Default.Of("claude-opus-5-6"));
        Assert.Null(Prices.Default.Of("claude-opus-5-5-1"));
        Assert.Equal("claude-opus-5-5", Prices.Default.Of("claude-opus-5-5[1m]")?.Model);
    }

    [Fact]
    public void A_recent_file_that_may_not_be_read_makes_its_folders_tokens_unavailable()
    {
        var a = _home.ClaudeFolder(".claude");
        var b = _home.ClaudeFolder(".claude-work");
        var locked = _home.Transcript(a, "-fixture-project/locked.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "a", input: 1000, output: 500)]);
        _home.Transcript(b, "-other/session.jsonl", [Fixtures.Assistant(Now.AddMinutes(-5), "b", input: 2000, output: 0)]);
        _home.Files.Unreadable = path => path == locked;

        var reading = Scanner().Read(Found());

        Assert.Null(reading.Folders[a].TokensToday);
        Assert.Equal("a Claude Code transcript may not be read; tokens are left out", reading.Folders[a].Note);
        Assert.Equal(2000, reading.Folders[b].TokensToday);
        Assert.Null(reading.Combined.TokensToday);
        Assert.Equal("a Claude Code transcript may not be read; tokens are left out", reading.Combined.Note);
    }
}

/// <summary>Finds a text in what an object keeps: its fields, and theirs, through collections, strings and bytes (UTF-8).</summary>
internal static class Retained
{
    /// <summary>The path of the first field holding <paramref name="text"/>; null when none does. Fields named in <paramref name="skip"/> aren't looked in.</summary>
    public static string? Find(object root, string text, IReadOnlyCollection<string> skip)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Look(root, "scanner", 0);

        string? Look(object? value, string where, int depth)
        {
            if (value == null || depth > 12)
                return null;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is DateTimeOffset or DateTime or TimeSpan or decimal or Type or Delegate)
                return null;
            if (!type.IsValueType && !seen.Add(value))
                return null;
            switch (value)
            {
                case string s:
                    return s.Contains(text, StringComparison.Ordinal) ? where : null;
                case byte[] b:
                    return b.AsSpan().IndexOf(bytes) >= 0 ? where : null;
                case char[] c:
                    return new string(c).Contains(text, StringComparison.Ordinal) ? where : null;
                case List<byte> list:
                    return list.ToArray().AsSpan().IndexOf(bytes) >= 0 ? where : null;
                case System.Collections.IEnumerable items:
                    var i = 0;
                    foreach (var item in items)
                    {
                        if (Look(item, $"{where}[{i++}]", depth + 1) is { } found)
                            return found;
                    }
                    break;
            }
            if (type.Assembly != typeof(TranscriptScanner).Assembly && !type.IsGenericType && !(type.Namespace ?? "").StartsWith("System", StringComparison.Ordinal))
                return null;
            if (value is System.Collections.IEnumerable && type.Assembly != typeof(TranscriptScanner).Assembly)
                return null;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (skip.Contains(field.Name) || field.FieldType == typeof(UsageEnvironment) || field.Name.Contains("env", StringComparison.Ordinal))
                        continue;
                    if (Look(field.GetValue(value), $"{where}.{field.Name}", depth + 1) is { } found)
                        return found;
                }
            }
            return null;
        }
    }
}
