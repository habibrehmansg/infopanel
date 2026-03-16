using System;
using System.Net.Http;
using InfoPanel.ApiClient;
using Serilog;

namespace InfoPanel.Services;

public sealed class InfoPanelApiService
{
    private static readonly Lazy<InfoPanelApiService> _instance = new(() => new InfoPanelApiService());
    private static readonly ILogger Logger = Log.ForContext<InfoPanelApiService>();

    private const string BaseUrl = "https://api.infopanel.net";

    private readonly HttpClient _httpClient;
    private readonly InfoPanelApiClient _client;

    public static InfoPanelApiService Instance => _instance.Value;

    public IInfoPanelApiClient Client => _client;

    private InfoPanelApiService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
        _client = new InfoPanelApiClient(_httpClient);
    }

    public void SetAuthToken(string? token)
    {
        if (token != null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Logger.Debug("API auth token set");
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            Logger.Debug("API auth token cleared");
        }
    }
}
