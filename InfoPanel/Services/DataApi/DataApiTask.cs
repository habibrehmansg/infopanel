using InfoPanel.DataApi;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    /// <summary>
    /// The Data API: a local HTTP + Server-Sent Events API that serves every
    /// sensor value InfoPanel knows (all plugins, LibreHardwareMonitor,
    /// HWiNFO, system load) plus values external processes push to it.
    /// Off by default; configured under Settings → Data API.
    /// </summary>
    public sealed class DataApiTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<DataApiTask>();
        private static readonly Lazy<DataApiTask> _instance = new(() => new DataApiTask());
        public static DataApiTask Instance => _instance.Value;

        private const int MaxPushBytes = 5 * 1024 * 1024;
        private static readonly TimeSpan EventInterval = TimeSpan.FromSeconds(1);

        // Sources the API produces itself; a replace push may not claim them.
        private static readonly HashSet<string> ReservedSources = new(StringComparer.OrdinalIgnoreCase) { "infopanel", "system" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly PushStore _pushStore = new();
        private readonly SensorSnapshotBuilder _snapshots;

        private DataApiTask()
        {
            _snapshots = new SensorSnapshotBuilder(_pushStore);
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var settings = ConfigModel.Instance.Settings;
            string baseUrl = $"http://{settings.DataApiListenIp}:{settings.DataApiListenPort}";

            try
            {
                var builder = WebApplication.CreateBuilder();
                var app = builder.Build();
                app.Urls.Add(baseUrl);
                // Pushes are only accepted from this machine, so it must be
                // able to reach the API on loopback when it listens on a LAN address.
                if (IPAddress.TryParse(settings.DataApiListenIp, out var listenIp) && !IPAddress.IsLoopback(listenIp))
                {
                    app.Urls.Add($"http://127.0.0.1:{settings.DataApiListenPort}");
                }

                // Dashboards are served from their own origin.
                app.Use(async (context, next) =>
                {
                    context.Response.Headers.AccessControlAllowOrigin = "*";
                    context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                    context.Response.Headers.CacheControl = "no-store";
                    if (HttpMethods.IsOptions(context.Request.Method))
                    {
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                        return;
                    }
                    await next(context);
                });

                // Browsers get a readable list of every data point; /api is the JSON index.
                app.MapGet("/", () => Results.Content(
                    DataApiIndexPage.Render(_snapshots.Get(baseUrl), Endpoints),
                    "text/html; charset=utf-8"));
                app.MapGet("/api", () => Results.Json(new
                {
                    name = "InfoPanel Data API",
                    endpoints = Endpoints.Select(e => new { method = e.Method, path = e.Path, description = e.Description }),
                }, JsonOptions));
                app.MapGet("/api/sensors", () => Results.Json(_snapshots.Get(baseUrl), JsonOptions));
                app.MapGet("/events", context => StreamEventsAsync(context, baseUrl));
                app.MapGet("/api/sources", () => Results.Json(Sources(), JsonOptions));
                app.MapGet("/api/images/{pluginId}/{imageId}", ServeImage);
                app.MapPost("/api/push", (Func<HttpContext, Task<IResult>>)PushAsync);
                app.MapPost("/api/clear", (HttpContext context) =>
                {
                    if (!IsLocal(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                    _pushStore.Clear();
                    _snapshots.Invalidate();
                    return Results.Json(new { ok = true }, JsonOptions);
                });

                Logger.Information("Data API listening on {Url}", baseUrl);
                await app.RunAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Stopped from settings or on shutdown.
            }
            catch (Exception e)
            {
                Logger.Error(e, "Data API failed to start on {Url}", baseUrl);
            }
        }

        private static readonly (string Method, string Path, string Description)[] Endpoints =
        [
            ("GET",  "/",            "This page: every data point, grouped by source"),
            ("GET",  "/api",         "Endpoint list as JSON"),
            ("GET",  "/api/sensors", "Full sensor snapshot"),
            ("GET",  "/events",      "Server-Sent Events: the full snapshot every second"),
            ("GET",  "/api/sources", "Plugins, containers and entries with their names and keys"),
            ("GET",  "/api/images/{pluginId}/{imageId}", "A plugin image as PNG"),
            ("POST", "/api/push",    "Add sensor values from another process (local machine only)"),
            ("POST", "/api/clear",   "Clear all pushed values (local machine only)"),
        ];

        private async Task StreamEventsAsync(HttpContext context, string baseUrl)
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers.Connection = "keep-alive";
            var token = context.RequestAborted;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var json = JsonSerializer.Serialize(_snapshots.Get(baseUrl), JsonOptions);
                    await response.WriteAsync($"data: {json}\n\n", token);
                    await response.Body.FlushAsync(token);
                    await Task.Delay(EventInterval, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
        }

        private async Task<IResult> PushAsync(HttpContext context)
        {
            // Reading is fine from any listen address, but only the local
            // machine may add data.
            if (!IsLocal(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.ContentLength > MaxPushBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest($"Invalid JSON: {ex.Message}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Results.BadRequest("expected an object");

                string source = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()?.Trim() ?? ""
                    : "";
                bool replace = root.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.True;

                if (source.Length == 0) return Results.BadRequest("source is required");
                if (ReservedSources.Contains(source)) return Results.BadRequest($"source \"{source}\" is reserved");
                if (!root.TryGetProperty("sensors", out var sensorsProp) || sensorsProp.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest("sensors must be an object");

                var values = new Dictionary<string, DataApiValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in sensorsProp.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                    values[prop.Name] = ParsePushedValue(prop.Value, source);
                }

                int removed = _pushStore.Apply(source, values, replace);
                _snapshots.Invalidate();
                return Results.Json(new { ok = true, count = values.Count, removed }, JsonOptions);
            }
        }

        /// <summary>
        /// A pushed value: an object with value/text/unit/min/max/avg/name, or
        /// a bare number or string.
        /// </summary>
        private static DataApiValue ParsePushedValue(JsonElement element, string source)
        {
            static double? Number(JsonElement obj, string name) =>
                obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            static string? Text(JsonElement obj, string name) =>
                obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            return element.ValueKind switch
            {
                JsonValueKind.Object => new DataApiValue
                {
                    Value = Number(element, "value"),
                    Text = Text(element, "text"),
                    Unit = Text(element, "unit") ?? "",
                    Min = Number(element, "min"),
                    Max = Number(element, "max"),
                    Avg = Number(element, "avg"),
                    Name = Text(element, "name"),
                    Source = source,
                },
                JsonValueKind.Number => new DataApiValue { Value = element.GetDouble(), Source = source },
                JsonValueKind.String => new DataApiValue { Text = element.GetString(), Source = source },
                _ => new DataApiValue { Source = source },
            };
        }

        private static bool IsLocal(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

        /// <summary>The plugin → container → entry tree, with the key each entry has in /api/sensors.</summary>
        private static object Sources()
        {
            var plugins = PluginMonitor.GetOrderedList()
                .Where(r => !r.ContainerId.StartsWith("__images_", StringComparison.Ordinal))
                .GroupBy(r => (r.PluginId, r.PluginName))
                .Select(plugin => new
                {
                    id = plugin.Key.PluginId,
                    name = plugin.Key.PluginName,
                    containers = plugin
                        .GroupBy(r => (r.ContainerId, r.ContainerName))
                        .Select(container => new
                        {
                            id = container.Key.ContainerId,
                            name = container.Key.ContainerName,
                            entries = container.Select(r => new
                            {
                                id = r.Data?.Id ?? r.Name,
                                name = r.Name,
                                type = r.Data switch
                                {
                                    IPluginSensor => "sensor",
                                    IPluginTable => "table",
                                    _ => "text",
                                },
                                key = SensorSnapshotBuilder.PluginKey(r.Id),
                            }),
                        }),
                    images = PluginMonitor.Instance.GetAllImageProxies()
                        .Where(i => i.PluginId == plugin.Key.PluginId)
                        .Select(i => new { id = i.ImageId, name = i.ImageName, path = $"/api/images/{i.PluginId}/{i.ImageId}" }),
                });

            return new
            {
                plugins,
                hardware = new
                {
                    libre = LibreMonitor.SENSORHASH.Count,
                    hwinfo = HWHash.SENSORHASH.Count,
                },
            };
        }

        private static IResult ServeImage(string pluginId, string imageId)
        {
            var proxy = PluginMonitor.Instance.GetImageProxy(pluginId, imageId);
            using var frame = proxy?.GetCurrentFrame();
            if (frame == null) return Results.NotFound();

            using var png = frame.Encode(SKEncodedImageFormat.Png, 100);
            return Results.Bytes(png.ToArray(), "image/png");
        }
    }
}
