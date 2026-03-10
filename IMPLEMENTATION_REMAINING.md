# Remaining implementation steps (Autosave + Undo UI)

The following were implemented in the repo at `D:\Documents\.cursor-projects\infopanel`:

- **InfoPanel/Services/UndoManager.cs** – full implementation (undo/redo stacks, XML snapshots, max depth 50).
- **InfoPanel/Models/SharedModel.cs** – undo integration (PushUndo before mutations, debounced PropertyChanged, Undo/Redo/CanUndo/CanRedo, GetProfileDisplayItemsCopy, SaveDisplayItems, LoadDisplayItemsFromFile).

If `InfoPanel/Views/Components/DisplayItems.xaml.cs` exists in your clone, apply these edits:

## 1. Push undo before drop (in `IDropTarget.Drop`)

Right after the block that returns when `targetItem == sourceItem`, add:

```csharp
// Push undo snapshot before any drop mutation
if (SharedModel.Instance.SelectedProfile is Profile profile)
{
    var copy = SharedModel.Instance.GetProfileDisplayItemsCopy(profile);
    if (copy.Count > 0)
        InfoPanel.Services.UndoManager.Instance.PushUndo(profile, copy.ToList());
}
```

## 2. Undo/Redo button handlers

Add these methods:

```csharp
private void ButtonUndo_Click(object sender, RoutedEventArgs e)
{
    SharedModel.Instance.Undo();
    _displayItemsViewSource?.View?.Refresh();
}

private void ButtonRedo_Click(object sender, RoutedEventArgs e)
{
    SharedModel.Instance.Redo();
    _displayItemsViewSource?.View?.Refresh();
}
```

## 3. In `Instance_PropertyChanged`, refresh undo/redo state

In the `else if (e.PropertyName == nameof(SharedModel.Instance.SelectedProfile))` block, add another branch:

```csharp
else if (e.PropertyName == nameof(SharedModel.Instance.CanUndo) || e.PropertyName == nameof(SharedModel.Instance.CanRedo))
{
    CommandManager.InvalidateRequerySuggested();
}
```

## 4. DisplayItems.xaml – Undo/Redo buttons and Reload label

- Add Undo and Redo buttons (e.g. next to the existing Reload button), bound to `ButtonUndo_Click` and `ButtonRedo_Click`.
- Change the Reload button label from "Revert" to "Reload from disk".

## 5. DesignPage.xaml – tip text

Change the tip from "Pressing the revert button will revert to your last saved state." to something like: "Undo/Redo revert design steps. Reload from disk resets to the last saved file."

## 6. Settings + Autosave (plan todos autosave-settings through autosave-ui)

- **Settings.cs**: Done in repo – `AutosaveEnabled` and `AutosaveIntervalSeconds` (default 60) added.
- **ConfigModel**: In LoadSettings (when deserializing), add: `Settings.AutosaveEnabled = settings.AutosaveEnabled; Settings.AutosaveIntervalSeconds = settings.AutosaveIntervalSeconds;` (XmlSerializer will persist if properties exist). Add: `private DispatcherTimer? _autosaveTimer;` and in Initialize (or constructor after LoadProfiles): start a DispatcherTimer with Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.AutosaveIntervalSeconds)), Tick = async (s,e) => { if (Settings.AutosaveEnabled && SharedModel.Instance.IsDirty) { ConfigModel.Instance.SaveProfiles(); SharedModel.Instance.SaveDisplayItems(); SharedModel.Instance.ClearDirty(); } }. Start timer; in Cleanup stop it. Wire Settings_PropertyChanged for AutosaveEnabled/AutosaveIntervalSeconds to restart timer.
- **SharedModel**: Add `bool IsDirty`, `void MarkDirty()`, `void ClearDirty()`. Call MarkDirty from PushUndoSnapshot and from DisplayItem_PropertyChanged debounce. ClearDirty in SaveDisplayItems.
- **Settings UI**: Add checkbox bound to AutosaveEnabled and numeric input for AutosaveIntervalSeconds.

After applying the DisplayItems and DesignPage changes, build and commit to `https://github.com/fweepa/infopanel.git`.
