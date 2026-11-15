using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using InfoPanel.Sensors;
using InfoPanel.Spec;

namespace InfoPanel.Scenes;

/// <summary>
/// Inputs that give an input without a binding a key to read (<see cref="SceneRuntime"/> asks them for each such input
/// on every tick); null leaves it unbound.
/// </summary>
internal interface IUnboundInputs
{
    (string Key, string Stat)? Bind(string itemId, string input);
}

/// <summary>
/// A one-off draw's made-up values for every input (<see cref="InfoPanel.Output.RenderService.InspectOnce"/>), so a document drawn
/// before it is bound shows values of each input's type: the made-up values of a simulated subscription
/// (<see cref="SimulatedSceneInputs"/>) over <paramref name="live"/>, and besides them
/// <list type="bullet">
/// <item>an input without a binding reads a key of its own, <see cref="OwnKey"/>, of a source no plugin may name and the
/// engine has none of, so it is never read from <paramref name="live"/>: a number walks as a key with no reading does,
/// a text and a table are made up from <see cref="SampleText"/> and <see cref="SampleTable"/>;</item>
/// <item>a text or table whose binding has no reading is made up from the same samples, with its key's kind and labels
/// (System Info's uptime from its start, say).</item>
/// </list>
/// Only a one-off draw with made-up values for every input is made with these; every loop and subscription reads its
/// inputs as before. Thread-safe.
/// </summary>
internal sealed class EveryInputMadeUp(ISceneInputSource live, TimeProvider time, SpecLimits? limits = null) : ISceneInputSource, IUnboundInputs
{
    /// <summary>The source an unbound input's key names: a built-in source's form, which no plugin may take, and no source of the engine's.</summary>
    internal const string OwnSource = "made-up";

    /// <summary>The text a made-up text starts from: one none of the heuristics places, so it shows its label.</summary>
    internal const string SampleText = "Text";

    private readonly SimulatedSceneInputs _madeUp = new(new ReadingsOrSamples(live), time, limits, (live as SensorSceneInputs)?.Catalogue);

    /// <summary>The key an unbound input of item <paramref name="itemId"/> reads: one for each item and input.</summary>
    internal static string OwnKey(string itemId, string input) => SensorKey.Plugin(string.Create(CultureInfo.InvariantCulture,
        $"/infopanel.{OwnSource}/{StableHash.Fnv1a(itemId + "\n" + input):x8}")).ToString();

    private static readonly string OwnPrefix = SensorKey.Plugin($"/infopanel.{OwnSource}/x").ToString()[..^1];

    private static bool IsOwn(string key) => key.StartsWith(OwnPrefix, StringComparison.Ordinal);

    public (string Key, string Stat)? Bind(string itemId, string input) => (OwnKey(itemId, input), "now");

    public SceneReading Read(string key, string inputType) => _madeUp.Read(key, inputType);

    public IReadOnlyList<double> History(string key) => _madeUp.History(key);

    /// <summary>
    /// A table of processes, as System Info's tables of the busiest processes have them: their columns, and a row for each
    /// of <see cref="SimulatedText.Processes"/>' first eight, each cell's numbers made up again each step.
    /// </summary>
    internal static SceneTable SampleTable { get; } = MakeSampleTable();

    private static SceneTable MakeSampleTable()
    {
        using var table = new DataTable();
        foreach (var column in new[] { "Process Name", "Usage", "Utility", "Memory" })
            table.Columns.Add(column, typeof(string));
        for (var r = 0; r < 8; r++)
        {
            var share = 40 * Math.Pow(2.0 / 3, r);
            table.Rows.Add(SimulatedText.Processes[r],
                share.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                (share * 0.8).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                (900 * Math.Pow(2.0 / 3, r)).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
        }
        return SensorSceneInputs.FromDataTable(table);
    }

    /// <summary>
    /// What the made-up values are made from: <paramref name="live"/>'s readings, a sample in place of a text or table
    /// without one, and only samples for <see cref="OwnKey"/>s (a number's: none, so it walks as a key with no reading).
    /// </summary>
    private sealed class ReadingsOrSamples(ISceneInputSource live) : ISceneInputSource
    {
        public SceneReading Read(string key, string inputType)
        {
            var reading = IsOwn(key) ? SceneReading.Unavailable : live.Read(key, inputType);
            return reading.Available ? reading : inputType switch
            {
                "text" => new SceneReading { Status = SceneInputStatus.Live, Text = SampleText },
                "table" => new SceneReading { Status = SceneInputStatus.Live, Table = SampleTable, Text = SimulatedText.Summary(SampleTable) },
                _ => reading,
            };
        }

        public IReadOnlyList<double> History(string key) => IsOwn(key) ? [] : live.History(key);
    }
}
