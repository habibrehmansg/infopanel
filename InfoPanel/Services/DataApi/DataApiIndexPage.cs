using InfoPanel.Monitors;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace InfoPanel.DataApi
{
    /// <summary>
    /// The HTML page served at the Data API's root: every data point, grouped
    /// by source (system, each plugin and container, LibreHardwareMonitor and
    /// HWiNFO by device, external pushes by source), with its key and current
    /// value, plus the endpoint list.
    /// </summary>
    internal static class DataApiIndexPage
    {
        private sealed record Row(string Key, string Name, DataApiValue? Value);
        private sealed record Group(string Title, string? Note, List<Row> Rows);
        private sealed record Section(string Title, List<Group> Groups);

        public static string Render(DataApiSnapshot snapshot, IEnumerable<(string Method, string Path, string Description)> endpoints)
        {
            var sensors = snapshot.Sensors;
            DataApiValue? Get(string key) => sensors.TryGetValue(key, out var v) ? v : null;

            var sections = new List<Section>
            {
                new("System", [new Group("Windows", null,
                    sensors.Where(s => s.Key.StartsWith("system.", StringComparison.Ordinal))
                        .OrderBy(s => s.Key, StringComparer.Ordinal)
                        .Select(s => new Row(s.Key, s.Value.Name ?? s.Key, s.Value)).ToList())]),

                new("Plugins", PluginMonitor.GetOrderedList()
                    .GroupBy(r => r.PluginName)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(plugin => plugin
                        .GroupBy(r => SensorSnapshotBuilder.IsImageContainer(r.ContainerId) ? "Images" : r.ContainerName)
                        .Select(container => new Group(
                            $"{plugin.Key} · {container.Key}",
                            plugin.First().PluginId,
                            container.Select(r =>
                            {
                                var key = SensorSnapshotBuilder.PluginKey(r.Id);
                                return new Row(key, r.Name, Get(key));
                            }).ToList())))
                    .ToList()),

                new("LibreHardwareMonitor", SensorSnapshotBuilder.LibreKeys()
                    .GroupBy(l => SensorSnapshotBuilder.CleanName(l.Sensor.Hardware.Name))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new Group(g.Key, null,
                        g.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new Row(l.Key, l.Name, Get(l.Key))).ToList()))
                    .ToList()),

                new("HWiNFO", SensorSnapshotBuilder.HwInfoKeys()
                    .GroupBy(h => (h.Reading.RemoteIndex >= 0 ? $"Remote {h.Reading.RemoteIndex}: " : "") + SensorSnapshotBuilder.HwInfoDevice(h.Reading))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new Group(g.Key, null,
                        g.OrderBy(h => h.Reading.IndexOrder).Select(h => new Row(h.Key, h.Name, Get(h.Key))).ToList()))
                    .ToList()),

                new("Pushed", sensors
                    .Where(s => s.Value.Source is { } src && src != "infopanel" && src != "system")
                    .GroupBy(s => s.Value.Source!, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new Group(g.Key,
                        snapshot.Sources.TryGetValue(g.Key, out var status)
                            ? $"last push {DateTimeOffset.FromUnixTimeMilliseconds(status.LastSeen).ToLocalTime():HH:mm:ss}"
                            : null,
                        g.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new Row(s.Key, s.Value.Name ?? s.Key, s.Value)).ToList()))
                    .ToList()),
            };

            var sb = new StringBuilder();
            sb.Append("""
                <!doctype html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>InfoPanel Data API</title>
                <style>
                :root { --bg:#f7f7f8; --fg:#1d1d1f; --muted:#6b6b72; --card:#fff; --line:#e3e3e8; --accent:#2563eb; }
                @media (prefers-color-scheme: dark) { :root { --bg:#141417; --fg:#ececf0; --muted:#9a9aa3; --card:#1d1d22; --line:#2c2c33; --accent:#7aa2ff; } }
                * { box-sizing: border-box; }
                body { margin:0; padding:24px 16px 48px; background:var(--bg); color:var(--fg); font:14px/1.45 system-ui, "Segoe UI", sans-serif; }
                main { max-width:1100px; margin:0 auto; }
                h1 { font-size:22px; margin:0 0 4px; }
                h2 { font-size:16px; margin:28px 0 8px; }
                .muted { color:var(--muted); }
                code { font:12px/1.4 ui-monospace, "Cascadia Mono", Consolas, monospace; }
                a { color:var(--accent); }
                table { width:100%; border-collapse:collapse; }
                th, td { text-align:left; padding:5px 8px; border-top:1px solid var(--line); vertical-align:top; }
                th { font-weight:600; color:var(--muted); font-size:12px; }
                td.key { width:42%; word-break:break-all; }
                td.val { white-space:pre-wrap; word-break:break-word; }
                details { background:var(--card); border:1px solid var(--line); border-radius:8px; margin:6px 0; }
                summary { cursor:pointer; padding:8px 12px; font-weight:600; }
                summary .muted { font-weight:400; }
                details > table { margin-bottom:4px; }
                #filter { width:100%; max-width:420px; padding:7px 10px; margin:16px 0 4px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--fg); font:inherit; }
                .endpoints td:first-child { width:60px; }
                </style>
                </head>
                <body><main>
                <h1>InfoPanel Data API</h1>
                """);
            int total = sections.Sum(s => s.Groups.Sum(g => g.Rows.Count));
            sb.Append(CultureInfo.InvariantCulture,
                $"<p class=\"muted\">{total} data points, read {DateTime.Now:HH:mm:ss}. Reload for current values; <a href=\"/api/sensors\">/api/sensors</a> and <a href=\"/events\">/events</a> serve them as JSON.</p>");

            sb.Append("<h2>Endpoints</h2><details><summary>").Append(endpoints.Count()).Append(" endpoints</summary><table class=\"endpoints\">");
            foreach (var (method, path, description) in endpoints)
            {
                sb.Append("<tr><td><code>").Append(Html(method)).Append("</code></td><td><code>").Append(Html(path))
                  .Append("</code></td><td>").Append(Html(description)).Append("</td></tr>");
            }
            sb.Append("</table></details>");

            sb.Append("<input id=\"filter\" type=\"search\" placeholder=\"Filter by key, name or value\" autofocus>");

            foreach (var section in sections)
            {
                int count = section.Groups.Sum(g => g.Rows.Count);
                sb.Append("<section><h2>").Append(Html(section.Title)).Append(" <span class=\"muted\">").Append(count).Append("</span></h2>");
                if (count == 0)
                {
                    sb.Append("<p class=\"muted\">No data.</p>");
                }
                foreach (var group in section.Groups.Where(g => g.Rows.Count > 0))
                {
                    sb.Append("<details><summary>").Append(Html(group.Title)).Append(" <span class=\"muted\">")
                      .Append(group.Rows.Count);
                    if (group.Note != null) sb.Append(" · ").Append(Html(group.Note));
                    sb.Append("</span></summary><table><tr><th>Key</th><th>Name</th><th>Value</th></tr>");
                    foreach (var row in group.Rows)
                    {
                        sb.Append("<tr><td class=\"key\"><code>").Append(Html(row.Key)).Append("</code></td><td>")
                          .Append(Html(row.Name)).Append("</td><td class=\"val\">").Append(FormatValue(row.Value)).Append("</td></tr>");
                    }
                    sb.Append("</table></details>");
                }
                sb.Append("</section>");
            }

            sb.Append("""
                <script>
                const filter = document.getElementById("filter");
                filter.addEventListener("input", () => {
                  const q = filter.value.trim().toLowerCase();
                  for (const details of document.querySelectorAll("section details")) {
                    let shown = 0;
                    for (const row of details.querySelectorAll("tr:not(:first-child)")) {
                      const hit = !q || row.textContent.toLowerCase().includes(q);
                      row.hidden = !hit;
                      if (hit) shown++;
                    }
                    details.hidden = shown === 0;
                    details.open = !!q && shown > 0;
                  }
                });
                </script>
                </main></body></html>
                """);
            return sb.ToString();
        }

        private static string FormatValue(DataApiValue? value)
        {
            if (value == null) return "<span class=\"muted\">—</span>";

            if (value.Text != null)
            {
                string text = value.Text;
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return $"<a href=\"{Html(text)}\" target=\"_blank\" rel=\"noreferrer\">{Html(Truncate(text, 80))}</a>";
                }
                return Html(Truncate(text, 400));
            }

            if (value.Value is double number)
            {
                string unit = string.IsNullOrEmpty(value.Unit) ? "" : $" {value.Unit}";
                return Html(number.ToString("0.##", CultureInfo.InvariantCulture) + unit);
            }

            return "<span class=\"muted\">—</span>";
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

        private static string Html(string? text) => WebUtility.HtmlEncode(text ?? "");
    }
}
