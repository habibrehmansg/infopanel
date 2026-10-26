# Step 1 manual acceptance (macOS)

Run on 2026-10-26 on the dev Mac (M4 Max, macOS 27, .NET SDK 10.0.403), against the M8 tree. No real 1.x export was available, so the 1.x data folder was built from the frozen fixtures (`InfoPanel.Core.Tests/Fixtures/v1/` plus `Fixtures/assets/` renamed to the names the items use). Steps 2–5 were driven through the engine's own sign-in link and checked through the API.

| # | Step | Result |
|---|---|---|
| 1 | `dotnet run --project InfoPanel -c Release -- --import-v1 <1.x folder>` opens the browser | Done. Imported "Desk Overlay" (17 items, 6 assets) into `~/Library/Application Support/InfoPanel/v2`, skipped "USB Panel" (items file missing) with a warning, listened on a random loopback port and opened the default browser on the sign-in link. |
| 2 | Profile thumbnails come from the engine | Done. |
| 3 | Imported profile renders all 14 kinds with mock and simulated values | Done. 14 kinds; sensor status: 2 live (mock), 6 simulated, 2 missing. |
| 4 | Edit a text item, move a bar, undo/redo, save | Done. Text content edited; bar moved (x 400 → 456); undo and redo restored each position; save rewrote `v2/profiles/6f1c2a3b-….xml` with the new text and the items' `<Guid>` elements. |
| 5 | The live frame at scale 1 matches the thumbnail | Done after the M8 fix: 0 of 384,000 pixels differ (800×480). The first run differed by 57 pixels at the edge of a smoothed bar, because thumbnails were drawn without smoothing; a thumbnail taken while a live loop runs is now the loop's frame. |
| 6 | Optional: the export opens in 1.4.x on Windows | Not done (no Windows machine in this session). |
