using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class WebServerTask : BackgroundTask
    {
        private static readonly Lazy<WebServerTask> _instance = new(() => new WebServerTask());

        public static WebServerTask Instance => _instance.Value;

        public static byte[] BitmapToPng(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException(nameof(bitmap), "Bitmap cannot be null");
            }

            using var memoryStream = new MemoryStream();
            // Save the bitmap directly to the memory stream in PNG format
            bitmap.Save(memoryStream, ImageFormat.Png);
            return memoryStream.ToArray();
        }

        protected async override Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var builder = WebApplication.CreateBuilder();
                var _webApplication = builder.Build();

                _webApplication.Use(async (context, next) =>
                {
                    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.Expires = "-1";
                    context.Response.Headers.Vary = "*";

                    await next.Invoke();
                });

                _webApplication.Urls.Add($"http://{ConfigModel.Instance.Settings.WebServerListenIp}:{ConfigModel.Instance.Settings.WebServerListenPort}");

                _webApplication.MapGet("/", async context =>
                {
                    StringBuilder sb = new();

                    for (int i = 0; i < ConfigModel.Instance.Profiles.Count; i++)
                    {
                        sb.AppendLine($"<p><a href='http://{ConfigModel.Instance.Settings.WebServerListenIp}:{ConfigModel.Instance.Settings.WebServerListenPort}/{i}'>{ConfigModel.Instance.Profiles[i].Name}</a></p>");
                    }

                    await context.Response.WriteAsync(sb.ToString());
                });

                _webApplication.MapGet("/{id}", async context =>
                {
                    var filePath = "index.html";
                    var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                    content = content.Replace("{{REFRESH_RATE}}", $"{ConfigModel.Instance.Settings.WebServerRefreshRate}");
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(content);
                });

                _webApplication.MapGet("/{id}/image", async context =>
                {
                    if (int.TryParse(context.Request.RouteValues["id"]?.ToString(), out int id) && id < ConfigModel.Instance.Profiles.Count)
                    {
                        var profile = ConfigModel.Instance.Profiles[id];

                        using var bitmap = PanelDrawTask.Render(profile, false, overrideDpi: true);
                        byte[] buffer = BitmapToPng(bitmap);

                        context.Response.ContentType = "image/png";
                        await context.Response.Body.WriteAsync(buffer);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }
                });

                await _webApplication.RunAsync(token);
            }
            catch (Exception e)
            {
                Trace.WriteLine("WebServerTask: Init error");
            }
            finally
            {

            }
        }
    }
}
