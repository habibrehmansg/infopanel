using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>
/// AI usage (<c>ai-usage</c>): how close Claude Code and Codex are to their five-hour and weekly limits, when those reset
/// and how old the numbers are, as readings (docs/2.0/ai-usage.md). Its readers are off until the user turns them on
/// (Read Claude Code, Read Codex); while both are off it opens no file. It reads only usage numbers, never message text,
/// and logs folder names and field paths, never a path below them or a value. Readings it can't give are NaN, which
/// InfoPanel shows as "–", with the set's <c>status</c> saying why.
/// </summary>
public sealed class AiUsagePlugin : BasePlugin, IPluginConfigurable
{
    public const string PluginId = "ai-usage";

    public const string ReadClaudeCode = "readClaudeCode";
    public const string ReadCodex = "readCodex";
    public const string OtherClaudeFolders = "otherClaudeFolders";
    public const string CodexFolder = "codexFolder";

    /// <summary>The longest folder setting kept.</summary>
    public const int MaxFolderSettingChars = 4096;

    private readonly UsageEnvironment _env;
    private readonly CodexReader _codex;
    private readonly LimitsReader _claude;
    private readonly CodexEntries _codexEntries = new();
    private readonly ClaudeEntries _claudeEntries = new();
    private readonly Dictionary<string, LimitEntries> _folderEntries = new(StringComparer.Ordinal);
    private readonly object _refreshLock = new();
    private readonly object _settingsLock = new();
    private IReadOnlyList<ClaudeFolder> _claudeFolders = [];
    private volatile Settings _settings = new(false, false, "", "");

    public AiUsagePlugin() : this(UsageEnvironment.Default())
    {
    }

    internal AiUsagePlugin(UsageEnvironment env)
        : base(PluginId, "AI usage", "Claude Code's and Codex's limits: how much of each five-hour and weekly window is used, when it resets and how old the number is.")
    {
        _env = env;
        _codex = new CodexReader(env);
        _claude = new LimitsReader(env);
    }

    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    /// <summary>The Claude folders found at load, each with a set of its own.</summary>
    internal IReadOnlyList<ClaudeFolder> ClaudeFolders => _claudeFolders;

    internal CodexEntries Codex => _codexEntries;

    internal ClaudeEntries Claude => _claudeEntries;

    internal IReadOnlyDictionary<string, LimitEntries> ClaudeFolderEntries => _folderEntries;

    public override void Initialize()
    {
    }

    /// <summary>
    /// Lists the sets: Codex, Claude Code (combined) and one per Claude folder found now (folders only: whether
    /// <c>~/.claude</c>, <c>~/.claude-*</c> and the rest hold <c>projects/</c>; no file is opened). A folder made later
    /// gets its own set at the next reload; it counts in the combined set at once when a setting names it.
    /// </summary>
    public override void Load(List<IPluginContainer> containers)
    {
        _claudeFolders = Folders.FindClaude(_env);

        var codex = new PluginContainer("codex", "Codex");
        codex.Entries.AddRange(_codexEntries.Entries);
        var claude = new PluginContainer("claude", "Claude Code");
        claude.Entries.AddRange(_claudeEntries.Entries);
        containers.Add(codex);
        containers.Add(claude);
        foreach (var folder in _claudeFolders)
        {
            var entries = new LimitEntries();
            _folderEntries[folder.ContainerId] = entries;
            var container = new PluginContainer(folder.ContainerId, "Claude Code · " + folder.Name);
            container.Entries.AddRange(entries.Entries);
            containers.Add(container);
        }
        Refresh();
    }

    public override void Update() => Refresh();

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        Refresh(cancellationToken);
        return Task.CompletedTask;
    }

    public override void Close()
    {
    }

    /// <summary>Reads what changed (only what the settings turn on) and shows it; cancelled, it stops and throws.</summary>
    internal void Refresh(CancellationToken cancel = default)
    {
        lock (_refreshLock)
        {
            var settings = _settings;
            var now = _env.Now;
            var zone = _env.Zone;

            if (!settings.ReadCodex)
                _codexEntries.Show(new CodexReading(LimitsReading.None("off: turn on Read Codex in AI usage's settings", "off"), null, null, null, null), now, zone);
            else
                _codexEntries.Show(Guarded(() => _codex.Read(Folders.Codex(_env, settings.CodexFolder), cancel), cancel,
                    type => new CodexReading(LimitsReading.None($"Codex's logs couldn't be read ({type})", "unreadable"), null, null, null, null)), now, zone);

            ClaudeReading claude;
            if (!settings.ReadClaudeCode)
            {
                var off = LimitsReading.None("off: turn on Read Claude Code in AI usage's settings", "off");
                claude = new ClaudeReading(_claudeFolders.ToDictionary(f => f.Path, _ => off), off, "");
            }
            else
            {
                var folders = _claudeFolders.Concat(Folders.OtherClaude(_env, settings.OtherClaudeFolders, _claudeFolders)).ToList();
                claude = Guarded(() => _claude.Read(folders), cancel, type =>
                {
                    var unreadable = LimitsReading.None($"Claude Code's usage files couldn't be read ({type})", "unreadable");
                    return new ClaudeReading(_claudeFolders.ToDictionary(f => f.Path, _ => unreadable), unreadable, "");
                });
            }
            _claudeEntries.Show(claude.Combined, now, zone);
            _claudeEntries.Profile.Value = claude.Profile;
            foreach (var folder in _claudeFolders)
                _folderEntries[folder.ContainerId].Show(claude.Folders.TryGetValue(folder.Path, out var reading) ? reading : LimitsReading.None(LimitsReader.NotConnected, "not connected"), now, zone);
        }
    }

    /// <summary>A reader's answer; past a failure it didn't expect, <paramref name="failed"/>'s, with the failure logged by its kind.</summary>
    private static T Guarded<T>(Func<T> read, CancellationToken cancel, Func<string, T> failed)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancel.IsCancellationRequested))
        {
            PluginLog.Error($"AI usage: a reader failed ({ex.GetType().Name})");
            return failed(ex.GetType().Name);
        }
    }

    public IReadOnlyList<PluginConfigProperty> ConfigProperties
    {
        get
        {
            var settings = _settings;
            return
            [
                new PluginConfigProperty
                {
                    Key = ReadClaudeCode,
                    DisplayName = "Read Claude Code",
                    Description = "Reads Claude Code's limits from the file its status line writes once you connect it (infopanel-usage.json in each Claude folder). Usage numbers only, never message text. Off until you turn it on.",
                    Type = PluginConfigType.Boolean,
                    Value = settings.ReadClaudeCode,
                },
                new PluginConfigProperty
                {
                    Key = ReadCodex,
                    DisplayName = "Read Codex",
                    Description = "Reads Codex's limits and tokens from its session logs (~/.codex/sessions). Usage numbers only, never message text. Off until you turn it on.",
                    Type = PluginConfigType.Boolean,
                    Value = settings.ReadCodex,
                },
                new PluginConfigProperty
                {
                    Key = OtherClaudeFolders,
                    DisplayName = "Other Claude folders",
                    Description = "Claude folders besides ~/.claude, ~/.claude-* and ~/.config/claude, separated by ;. They count in Claude Code's combined readings.",
                    Type = PluginConfigType.String,
                    Value = settings.OtherClaudeFolders,
                },
                new PluginConfigProperty
                {
                    Key = CodexFolder,
                    DisplayName = "Codex folder",
                    Description = "Empty for the default: CODEX_HOME when it is set, else ~/.codex.",
                    Type = PluginConfigType.String,
                    Value = settings.CodexFolder,
                },
            ];
        }
    }

    public void ApplyConfig(string key, object? value)
    {
        // Not the refresh's lock: a change never waits for a read in progress; the next refresh takes it.
        lock (_settingsLock)
        {
            _settings = key switch
            {
                ReadClaudeCode => _settings with { ReadClaudeCode = Switch(value) },
                ReadCodex => _settings with { ReadCodex = Switch(value) },
                OtherClaudeFolders => _settings with { OtherClaudeFolders = FolderText(value) },
                CodexFolder => _settings with { CodexFolder = FolderText(value) },
                _ => throw new ArgumentException("AI usage has no such setting.", nameof(key)),
            };
        }
    }

    private static bool Switch(object? value) => value is bool on ? on : throw new ArgumentException("This setting is on or off.", nameof(value));

    private static string FolderText(object? value) => value switch
    {
        null => "",
        string { Length: <= MaxFolderSettingChars } text => text,
        string => throw new ArgumentException($"At most {MaxFolderSettingChars} characters.", nameof(value)),
        _ => throw new ArgumentException("This setting is text.", nameof(value)),
    };

    private sealed record Settings(bool ReadClaudeCode, bool ReadCodex, string OtherClaudeFolders, string CodexFolder);
}
