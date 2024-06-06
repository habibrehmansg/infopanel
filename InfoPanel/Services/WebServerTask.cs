using InfoPanel.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class WebServerTask
    {
        private static volatile WebServerTask? _instance;
        private static readonly object _lock = new object();

        private CancellationTokenSource? _cts;
        private Task? _task;
        private WebApplication? _webApplication;

        ConcurrentDictionary<Guid, byte[]> _cache = new ConcurrentDictionary<Guid, byte[]>();

        public static WebServerTask Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new WebServerTask();
                    }
                }
                return _instance;
            }
        }

        public byte[] BitmapToPng(Bitmap bitmap)
        {
            // Set the PNG encoding parameters
            var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Png.Guid);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);

            // Encode the bitmap as a PNG byte array
            byte[] pngBytes;
            using (var memoryStream = new MemoryStream())
            {
                bitmap.Save(memoryStream, encoder, encoderParams);
                pngBytes = memoryStream.ToArray();
            }

            return pngBytes;
        }

        public byte[] GetBuffer(Profile profile)
        {
            return _cache[profile.Guid];
        }

        public void UpdateBuffer(Profile profile, Bitmap bitmap)
        {
            byte[] buffer = BitmapToPng(bitmap);
            _cache.AddOrUpdate(profile.Guid, buffer, (key, oldValue) => buffer);
        }

        public bool IsRunning()
        {
            return _cts != null && !_cts.IsCancellationRequested;
        }

        public void Restart()
        {

            if (!IsRunning())
            {
                return;
            }

            if (_task != null)
            {
                Stop();
                while (!_task.IsCompleted)
                {
                    Task.Delay(50).Wait();
                }
            }

            Start();
        }

        public void Start()
        {
            if (_task != null && !_task.IsCompleted) return;
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();
            _webApplication = builder.Build();

            _webApplication.Use(async (context, next) =>
            {
                context.Response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
                context.Response.Headers.Add("Pragma", "no-cache");
                context.Response.Headers.Add("Expires", "-1");
                context.Response.Headers["Vary"] = "*";

                await next.Invoke();
            });

            _webApplication.Urls.Add($"http://{ConfigModel.Instance.Settings.WebServerListenIp}:{ConfigModel.Instance.Settings.WebServerListenPort}");
            _webApplication.MapGet("/", async context => {
                StringBuilder sb = new StringBuilder();
                
                for(int i = 0; i < ConfigModel.Instance.Profiles.Count; i++)
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
                int id = 0;
                _ = int.TryParse(context.Request.RouteValues["id"]?.ToString(), out id);
              
                if(id >= ConfigModel.Instance.Profiles.Count)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var profile = ConfigModel.Instance.Profiles[id];
                
                byte[]? buffer = null;
                if (profile.Active
                    || (ConfigModel.Instance.Settings.BeadaPanel && ConfigModel.Instance.Settings.BeadaPanelProfile == profile.Guid)
                    || ConfigModel.Instance.Settings.TuringPanelA && ConfigModel.Instance.Settings.TuringPanelAProfile == profile.Guid
                    || ConfigModel.Instance.Settings.TuringPanelC && ConfigModel.Instance.Settings.TuringPanelCProfile == profile.Guid)
                {
                    buffer = WebServerTask.Instance.GetBuffer(profile);
                } else
                {
                    var lockedBitmap = PanelDrawTask.Render(profile, 0, 0, false);

                    lockedBitmap.Access(bitmap =>
                    {
                        buffer = BitmapToPng(bitmap);
                        _cache.AddOrUpdate(profile.Guid, buffer, (key, oldValue) => buffer);
                    });
                }


                if (buffer != null)
                {
                    context.Response.ContentType = "image/png";
                    await context.Response.Body.WriteAsync(buffer);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    return;
                }


            });
            
            _task = _webApplication.RunAsync();
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _webApplication?.StopAsync();
            }
        }

    }
}
