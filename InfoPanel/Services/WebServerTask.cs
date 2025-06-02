using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SkiaSharp;
using System;
using System.Diagnostics;
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

                    // Add HTML structure with Windows Mica-inspired styling
                    sb.AppendLine("<!DOCTYPE html>");
                    sb.AppendLine("<html lang='en'>");
                    sb.AppendLine("<head>");
                    sb.AppendLine("    <meta charset='UTF-8'>");
                    sb.AppendLine("    <meta name='viewport' content='width=device-width, initial-scale=1.0'>");
                    sb.AppendLine("    <title>InfoPanel Remote Sensor</title>");
                    sb.AppendLine("    <style>");
                    sb.AppendLine("        * { margin: 0; padding: 0; box-sizing: border-box; }");
                    sb.AppendLine("        :root {");
                    sb.AppendLine("            --mica-bg: rgba(243, 243, 243, 0.9);");
                    sb.AppendLine("            --mica-bg-dark: rgba(32, 32, 32, 0.9);");
                    sb.AppendLine("            --surface-bg: rgba(255, 255, 255, 0.7);");
                    sb.AppendLine("            --surface-bg-hover: rgba(255, 255, 255, 0.85);");
                    sb.AppendLine("            --text-primary: #323232;");
                    sb.AppendLine("            --text-secondary: #656565;");
                    sb.AppendLine("            --accent: #0078D4;");
                    sb.AppendLine("            --accent-hover: #106EBE;");
                    sb.AppendLine("            --border-subtle: rgba(0, 0, 0, 0.0578);");
                    sb.AppendLine("            --shadow-subtle: 0 2px 4px rgba(0, 0, 0, 0.04), 0 1px 2px rgba(0, 0, 0, 0.08);");
                    sb.AppendLine("            --shadow-elevated: 0 8px 16px rgba(0, 0, 0, 0.08), 0 4px 8px rgba(0, 0, 0, 0.04);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        body {");
                    sb.AppendLine("            font-family: 'Segoe UI Variable Display', 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;");
                    sb.AppendLine("            background: linear-gradient(180deg, #f3f3f3 0%, #e7e7e7 100%);");
                    sb.AppendLine("            min-height: 100vh;");
                    sb.AppendLine("            color: var(--text-primary);");
                    sb.AppendLine("            backdrop-filter: blur(60px);");
                    sb.AppendLine("            -webkit-backdrop-filter: blur(60px);");
                    sb.AppendLine("            margin: 0;");
                    sb.AppendLine("            padding: 20px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .container {");
                    sb.AppendLine("            max-width: 1200px;");
                    sb.AppendLine("            margin: 0 auto;");
                    sb.AppendLine("            padding: 24px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .header {");
                    sb.AppendLine("            background: var(--surface-bg);");
                    sb.AppendLine("            backdrop-filter: blur(20px);");
                    sb.AppendLine("            -webkit-backdrop-filter: blur(20px);");
                    sb.AppendLine("            border: 1px solid var(--border-subtle);");
                    sb.AppendLine("            border-radius: 8px;");
                    sb.AppendLine("            padding: 32px;");
                    sb.AppendLine("            margin-bottom: 24px;");
                    sb.AppendLine("            box-shadow: var(--shadow-subtle);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .logo {");
                    sb.AppendLine("            display: flex;");
                    sb.AppendLine("            align-items: center;");
                    sb.AppendLine("            gap: 16px;");
                    sb.AppendLine("            margin-bottom: 8px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .logo-icon {");
                    sb.AppendLine("            width: 48px;");
                    sb.AppendLine("            height: 48px;");
                    sb.AppendLine("            background: var(--accent);");
                    sb.AppendLine("            border-radius: 8px;");
                    sb.AppendLine("            display: flex;");
                    sb.AppendLine("            align-items: center;");
                    sb.AppendLine("            justify-content: center;");
                    sb.AppendLine("            color: white;");
                    sb.AppendLine("            font-weight: 600;");
                    sb.AppendLine("            font-size: 24px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        h1 {");
                    sb.AppendLine("            font-size: 32px;");
                    sb.AppendLine("            font-weight: 600;");
                    sb.AppendLine("            color: var(--text-primary);");
                    sb.AppendLine("            margin: 0;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .subtitle {");
                    sb.AppendLine("            color: var(--text-secondary);");
                    sb.AppendLine("            font-size: 14px;");
                    sb.AppendLine("            margin-top: 4px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profiles-grid {");
                    sb.AppendLine("            display: grid;");
                    sb.AppendLine("            grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));");
                    sb.AppendLine("            gap: 16px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-card {");
                    sb.AppendLine("            background: var(--surface-bg);");
                    sb.AppendLine("            backdrop-filter: blur(20px);");
                    sb.AppendLine("            -webkit-backdrop-filter: blur(20px);");
                    sb.AppendLine("            border: 1px solid var(--border-subtle);");
                    sb.AppendLine("            border-radius: 8px;");
                    sb.AppendLine("            padding: 24px;");
                    sb.AppendLine("            transition: all 0.2s ease;");
                    sb.AppendLine("            cursor: pointer;");
                    sb.AppendLine("            text-decoration: none;");
                    sb.AppendLine("            color: inherit;");
                    sb.AppendLine("            display: block;");
                    sb.AppendLine("            box-shadow: var(--shadow-subtle);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-card:hover {");
                    sb.AppendLine("            background: var(--surface-bg-hover);");
                    sb.AppendLine("            transform: translateY(-2px);");
                    sb.AppendLine("            box-shadow: var(--shadow-elevated);");
                    sb.AppendLine("            border-color: var(--accent);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-header {");
                    sb.AppendLine("            display: flex;");
                    sb.AppendLine("            align-items: center;");
                    sb.AppendLine("            gap: 16px;");
                    sb.AppendLine("            margin-bottom: 16px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-icon {");
                    sb.AppendLine("            width: 40px;");
                    sb.AppendLine("            height: 40px;");
                    sb.AppendLine("            background: var(--accent);");
                    sb.AppendLine("            border-radius: 6px;");
                    sb.AppendLine("            display: flex;");
                    sb.AppendLine("            align-items: center;");
                    sb.AppendLine("            justify-content: center;");
                    sb.AppendLine("            color: white;");
                    sb.AppendLine("            font-weight: 600;");
                    sb.AppendLine("            flex-shrink: 0;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-title {");
                    sb.AppendLine("            font-size: 18px;");
                    sb.AppendLine("            font-weight: 600;");
                    sb.AppendLine("            color: var(--text-primary);");
                    sb.AppendLine("            line-height: 1.2;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .profile-details {");
                    sb.AppendLine("            display: grid;");
                    sb.AppendLine("            grid-template-columns: repeat(2, 1fr);");
                    sb.AppendLine("            gap: 12px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .detail-item {");
                    sb.AppendLine("            background: rgba(0, 0, 0, 0.03);");
                    sb.AppendLine("            border-radius: 6px;");
                    sb.AppendLine("            padding: 12px;");
                    sb.AppendLine("            text-align: center;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .detail-label {");
                    sb.AppendLine("            font-size: 12px;");
                    sb.AppendLine("            color: var(--text-secondary);");
                    sb.AppendLine("            text-transform: uppercase;");
                    sb.AppendLine("            letter-spacing: 0.5px;");
                    sb.AppendLine("            margin-bottom: 4px;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .detail-value {");
                    sb.AppendLine("            font-size: 20px;");
                    sb.AppendLine("            font-weight: 600;");
                    sb.AppendLine("            color: var(--accent);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .empty-state {");
                    sb.AppendLine("            background: var(--surface-bg);");
                    sb.AppendLine("            backdrop-filter: blur(20px);");
                    sb.AppendLine("            -webkit-backdrop-filter: blur(20px);");
                    sb.AppendLine("            border: 1px solid var(--border-subtle);");
                    sb.AppendLine("            border-radius: 8px;");
                    sb.AppendLine("            padding: 48px;");
                    sb.AppendLine("            text-align: center;");
                    sb.AppendLine("            box-shadow: var(--shadow-subtle);");
                    sb.AppendLine("        }");
                    sb.AppendLine("        .empty-icon {");
                    sb.AppendLine("            width: 64px;");
                    sb.AppendLine("            height: 64px;");
                    sb.AppendLine("            margin: 0 auto 16px;");
                    sb.AppendLine("            opacity: 0.5;");
                    sb.AppendLine("        }");
                    sb.AppendLine("        @media (prefers-color-scheme: dark) {");
                    sb.AppendLine("            :root {");
                    sb.AppendLine("                --mica-bg: var(--mica-bg-dark);");
                    sb.AppendLine("                --surface-bg: rgba(48, 48, 48, 0.7);");
                    sb.AppendLine("                --surface-bg-hover: rgba(60, 60, 60, 0.85);");
                    sb.AppendLine("                --text-primary: #ffffff;");
                    sb.AppendLine("                --text-secondary: #c5c5c5;");
                    sb.AppendLine("                --border-subtle: rgba(255, 255, 255, 0.0837);");
                    sb.AppendLine("            }");
                    sb.AppendLine("            body {");
                    sb.AppendLine("                background: linear-gradient(180deg, #202020 0%, #181818 100%);");
                    sb.AppendLine("            }");
                    sb.AppendLine("            .detail-item {");
                    sb.AppendLine("                background: rgba(255, 255, 255, 0.05);");
                    sb.AppendLine("            }");
                    sb.AppendLine("        }");
                    sb.AppendLine("    </style>");
                    sb.AppendLine("</head>");
                    sb.AppendLine("<body>");
                    sb.AppendLine("    <div class='container'>");
                    sb.AppendLine("        <div class='header'>");
                    sb.AppendLine("                    <h1>InfoPanel Remote Sensor</h1>");
                    sb.AppendLine("                </div>");
                    sb.AppendLine("            </div>");
                    sb.AppendLine("        </div>");

                    if (ConfigModel.Instance.Profiles.Count == 0)
                    {
                        sb.AppendLine("        <div class='empty-state'>");
                        sb.AppendLine("            <svg class='empty-icon' xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 24 24' stroke='currentColor'>");
                        sb.AppendLine("                <path stroke-linecap='round' stroke-linejoin='round' stroke-width='1.5' d='M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z' />");
                        sb.AppendLine("            </svg>");
                        sb.AppendLine("            <h2 style='font-size: 24px; margin-bottom: 8px;'>No Profiles Available</h2>");
                        sb.AppendLine("            <p style='color: var(--text-secondary);'>There are currently no display profiles configured.</p>");
                        sb.AppendLine("        </div>");
                    }
                    else
                    {
                        sb.AppendLine("        <div class='profiles-grid'>");

                        for (int i = 0; i < ConfigModel.Instance.Profiles.Count; i++)
                        {
                            var profile = ConfigModel.Instance.Profiles[i];
                            var profileUrl = $"http://{ConfigModel.Instance.Settings.WebServerListenIp}:{ConfigModel.Instance.Settings.WebServerListenPort}/{i}";

                            sb.AppendLine($"        <a href='{profileUrl}' class='profile-card'>");
                            sb.AppendLine($"            <div class='profile-header'>");
                            sb.AppendLine($"                <div class='profile-icon'>{i + 1}</div>");
                            sb.AppendLine($"                <div class='profile-title'>{System.Web.HttpUtility.HtmlEncode(profile.Name)}</div>");
                            sb.AppendLine($"            </div>");
                            sb.AppendLine($"            <div class='profile-details'>");
                            sb.AppendLine($"                <div class='detail-item'>");
                            sb.AppendLine($"                    <div class='detail-label'>Width</div>");
                            sb.AppendLine($"                    <div class='detail-value'>{profile.Width}px</div>");
                            sb.AppendLine($"                </div>");
                            sb.AppendLine($"                <div class='detail-item'>");
                            sb.AppendLine($"                    <div class='detail-label'>Height</div>");
                            sb.AppendLine($"                    <div class='detail-value'>{profile.Height}px</div>");
                            sb.AppendLine($"                </div>");
                            sb.AppendLine($"            </div>");
                            sb.AppendLine($"        </a>");
                        }

                        sb.AppendLine("        </div>");
                    }

                    sb.AppendLine("    </div>");
                    sb.AppendLine("</body>");
                    sb.AppendLine("</html>");

                    context.Response.ContentType = "text/html; charset=utf-8";
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

                        using var bitmap = PanelDrawTask.RenderSK(profile, false);
                        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

                        context.Response.ContentType = "image/png";
                        await context.Response.Body.WriteAsync(data.ToArray());
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
