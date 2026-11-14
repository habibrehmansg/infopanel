namespace InfoPanel.Plugins
{
    /// <summary>
    /// A plugin whose actions (<see cref="PluginActionAttribute"/>) say what they will do: whether each can run now, one
    /// line of its state, and the confirmation a person sees before it runs. Optional: a plugin without it has every action
    /// enabled, with no state and no confirmation, as in InfoPanel 1.x.
    /// </summary>
    public interface IPluginActionStates
    {
        /// <summary>
        /// The action <paramref name="method"/>'s state now (the name of one of the plugin's action methods). Called on any
        /// thread, while the plugin updates and while the action itself runs, and before each run: an action that isn't
        /// <see cref="PluginActionState.Enabled"/> isn't run. Answer quickly; one that takes over 5 seconds, or throws, is
        /// taken as disabled. Null is the default state (enabled, no state, no confirmation).
        /// </summary>
        PluginActionState? Describe(string method);
    }

    /// <summary>An action's state (<see cref="IPluginActionStates.Describe"/>).</summary>
    public sealed class PluginActionState
    {
        /// <summary>The longest <see cref="State"/> shown, in characters; a longer one is cut.</summary>
        public const int StateChars = 256;

        /// <summary>Whether it can run now. A disabled action isn't run, and <see cref="State"/> should say why.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>One line for people, shown with the action: "Connected · 5 folders", or why it can't run. Null: none.</summary>
        public string? State { get; init; }

        /// <summary>What a person confirms before it runs, or null to run it at once.</summary>
        public PluginActionConfirmation? Confirm { get; init; }
    }

    /// <summary>The confirmation a person sees before an action runs, in the plugin's own words: plain text, no markup.</summary>
    public sealed class PluginActionConfirmation
    {
        /// <summary>The longest <see cref="Title"/> shown, in characters; a longer one is cut.</summary>
        public const int TitleChars = 256;

        /// <summary>The longest <see cref="Text"/> shown, in characters; a longer one is cut.</summary>
        public const int TextChars = 4096;

        /// <summary>Its title, a question or the action's name ("Connect Claude Code limits?"); empty: the action's name.</summary>
        public required string Title { get; init; }

        /// <summary>What the action will do, as plain paragraphs separated by blank lines.</summary>
        public required string Text { get; init; }
    }
}
