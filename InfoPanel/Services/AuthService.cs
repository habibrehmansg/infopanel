using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Services;

public static class AuthService
{
    private static readonly ILogger Logger = Log.ForContext(typeof(AuthService));

    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InfoPanel", "auth.bin");

    private static readonly string[] RedirectUris =
    [
        "http://localhost:49636/auth/discord/callback/",
        "http://localhost:49637/auth/discord/callback/",
        "http://localhost:49638/auth/discord/callback/",
    ];

    public static void SaveToken(string token)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(token);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

            var dir = Path.GetDirectoryName(TokenFilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(TokenFilePath, encrypted);

            InfoPanelApiService.Instance.SetAuthToken(token);
            Logger.Information("Auth token saved");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save auth token");
        }
    }

    public static void ClearToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                File.Delete(TokenFilePath);
            }

            InfoPanelApiService.Instance.SetAuthToken(null);
            Logger.Information("Auth token cleared");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear auth token");
        }
    }

    public static string? LoadAndRestoreToken()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
                return null;

            var encrypted = File.ReadAllBytes(TokenFilePath);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var token = System.Text.Encoding.UTF8.GetString(bytes);

            InfoPanelApiService.Instance.SetAuthToken(token);
            Logger.Information("Auth token restored from disk");
            return token;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to restore auth token");
            return null;
        }
    }

    public static async Task<string?> StartOAuthFlowAsync(CancellationToken cancellationToken = default)
    {
        HttpListener? listener = null;
        string? redirectUri = null;

        foreach (var uri in RedirectUris)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(uri);
                listener.Start();
                redirectUri = uri;
                Logger.Information("OAuth listener started on {Uri}", uri);
                break;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Port unavailable for {Uri}, trying next", uri);
                listener?.Close();
                listener = null;
            }
        }

        if (listener == null || redirectUri == null)
        {
            Logger.Error("All OAuth listener ports are unavailable");
            throw new InvalidOperationException("Could not start local OAuth listener. All ports (49636-49638) are in use.");
        }

        try
        {
            // The API expects the redirect_uri without trailing slash
            var apiRedirectUri = new Uri(redirectUri.TrimEnd('/'));
            var authResponse = await InfoPanelApiService.Instance.Client.Get_DiscordAuthAsync(apiRedirectUri, cancellationToken);

            Process.Start(new ProcessStartInfo(authResponse.Url) { UseShellExecute = true });
            Logger.Information("Opened Discord OAuth URL in browser");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);

            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];

            // Respond to browser
            var responseHtml = "<html><body style=\"font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;background:#1a1a2e;color:#e0e0e0\"><div style=\"text-align:center\"><h2>Sign-in successful!</h2><p>You can close this tab and return to InfoPanel.</p></div></body></html>";
            var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, linkedCts.Token);
            context.Response.Close();

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                Logger.Warning("OAuth callback missing code or state");
                return null;
            }

            var callbackResponse = await InfoPanelApiService.Instance.Client.Get_DiscordCallbackAsync(code, state, cancellationToken);

            Logger.Information("OAuth flow completed for user {Username}", callbackResponse.User.Username);
            return callbackResponse.Token;
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
