# Plugin Registry

InfoPanel maintains a community plugin registry in [`plugins.json`](plugins.json) at the root of this repository. The InfoPanel API reads this file to populate the plugin browser inside the app, enriching each entry with GitHub release data, download counts, and ratings.

## Adding Your Plugin

To list your plugin in the registry, open a pull request that adds an entry to the `plugins` array in `plugins.json`.

### Required Fields

| Field | Type | Description |
|---|---|---|
| `repo` | `string` | GitHub repository in `owner/repo` format (e.g. `F3NN3X/InfoPanel.Spotify`) |
| `name` | `string` | Display name shown in the plugin browser |
| `description` | `string` | Short description of what the plugin does |

### Optional Fields

| Field | Type | Description |
|---|---|---|
| `category` | `string` | One of `media`, `monitoring`, `utilities`, or `other` (defaults to `other`) |
| `minVersion` | `string` | Minimum InfoPanel version required (e.g. `1.4.0`) |
| `icon` | `string` | URL to a custom icon image; if omitted, the API checks for `icon.png` in your repo root, then falls back to your GitHub avatar |

### Example Entry

```json
{
  "repo": "YourUsername/InfoPanel.MyPlugin",
  "name": "My Plugin",
  "description": "Does something useful with your hardware data",
  "category": "monitoring",
  "minVersion": "1.4.0"
}
```

### Naming Convention

The repository name is used to match marketplace plugins to locally installed plugins. The API exposes a `repoName` field (the `repo` portion after the `/`) which the desktop app compares against installed plugin folder names.

For this matching to work, your **repository name must match your plugin's installed folder name**. For example, if your repo is `YourUsername/InfoPanel.MyPlugin`, the installed folder must be `InfoPanel.MyPlugin`.

### Release Requirements

The API automatically discovers releases from your GitHub repository. For downloads to work:

- Create a [GitHub Release](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository) with a tag (e.g. `v1.0.0`)
- Attach a ZIP asset whose name starts with `InfoPanel.` and ends with `.zip` (e.g. `InfoPanel.MyPlugin.zip` or `InfoPanel.MyPlugin-v1.0.0.zip`)
- The ZIP **must** contain a top-level folder matching the plugin name. The DLLs go **inside** that folder, not at the ZIP root:

  **Correct** — DLLs inside a named folder:
  ```
  InfoPanel.MyPlugin.zip
  └── InfoPanel.MyPlugin/
      ├── InfoPanel.MyPlugin.dll
      ├── [dependency DLLs]
      └── PluginInfo.ini
  ```

  **Wrong** — DLLs at the ZIP root (no containing folder):
  ```
  InfoPanel.MyPlugin.zip
  ├── InfoPanel.MyPlugin.dll
  ├── [dependency DLLs]
  └── PluginInfo.ini
  ```

  To get the correct structure, zip the **folder itself**, not its contents. For example:
  - **Windows Explorer:** Right-click the `InfoPanel.MyPlugin` folder → Compress to ZIP file
  - **Command line:** `Compress-Archive -Path InfoPanel.MyPlugin -DestinationPath InfoPanel.MyPlugin.zip`
  - **CI (GitHub Actions):**
    ```yaml
    - run: Compress-Archive -Path InfoPanel.MyPlugin -DestinationPath InfoPanel.MyPlugin.zip
    ```

The latest release version, download URL, and changelog are pulled automatically from your repo. The registry syncs hourly, so it may take up to an hour for new releases or registry changes to appear in the app.

## PR Checklist

Before submitting your pull request:

- [ ] Plugin builds and runs correctly with the latest InfoPanel release
- [ ] `repo` points to a public GitHub repository
- [ ] Repository has at least one GitHub Release with a properly named ZIP asset
- [ ] `name` and `description` are concise and accurate
- [ ] `category` is set to the most appropriate value
- [ ] `PluginInfo.ini` is included in the release ZIP
- [ ] Entry is valid JSON (no trailing commas, proper quoting)
