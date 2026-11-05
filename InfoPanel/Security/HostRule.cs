using System.Text;
using InfoPanel.Settings;

namespace InfoPanel.Security;

/// <summary>
/// The engine's host rule (spec/docs/api.md, "Where requests come from"), the first check every request meets after the
/// refusal of requests from the internet (<see cref="HostRuleStartupFilter"/>): its
/// <c>Host</c> must be <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>, or one of the <c>proxy.hosts</c> setting's names
/// (a reverse proxy on this computer), on any port; else 400 with no body. A name matches only whole, in any ASCII case:
/// never a name it ends or starts with, never with a trailing dot. The setting is read at each request, so a change
/// applies from the next one. The header is read as received, never decoded (<c>HttpRequest.Host</c> turns an
/// <c>xn--</c> name into Unicode), so an internationalised name matches in the <c>xn--</c> form browsers send and the
/// setting holds. Only the <c>Host</c> the engine received counts: <c>X-Forwarded-Host</c>, <c>Forwarded</c>
/// and every other header a proxy (or anyone on this computer) may add are never read. ASP.NET's own host filtering is
/// left open (<c>Program</c>), so this rule alone decides. It stops DNS rebinding: a page whose name a browser resolves
/// to this computer still sends that name.
/// </summary>
public sealed class HostRule(Func<IReadOnlyList<string>> proxyHosts)
{
    /// <summary>The names the engine always answers to.</summary>
    public static IReadOnlyList<string> LoopbackNames { get; } = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>The rule over <paramref name="settings"/>' <c>proxy.hosts</c>, read at each request.</summary>
    public HostRule(SettingsStore settings) : this(() => settings.Current.Settings.Proxy.Hosts)
    {
    }

    /// <summary>Whether a request whose <c>Host</c> header is <paramref name="host"/> (as received; null or empty without one) is answered.</summary>
    public bool Allows(string? host)
    {
        var name = new HostString(host ?? "").Host;
        if (string.IsNullOrEmpty(name) || !Ascii.IsValid(name))
            return false;
        return Matches(name, LoopbackNames) || Matches(name, proxyHosts());
    }

    private static bool Matches(string name, IReadOnlyList<string> names)
    {
        foreach (var candidate in names)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Runs ahead of the rest of the pipeline (registered right after the security headers' filter, so its answers carry
/// them), as ASP.NET's host filtering ran: first a request a reverse proxy marks as from the internet
/// (<see cref="FunnelHeader"/>, any value, the name in any case) is 403 <c>problem</c>, whatever its host: the engine
/// answers this computer and the private network a proxy serves it on, never the internet (the proxy sets the header
/// itself and drops it from what clients send); then <see cref="HostRule"/>.
/// </summary>
public sealed class HostRuleStartupFilter : IStartupFilter
{
    /// <summary>The header Tailscale Funnel adds to each request it brings from the internet.</summary>
    public const string FunnelHeader = "Tailscale-Funnel-Request";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var rule = app.ApplicationServices.GetRequiredService<HostRule>();
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.ContainsKey(FunnelHeader))
                return Problems.Write(context, StatusCodes.Status403Forbidden, "Forbidden", "This engine doesn't answer requests from the internet.");
            if (rule.Allows(context.Request.Headers.Host.ToString()))
                return nextMiddleware(context);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        next(app);
    };
}
