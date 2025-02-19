using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace InfoPanel.Extras
{
    /// <summary>
    /// InfoPanel Spotify Plugin - v.1.0.1-beta
    ///
    /// A plugin to display current Spotify track information in the InfoPanel application.
    /// </summary>
    public class SpotifyPlugin : BasePlugin
    {
        // PluginText objects for displaying track information
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
        private readonly PluginText _artist = new("artist", "Artist", "-");
        private readonly PluginText _coverArt = new("cover-art", "Cover Art", "-");

        // PluginText objects for displaying time information
        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00");
        private readonly PluginText _remainingTime = new(
            "remaining-time",
            "Remaining Time",
            "00:00"
        );

        // Spotify API client and authentication components
        private SpotifyClient? _spotifyClient;
        private string? _verifier;
        private EmbedIOAuthServer? _server;
        private string? _apiKey;
        private string? _configFilePath;
        private string? _refreshToken;

        // Rate limiter to prevent exceeding API call limits
        private readonly RateLimiter _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1)); // Adjusted to 180 requests per minute

        private static readonly object _fileLock = new object();

        public SpotifyPlugin()
            : base(
                "spotify-plugin",
                "Spotify Info",
                "Displays the current Spotify track information."
            ) { }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        /// <summary>
        /// Initializes the plugin by setting up authentication and loading configuration.
        /// </summary>
        public override void Initialize()
        {
            Debug.WriteLine("Initialize called");

            // Determine the configuration file path
            Assembly assembly = Assembly.GetExecutingAssembly();
            _configFilePath = $"{assembly.ManifestModule.FullyQualifiedName}.ini";

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
                // If config file does not exist, create it with default values
                config = new IniData();
                config["Spotify Plugin"]["APIKey"] = "<your-spotify-api-key>";
                parser.WriteFile(_configFilePath, config);
            }
            else
            {
                try
                {
                    config = parser.ReadFile(_configFilePath);

                    _apiKey = config["Spotify Plugin"]["APIKey"];
                    _refreshToken = config["Spotify Plugin"]["RefreshToken"];

                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        // Try to refresh the token if it exists, otherwise start authentication
                        if (string.IsNullOrEmpty(_refreshToken) || !TryRefreshTokenAsync().Result)
                        {
                            StartAuthentication();
                        }
                    }
                    else
                    {
                        Debug.WriteLine("Spotify API Key is not set or is invalid.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading config file: {ex.Message}");
                    StartAuthentication(); // If config read fails, start authentication
                }
            }

            // Initialize the container with default values
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange(
                [_currentTrack, _artist, _coverArt, _elapsedTime, _remainingTime]
            );
            Load([container]);
        }

        /// <summary>
        /// Tries to refresh the Spotify token asynchronously.
        /// </summary>
        /// <returns>True if the token was refreshed successfully, false otherwise.</returns>
        private async Task<bool> TryRefreshTokenAsync()
        {
            if (_refreshToken == null || _apiKey == null)
            {
                Debug.WriteLine("Refresh token or API key missing.");
                return false;
            }

            try
            {
                var response = await new OAuthClient().RequestToken(
                    new PKCETokenRefreshRequest(_apiKey, _refreshToken)
                );

                var authenticator = new PKCEAuthenticator(_apiKey, response);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

                _spotifyClient = new SpotifyClient(config);

                Debug.WriteLine("Successfully refreshed token.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing token: {ex.Message}");
                _refreshToken = null; // Clear the refresh token if it fails
                return false;
            }
        }

        /// <summary>
        /// Starts the authentication process for Spotify.
        /// </summary>
        private void StartAuthentication()
        {
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes();
                _verifier = verifier;

                _server = new EmbedIOAuthServer(new Uri("http://localhost:5000/callback"), 5000);
                _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                _server.Start();

                if (_apiKey == null)
                {
                    HandleError("API Key missing");
                    return;
                }

                var loginRequest = new LoginRequest(
                    _server.BaseUri,
                    _apiKey,
                    LoginRequest.ResponseType.Code
                )
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] { Scopes.UserReadPlaybackState, Scopes.UserReadCurrentlyPlaying },
                };
                var uri = loginRequest.ToUri();

                Debug.WriteLine($"Authentication URI: {uri}");
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uri.ToString(),
                        UseShellExecute = true,
                    }
                );

                Debug.WriteLine("Authentication process started.");
            }
            catch (Exception ex)
            {
                HandleError($"Error starting authentication: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler for when authorization code is received from Spotify.
        /// </summary>
        private async Task OnAuthorizationCodeReceived(
            object sender,
            AuthorizationCodeResponse response
        )
        {
            if (_verifier == null || _apiKey == null)
            {
                HandleError("Authentication setup error");
                return;
            }

            try
            {
                var initialResponse = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_apiKey, response.Code, _server!.BaseUri, _verifier)
                );

                Debug.WriteLine($"Received access token: {initialResponse.AccessToken}");
                if (!string.IsNullOrEmpty(initialResponse.RefreshToken))
                {
                    _refreshToken = initialResponse.RefreshToken;
                    SaveRefreshToken(_refreshToken);
                    Debug.WriteLine("Refresh token saved.");
                }
                else
                {
                    Debug.WriteLine("Warning: No refresh token received.");
                }

                var authenticator = new PKCEAuthenticator(_apiKey, initialResponse);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

                _spotifyClient = new SpotifyClient(config);

                await _server.Stop();
                Debug.WriteLine("Authentication completed successfully.");
            }
            catch (APIException apiEx)
            {
                HandleError("API authentication error");
                if (apiEx.Response != null && Debugger.IsAttached) // Only log detailed error if debugger is attached
                {
                    Debug.WriteLine($"API Response Error: {apiEx.Message}");
                }
            }
            catch (Exception ex)
            {
                HandleError($"Authentication failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the refresh token to the configuration file for future use.
        /// </summary>
        private void SaveRefreshToken(string token)
        {
            try
            {
                var parser = new FileIniDataParser();
                var config = parser.ReadFile(_configFilePath);
                config["Spotify Plugin"]["RefreshToken"] = token;
                parser.WriteFile(_configFilePath, config);
                Debug.WriteLine("Refresh token saved successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving refresh token: {ex.Message}");
            }
        }

        public override void Close()
        {
            _server?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange(
                [_currentTrack, _artist, _coverArt, _elapsedTime, _remainingTime]
            );
            containers.Add(container);
        }

        public override void Update()
        {
            UpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("UpdateAsync called");
            await GetSpotifyInfo();
        }

        /// <summary>
        /// Fetches and updates the current Spotify playback information.
        /// </summary>
        private async Task GetSpotifyInfo()
        {
            Debug.WriteLine("GetSpotifyInfo called");

            if (_spotifyClient == null)
            {
                Debug.WriteLine("Spotify client is not initialized.");
                HandleError("Spotify client not initialized");
                return;
            }

            if (!_rateLimiter.TryRequest())
            {
                Debug.WriteLine("Rate limit exceeded, waiting...");
                await Task.Delay(1000); // Or implement a more sophisticated backoff strategy
                HandleError("Rate limit exceeded");
                return;
            }

            try
            {
                var playback = await ExecuteWithRetry(
                    () => _spotifyClient.Player.GetCurrentPlayback()
                );
                if (playback?.Item is FullTrack result)
                {
                    _currentTrack.Value = !string.IsNullOrEmpty(result.Name)
                        ? result.Name
                        : "Unknown Track";
                    _artist.Value = string.Join(
                        ", ",
                        result
                            .Artists.Select(a => !string.IsNullOrEmpty(a.Name) ? a.Name : "Unknown")
                            .Where(n => !string.IsNullOrEmpty(n))
                    );

                    var coverArtUrl = result.Album.Images.FirstOrDefault()?.Url ?? string.Empty;
                    if (!string.IsNullOrEmpty(coverArtUrl))
                    {
                        var localCoverArtPath = await DownloadAndSaveCoverArtAsync(coverArtUrl);
                        _coverArt.Value = localCoverArtPath ?? string.Empty;
                    }
                    else
                    {
                        _coverArt.Value = string.Empty;
                    }

                    // Format elapsed and remaining time in mm:ss
                    var elapsedSeconds = playback.ProgressMs / 1000;
                    var remainingSeconds = (result.DurationMs - playback.ProgressMs) / 1000;

                    _elapsedTime.Value = TimeSpan.FromSeconds(elapsedSeconds).ToString(@"mm\:ss");
                    _remainingTime.Value = TimeSpan
                        .FromSeconds(remainingSeconds)
                        .ToString(@"mm\:ss");

                    // Log updated values for debugging
                    Debug.WriteLine($"Current track Value: {_currentTrack.Value}");
                    Debug.WriteLine($"Artist Value: {_artist.Value}");
                    Debug.WriteLine($"Elapsed Time Value: {_elapsedTime.Value}");
                    Debug.WriteLine($"Remaining Time Value: {_remainingTime.Value}");
                    Debug.WriteLine($"Cover Art Value: {_coverArt.Value}");
                }
                else
                {
                    Debug.WriteLine("No track is currently playing.");
                    SetDefaultValues("No track playing");
                }
            }
            catch (Exception ex)
            {
                // Log the error
                Debug.WriteLine($"Error fetching Spotify playback information: {ex.Message}");
                HandleError("Error updating Spotify info");
            }
        }

        /// <summary>
        /// Executes a given operation with retry logic in case of rate limit or network issues.
        /// </summary>
        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxAttempts = 3)
        {
            int attempts = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1); // Start with 1 second delay

            while (attempts < maxAttempts)
            {
                try
                {
                    return await operation();
                }
                catch (APIException apiEx)
                    when (apiEx.Response != null
                        && apiEx.Response.StatusCode == HttpStatusCode.TooManyRequests
                    )
                {
                    if (
                        apiEx.Response.Headers.TryGetValue("Retry-After", out string? retryAfter)
                        && retryAfter != null
                    )
                    {
                        if (int.TryParse(retryAfter, out int seconds))
                        {
                            delay = TimeSpan.FromSeconds(seconds);
                        }
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(5); // Default delay if no Retry-After header
                    }

                    attempts++;
                    if (attempts >= maxAttempts)
                    {
                        throw; // If max attempts reached, rethrow the last exception
                    }

                    Debug.WriteLine(
                        $"Rate limit hit, waiting for {delay.TotalSeconds} seconds before retry. Attempt {attempts}/{maxAttempts}."
                    );
                    await Task.Delay(delay);
                    delay = delay.Multiply(2); // Exponential backoff
                    delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 3)); // Add some jitter
                }
                catch (HttpRequestException httpEx)
                {
                    attempts++;
                    Debug.WriteLine(
                        $"HTTP Request Exception: {httpEx.Message}. Inner Exception: {httpEx.InnerException?.Message}. Attempt {attempts}/{maxAttempts}."
                    );
                    if (attempts >= maxAttempts)
                    {
                        throw; // If max attempts reached, rethrow the last exception
                    }
                    await Task.Delay(delay); // Wait before retrying
                    delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 4)); // Exponential backoff with jitter
                }
            }

            return default; // This should not be reached due to the exception rethrowing
        }

        /// <summary>
        /// Sets default values for the UI elements when no data is available or on error.
        /// </summary>
        private void SetDefaultValues(string message = "Unknown")
        {
            _currentTrack.Value = message;
            _artist.Value = message;
            _coverArt.Value = string.Empty;

            _elapsedTime.Value = "00:00";
            _remainingTime.Value = "00:00";

            Debug.WriteLine($"Set default values for Spotify Info: {message}");
        }

        /// <summary>
        /// Handles errors by setting default values and logging the error.
        /// </summary>
        private void HandleError(string errorMessage)
        {
            SetDefaultValues(errorMessage);
            Debug.WriteLine($"Error occurred: {errorMessage}");
        }

        /// <summary>
        /// Downloads the cover art image from the provided URL and saves it to a local file.
        /// </summary>
private static readonly HttpClient _httpClient = new HttpClient();

private async Task<string?> DownloadAndSaveCoverArtAsync(string imageUrl)
{
    if (string.IsNullOrEmpty(_configFilePath))
    {
        Debug.WriteLine("Config file path is not set.");
        return null;
    }

    var configDirectory = Path.GetDirectoryName(_configFilePath);
    if (configDirectory == null)
    {
        Debug.WriteLine("Config directory is not valid.");
        return null;
    }

    var filePath = Path.Combine(configDirectory, "spotifyCoverArt.png");

    try
    {
        var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);

        int retryCount = 0;
        int maxRetries = 5;
        int delay = 500;

        while (retryCount < maxRetries)
        {
            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await fileStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                }
                Debug.WriteLine($"Successfully wrote to file: {filePath}");
                return filePath;
            }
            catch (IOException ioEx) when (IsFileLocked(ioEx))
            {
                retryCount++;
                Debug.WriteLine($"File is locked, retrying... Attempt {retryCount}/{maxRetries}");
                await Task.Delay(delay);
                delay *= 2; // Exponential backoff
            }
        }
        Debug.WriteLine("Failed to write cover art image after multiple attempts.");
        return null;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Error downloading cover art image: {ex.Message}");
        return null;
    }
}

        private bool IsFileLocked(IOException ioEx)
        {
            int errorCode =
                System.Runtime.InteropServices.Marshal.GetHRForException(ioEx) & ((1 << 16) - 1);
            return errorCode == 32 || errorCode == 33;
        }
    }

    /// <summary>
    /// Manages API request rates to comply with Spotify's rate limits.
    /// </summary>
    public class RateLimiter
    {
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly ConcurrentQueue<DateTime> _requestTimes;

        public RateLimiter(int maxRequests, TimeSpan timeWindow)
        {
            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
            _requestTimes = new ConcurrentQueue<DateTime>();
        }

        /// <summary>
        /// Checks if a new request can be made within the rate limit.
        /// </summary>
        /// <returns>True if the request can be made, false if the rate limit is exceeded.</returns>
        public bool TryRequest()
        {
            var now = DateTime.UtcNow;
            _requestTimes.Enqueue(now);

            // Remove times outside the time window
            while (_requestTimes.TryPeek(out DateTime oldest) && (now - oldest) > _timeWindow)
            {
                _requestTimes.TryDequeue(out _);
            }

            // Check if we're within the limit
            return _requestTimes.Count <= _maxRequests;
        }
    }
}
