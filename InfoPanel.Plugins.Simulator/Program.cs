using System.Globalization;
using System.Reflection;
using System.Text;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;

return await InfoPanel.Plugins.Simulator.SimulatorCommand.RunAsync(args, Console.Out, CancellationToken.None);

namespace InfoPanel.Plugins.Simulator
{
    /// <summary>
    /// The simulator's command line, run by its Program and, in process, by its tests:
    /// <c>InfoPanel.Plugins.Simulator &lt;plugin DLL&gt; [plugin id] [--set key=value]… [--action &lt;method&gt;]… [--yes] [--once]</c>.
    /// It loads the DLL's plugins (the one with the id, or all), applies each <c>--set</c> to every plugin with that setting,
    /// shows each <c>--action</c> and runs it only with <c>--yes</c>, then prints the readings: every round until stopped,
    /// or one round with <c>--once</c>, which exits 0, or 1 when a plugin failed (to load, to list or take a setting, in its
    /// action, its update, a read of what it shows or its close). Arguments it can't take exit 2, saying why, before any
    /// setting is applied: a value is checked as the engine checks it (a finite number within the setting's declared range,
    /// one of a choice's options). A number a plugin can't read (not finite) prints as "–".
    /// </summary>
    public static class SimulatorCommand
    {
        public const string Usage =
            "Usage: InfoPanel.Plugins.Simulator <path to the plugin's DLL> [plugin id] [--set key=value]... [--action <method>]... [--yes] [--once]";

        /// <summary>What a number a plugin can't read prints as.</summary>
        public const string NoReading = "–";

        private sealed record Options(string Dll, string? Id, List<(string Key, string Value)> Settings, List<string> Actions, bool Yes, bool Once);

        public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
        {
            if (Parse(args, out var problem) is not { } options)
            {
                output.WriteLine(problem);
                output.WriteLine(Usage);
                return 2;
            }

            var pluginPath = Path.GetFullPath(options.Dll);
            List<IPlugin> plugins;
            try
            {
                plugins = PluginLoader.InitializePlugin(pluginPath).ToList();
            }
            catch (Exception ex)
            {
                output.WriteLine($"Can't load the plugins in {pluginPath}: {ex.Message}");
                return 1;
            }

            // An id the DLL doesn't have is an error here, before the display loop starts.
            var selected = plugins.Where(plugin => options.Id == null || IdOf(plugin) == options.Id).ToList();
            if (selected.Count == 0)
            {
                var ids = string.Join(", ", plugins.Select(plugin => IdOf(plugin) ?? "(unreadable)"));
                output.WriteLine(options.Id == null
                    ? $"No plugin found in {pluginPath}"
                    : $"No plugin with the id '{options.Id}' in {pluginPath}. Its plugin ids: {(ids.Length == 0 ? "(none)" : ids)}");
                return 1;
            }

            return options.Once
                ? await RunOnceAsync(options, selected, output, cancellationToken)
                : await RunLoopAsync(options, pluginPath, selected, output, cancellationToken);
        }

        private static Options? Parse(string[] args, out string problem)
        {
            problem = "";
            var positional = new List<string>();
            var settings = new List<(string, string)>();
            var actions = new List<string>();
            bool yes = false, once = false;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--yes":
                        yes = true;
                        break;
                    case "--once":
                        once = true;
                        break;
                    case "--set" or "--action" when i + 1 >= args.Length:
                        problem = $"{arg} takes a value";
                        return null;
                    case "--set":
                        var pair = args[++i];
                        var equals = pair.IndexOf('=');
                        if (equals <= 0)
                        {
                            problem = $"--set takes key=value, not '{pair}'";
                            return null;
                        }
                        settings.Add((pair[..equals], pair[(equals + 1)..]));
                        break;
                    case "--action":
                        actions.Add(args[++i]);
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            problem = $"Unknown option {arg}";
                            return null;
                        }
                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count is < 1 or > 2)
            {
                problem = positional.Count == 0 ? "No plugin DLL given" : $"One DLL and one plugin id at most, not '{string.Join(" ", positional)}'";
                return null;
            }

            return new Options(positional[0], positional.Count > 1 ? positional[1] : null, settings, actions, yes, once);
        }

        /// <summary>
        /// One round: each plugin started, given its settings and actions, updated once, printed and closed. Every call to a
        /// plugin and every read of what it shows is guarded: a failure is said and counted, and the exit code is reckoned
        /// once every plugin started has been closed.
        /// </summary>
        private static async Task<int> RunOnceAsync(Options options, List<IPlugin> selected, TextWriter output, CancellationToken cancellationToken)
        {
            // Every plugin whose Initialize ran, closed at the end whatever the round did.
            var initialized = new List<IPlugin>();
            var code = 1;
            var closed = true;
            try
            {
                code = await RoundAsync(options, selected, initialized, output, cancellationToken);
            }
            finally
            {
                foreach (var plugin in initialized)
                {
                    try
                    {
                        plugin.Close();
                    }
                    catch (Exception ex)
                    {
                        closed = false;
                        output.WriteLine($"Plugin {NameOf(plugin)} failed to close: {ex.Message}");
                    }
                }
            }

            return code == 0 && !closed ? 1 : code;
        }

        /// <summary>The round of <see cref="RunOnceAsync"/> before the plugins are closed: 0, 1 when a plugin failed, 2 for arguments it can't take.</summary>
        private static async Task<int> RoundAsync(Options options, List<IPlugin> selected, List<IPlugin> initialized, TextWriter output, CancellationToken cancellationToken)
        {
            var failed = false;
            // The plugins that loaded too: run and printed.
            var running = new List<(IPlugin Plugin, List<IPluginContainer> Containers)>();
            foreach (var plugin in selected)
            {
                var containers = new List<IPluginContainer>();
                try
                {
                    plugin.Initialize();
                    initialized.Add(plugin);
                    plugin.Load(containers);
                    running.Add((plugin, containers));
                    output.WriteLine($"Plugin {NameOf(plugin)} loaded successfully");
                }
                catch (Exception ex)
                {
                    failed = true;
                    output.WriteLine($"Plugin {NameOf(plugin)} failed to load: {ex.Message}");
                }
            }

            if (running.Count == 0)
            {
                output.WriteLine("No plugin could be loaded.");
                return 1;
            }

            switch (ApplyAndAct(options, running.Select(r => r.Plugin).ToList(), output))
            {
                case null:
                    output.WriteLine(Usage);
                    return 2;
                case false:
                    failed = true;
                    break;
            }

            foreach (var (plugin, _) in running)
            {
                try
                {
                    if (plugin.UpdateInterval.TotalMilliseconds > 0)
                        await plugin.UpdateAsync(cancellationToken);
                    else
                        plugin.Update();
                }
                catch (Exception ex)
                {
                    failed = true;
                    output.WriteLine($"Plugin {NameOf(plugin)} failed to update: {ex.Message}");
                }
            }

            var screen = new StringBuilder();
            foreach (var (plugin, containers) in running)
                failed |= !AppendScreen(screen, plugin, containers, updateTimeMilliseconds: null);
            output.WriteLine(screen.ToString());
            return failed ? 1 : 0;
        }

        /// <summary>A plugin's name, or its type's when reading the name fails.</summary>
        private static string NameOf(IPlugin plugin)
        {
            try
            {
                return plugin.Name;
            }
            catch (Exception)
            {
                return plugin.GetType().FullName ?? plugin.GetType().Name;
            }
        }

        /// <summary>A plugin's id, or null when reading it fails.</summary>
        private static string? IdOf(IPlugin plugin)
        {
            try
            {
                return plugin.Id;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The readings every round until cancelled, on a screen cleared each time they change.</summary>
        private static async Task<int> RunLoopAsync(Options options, string pluginPath, List<IPlugin> selected, TextWriter output, CancellationToken cancellationToken)
        {
            var pluginInfo = PluginLoader.GetPluginInfo(Path.GetDirectoryName(pluginPath)!);
            var pluginDescriptor = new PluginDescriptor(pluginPath, pluginInfo);

            foreach (var plugin in selected)
            {
                PluginWrapper pluginWrapper = new(pluginDescriptor, plugin);

                if (pluginDescriptor.PluginWrappers.TryAdd(pluginWrapper.Id, pluginWrapper))
                {
                    try
                    {
                        await pluginWrapper.Initialize();
                        output.WriteLine($"Plugin {pluginWrapper.Name} loaded successfully");
                    }
                    catch (Exception ex)
                    {
                        // A plugin that failed to initialise must not reach the count check or the screens.
                        pluginDescriptor.PluginWrappers.Remove(pluginWrapper.Id);
                        output.WriteLine($"Plugin {pluginWrapper.Name} failed to load: {ex.Message}");
                    }
                }
                else
                {
                    output.WriteLine($"Plugin {pluginWrapper.Name} already loaded or duplicate plugin/name");
                }
            }

            if (pluginDescriptor.PluginWrappers.Count == 0)
            {
                output.WriteLine("No plugin could be loaded.");
                return 1;
            }

            try
            {
                if (ApplyAndAct(options, pluginDescriptor.PluginWrappers.Values.Select(w => w.Plugin).ToList(), output) is null)
                {
                    output.WriteLine(Usage);
                    return 2;
                }

                await Task.Delay(1000, cancellationToken);
                Console.Clear();

                var lastOutput = string.Empty;
                while (!cancellationToken.IsCancellationRequested)
                {
                    // One screen for every plugin, printed once per round.
                    var screen = new StringBuilder();
                    foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
                    {
                        wrapper.Update();
                        AppendScreen(screen, wrapper.Plugin, wrapper.PluginContainers, wrapper.UpdateTimeMilliseconds);
                    }

                    // Only update the console if the output has changed, to reduce flicker
                    var text = screen.ToString();
                    if (text != lastOutput)
                    {
                        lastOutput = text;
                        Console.Clear();
                        output.WriteLine(text);
                    }

                    await Task.Delay(30, cancellationToken);
                }

                return 0;
            }
            finally
            {
                foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
                    await wrapper.StopAsync();
            }
        }

        /// <summary>
        /// Applies every <c>--set</c> to each plugin with the setting, once every value given has been checked, then shows
        /// every <c>--action</c> and runs it with <c>--yes</c>. Null for a setting or action no plugin has, or a value its
        /// setting can't take (said in <paramref name="output"/>), before any setting is applied; false when a plugin failed
        /// to list its settings, to take a setting or in an action; else true.
        /// </summary>
        private static bool? ApplyAndAct(Options options, List<IPlugin> plugins, TextWriter output)
        {
            var ok = true;

            // Each plugin's settings, read once; a plugin whose settings can't be read has failed and takes none.
            var settings = new List<(IPlugin Plugin, IPluginConfigurable Configurable, List<PluginConfigProperty> Properties)>();
            if (options.Settings.Count > 0)
            {
                foreach (var plugin in plugins)
                {
                    if (plugin is not IPluginConfigurable configurable)
                        continue;
                    try
                    {
                        settings.Add((plugin, configurable, [.. (configurable.ConfigProperties ?? []).Where(p => p != null)]));
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        output.WriteLine($"Plugin {NameOf(plugin)} failed to list its settings: {ex.Message}");
                    }
                }
            }

            var changes = new List<(IPlugin Plugin, IPluginConfigurable Configurable, string Key, object Value, string Text)>();
            foreach (var (key, text) in options.Settings)
            {
                var targets = settings
                    .Select(s => (s.Plugin, s.Configurable, Property: s.Properties.FirstOrDefault(c => c.Key == key)))
                    .Where(t => t.Property != null)
                    .ToList();
                if (targets.Count == 0)
                {
                    if (!ok)
                    {
                        // A plugin whose settings couldn't be read may have it: that plugin failed, not the arguments.
                        output.WriteLine($"Setting {key}: not applied; no plugin whose settings could be read has it");
                        continue;
                    }
                    var keys = settings.SelectMany(s => s.Properties).Select(c => c.Key).Distinct().ToList();
                    output.WriteLine($"No setting '{key}'. Its settings: {(keys.Count == 0 ? "(none)" : string.Join(", ", keys))}");
                    return null;
                }

                foreach (var (plugin, configurable, property) in targets)
                {
                    if (ValueOf(property!, text, out var problem) is not { } value)
                    {
                        output.WriteLine(problem);
                        return null;
                    }
                    changes.Add((plugin, configurable, key, value, text));
                }
            }

            foreach (var (plugin, configurable, key, value, text) in changes)
            {
                try
                {
                    configurable.ApplyConfig(key, value);
                    output.WriteLine($"Setting {key} of {NameOf(plugin)}: {text}");
                }
                catch (Exception ex)
                {
                    ok = false;
                    output.WriteLine($"Setting {key} of {NameOf(plugin)}: failed: {ex.Message}");
                }
            }

            foreach (var name in options.Actions)
            {
                var targets = plugins
                    .Select(p => (Plugin: p, Method: ActionsOf(p).FirstOrDefault(a => a.Method.Name == name)))
                    .Where(t => t.Method.Method != null)
                    .ToList();
                if (targets.Count == 0)
                {
                    var all = plugins.SelectMany(ActionsOf).Select(a => $"{a.Method.Name} ({a.DisplayName})").Distinct().ToList();
                    output.WriteLine($"No action '{name}'. Its actions: {(all.Count == 0 ? "(none)" : string.Join(", ", all))}");
                    return null;
                }

                foreach (var (plugin, (method, displayName)) in targets)
                {
                    var label = $"{method.Name} ({displayName})";
                    if (!options.Yes)
                    {
                        output.WriteLine($"{label}: not run; add --yes to run it");
                        continue;
                    }
                    try
                    {
                        method.Invoke(plugin, null);
                        output.WriteLine($"{label}: done");
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        output.WriteLine($"{label}: failed: {cause.Message}");
                    }
                }
            }

            return ok;
        }

        /// <summary>A plugin's actions as the engine finds them: public methods without parameters marked <see cref="PluginActionAttribute"/>.</summary>
        private static IEnumerable<(MethodInfo Method, string DisplayName)> ActionsOf(IPlugin plugin) =>
            plugin.GetType().GetMethods()
                .Where(m => m.GetParameters().Length == 0)
                .Select(m => (Method: m, Attribute: m.GetCustomAttribute<PluginActionAttribute>(inherit: false)))
                .Where(a => a.Attribute != null)
                .Select(a => (a.Method, a.Attribute!.DisplayName));

        /// <summary>The longest text a text setting takes, in characters: the engine's limit (its <c>PluginSettings.StringChars</c>).</summary>
        private const int StringChars = 4096;

        /// <summary>
        /// <paramref name="text"/> as the type <paramref name="property"/> takes, checked as the engine checks a setting's
        /// value before its plugin gets it (a finite number within the declared minimum and maximum, a choice among the
        /// options, text within <see cref="StringChars"/>), or null with why.
        /// </summary>
        private static object? ValueOf(PluginConfigProperty property, string text, out string problem)
        {
            problem = "";
            var key = property.Key;
            switch (property.Type)
            {
                case PluginConfigType.String:
                    if (text.EnumerateRunes().Count() <= StringChars)
                        return text;
                    problem = $"'{key}' takes text of at most {StringChars} characters";
                    return null;
                case PluginConfigType.Boolean:
                    if (bool.TryParse(text, out var flag))
                        return flag;
                    problem = $"'{key}' takes true or false, not '{text}'";
                    return null;
                case PluginConfigType.Integer:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    {
                        problem = $"'{key}' takes a whole number, not '{text}'";
                        return null;
                    }
                    return InRange(property, whole, text, out problem) ? whole : null;
                case PluginConfigType.Double:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    {
                        problem = $"'{key}' takes a finite number, not '{text}'";
                        return null;
                    }
                    return InRange(property, number, text, out problem) ? number : null;
                case PluginConfigType.Choice:
                    var choices = (property.Options ?? []).Where(o => o != null).ToList();
                    if (choices.Contains(text, StringComparer.Ordinal))
                        return text;
                    problem = $"'{key}' takes one of {(choices.Count == 0 ? "(none)" : string.Join(", ", choices))}, not '{text}'";
                    return null;
                default:
                    problem = $"'{key}' can't be changed";
                    return null;
            }
        }

        /// <summary>Whether <paramref name="value"/> is within the setting's declared minimum and maximum (a bound that isn't finite is none, as in the engine).</summary>
        private static bool InRange(PluginConfigProperty property, double value, string text, out string problem)
        {
            problem = "";
            double? min = property.MinValue is { } low && double.IsFinite(low) ? low : null;
            double? max = property.MaxValue is { } high && double.IsFinite(high) ? high : null;
            if (min is { } atLeast && value < atLeast || max is { } atMost && value > atMost)
            {
                problem = $"'{property.Key}' takes a value from {Show(min)} to {Show(max)}, not '{text}'";
                return false;
            }
            return true;
        }

        private static string Show(double? bound) => bound is { } b ? b.ToString(CultureInfo.InvariantCulture) : "any";

        /// <summary>
        /// One plugin's part of the screen: its containers' entries, listed in order. A read that fails is shown as failed
        /// in its line, and the rest are shown; false when any failed.
        /// </summary>
        private static bool AppendScreen(StringBuilder buffer, IPlugin plugin, List<IPluginContainer> containers, long? updateTimeMilliseconds)
        {
            var ok = true;
            var time = updateTimeMilliseconds is { } ms ? $", UpdateTime={ms}ms" : "";
            string interval;
            try
            {
                interval = $"{plugin.UpdateInterval.TotalMilliseconds}ms";
            }
            catch (Exception ex)
            {
                ok = false;
                interval = $"failed: {ex.Message}";
            }
            buffer.AppendLine($"-{NameOf(plugin)} ({plugin.GetType().FullName}) [UpdateInterval={interval}{time}]");

            foreach (var container in containers)
            {
                List<IPluginData> entries;
                try
                {
                    buffer.AppendLine($"--{container.Name}");
                    entries = [.. container.Entries];
                }
                catch (Exception ex)
                {
                    ok = false;
                    buffer.AppendLine($"--failed: {ex.Message}");
                    buffer.AppendLine();
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        if (LineOf(entry) is { } line)
                            buffer.AppendLine(line);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        buffer.AppendLine($"---{EntryNameOf(entry)}: failed: {ex.Message}");
                    }
                }

                buffer.AppendLine();
            }

            return ok;
        }

        /// <summary>An entry's line on the screen, or null for an entry of a kind it doesn't show; throws when a read fails.</summary>
        private static string? LineOf(IPluginData entry)
        {
            switch (entry)
            {
                case IPluginText text:
                    return $"---{text.Name}: {text.Value}";
                case IPluginSensor sensor:
                    var value = sensor.Value;
                    return float.IsFinite(value)
                        ? $"---{sensor.Name}: {value.ToString(CultureInfo.InvariantCulture)}{sensor.Unit}"
                        : $"---{sensor.Name}: {NoReading}";
                case IPluginTable table:
                    return $"---{table.Name}: {table.ToString()}";
                case IPluginImage image:
                    // Its size only: InfoPanel 2.0 shows the picture; a console shows that there is one.
                    var picture = image.Value;
                    return $"---{image.Name}: {(picture.IsEmpty ? "no picture" : $"a picture of {picture.Length} bytes")}";
                default:
                    return null;
            }
        }

        /// <summary>An entry's name, or its type's when reading the name fails.</summary>
        private static string EntryNameOf(IPluginData entry)
        {
            try
            {
                return entry.Name;
            }
            catch (Exception)
            {
                return entry.GetType().Name;
            }
        }
    }
}
