using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel.Services;

/// <summary>
/// Manages undo/redo stacks per profile using XML-serialized snapshots of display items.
/// </summary>
public sealed class UndoManager
{
    private static readonly ILogger Logger = Log.ForContext<UndoManager>();
    private const int MaxUndoDepth = 50;

    private static readonly Lazy<UndoManager> Lazy = new(() => new UndoManager());
    public static UndoManager Instance => Lazy.Value;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Stack<string>> _undoStacks = new();
    private readonly Dictionary<Guid, Stack<string>> _redoStacks = new();

    private static readonly Type[] DisplayItemTypes =
    [
        typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem),
        typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem),
        typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem),
        typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)
    ];

    private static readonly XmlSerializer Serializer = new(typeof(List<DisplayItem>), DisplayItemTypes);

    private UndoManager() { }

    /// <summary>
    /// Push current display items state onto the undo stack for the given profile.
    /// Call before performing a mutable operation.
    /// </summary>
    public void PushUndo(Profile profile, IList<DisplayItem> displayItems)
    {
        if (profile == null || displayItems == null) return;

        try
        {
            var snapshot = SerializeDisplayItems(displayItems);
            if (string.IsNullOrEmpty(snapshot)) return;

            lock (_lock)
            {
                var guid = profile.Guid;
                if (!_undoStacks.TryGetValue(guid, out var stack))
                {
                    stack = new Stack<string>();
                    _undoStacks[guid] = stack;
                }

                TrimStack(stack);
                stack.Push(snapshot);

                if (_redoStacks.TryGetValue(guid, out var redoStack))
                    redoStack.Clear();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to push undo snapshot for profile {ProfileName}", profile.Name);
        }
    }

    /// <summary>
    /// Trim undo stack to MaxUndoDepth by keeping the most recent entries.
    /// </summary>
    private static void TrimStack(Stack<string> stack)
    {
        if (stack.Count <= MaxUndoDepth) return;
        var list = new List<string>();
        for (int i = 0; i < MaxUndoDepth && stack.Count > 0; i++)
            list.Add(stack.Pop());
        list.Reverse();
        foreach (var item in list)
            stack.Push(item);
    }

    public bool CanUndo(Profile profile)
    {
        if (profile == null) return false;
        lock (_lock)
            return _undoStacks.TryGetValue(profile.Guid, out var stack) && stack.Count > 0;
    }

    public bool CanRedo(Profile profile)
    {
        if (profile == null) return false;
        lock (_lock)
            return _redoStacks.TryGetValue(profile.Guid, out var stack) && stack.Count > 0;
    }

    /// <summary>
    /// Pop undo stack and return deserialized display items for the profile.
    /// currentStateXmlForRedo: optional serialized current state (from SerializeDisplayItemsForProfile) to push onto redo stack.
    /// </summary>
    public List<DisplayItem>? Undo(Profile profile, string? currentStateXmlForRedo = null)
    {
        if (profile == null) return null;
        lock (_lock)
        {
            if (!_undoStacks.TryGetValue(profile.Guid, out var stack) || stack.Count == 0)
                return null;

            var snapshot = stack.Pop();
            var items = DeserializeDisplayItems(snapshot, profile);
            if (items == null) return null;

            if (!string.IsNullOrEmpty(currentStateXmlForRedo))
            {
                if (!_redoStacks.TryGetValue(profile.Guid, out var redoStack))
                {
                    redoStack = new Stack<string>();
                    _redoStacks[profile.Guid] = redoStack;
                }
                redoStack.Push(currentStateXmlForRedo);
            }
            return items;
        }
    }

    /// <summary>
    /// Serialize display items to XML (for passing current state to Undo for redo stack).
    /// </summary>
    public static string? SerializeDisplayItemsForProfile(IList<DisplayItem> displayItems)
    {
        return SerializeDisplayItems(displayItems);
    }

    /// <summary>
    /// Pop redo stack and return deserialized display items for the profile.
    /// </summary>
    public List<DisplayItem>? Redo(Profile profile)
    {
        if (profile == null) return null;
        lock (_lock)
        {
            if (!_redoStacks.TryGetValue(profile.Guid, out var redoStack) || redoStack.Count == 0)
                return null;

            var snapshot = redoStack.Pop();
            return DeserializeDisplayItems(snapshot, profile);
        }
    }

    /// <summary>
    /// Clear undo/redo history for a profile (e.g. on load or profile switch).
    /// </summary>
    public void ClearHistory(Profile profile)
    {
        if (profile == null) return;
        lock (_lock)
        {
            _undoStacks.Remove(profile.Guid);
            _redoStacks.Remove(profile.Guid);
        }
    }

    /// <summary>
    /// Remove history when a profile is deleted.
    /// </summary>
    public void RemoveProfile(Guid profileGuid)
    {
        lock (_lock)
        {
            _undoStacks.Remove(profileGuid);
            _redoStacks.Remove(profileGuid);
        }
    }

    private static string? SerializeDisplayItems(IList<DisplayItem> displayItems)
    {
        if (displayItems == null || displayItems.Count == 0)
            return string.Empty;

        try
        {
            var list = displayItems.ToList();
            using var ms = new MemoryStream();
            var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true, OmitXmlDeclaration = true };
            using (var wr = XmlWriter.Create(ms, settings))
                Serializer.Serialize(wr, list);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Serialize display items failed");
            return null;
        }
    }

    private static List<DisplayItem>? DeserializeDisplayItems(string? xml, Profile profile)
    {
        if (string.IsNullOrWhiteSpace(xml) || profile == null) return null;

        try
        {
            using var rd = XmlReader.Create(new StringReader(xml));
            if (Serializer.Deserialize(rd) is not List<DisplayItem> items)
                return null;

            foreach (var item in items)
                item.SetProfile(profile);

            return items;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Deserialize display items failed");
            return null;
        }
    }
}
