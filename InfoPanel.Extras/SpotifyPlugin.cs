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

/*
 * Plugin: Spotify Info - SpotifyPlugin
 * Version: 1.0.23
 * Description: A plugin for InfoPanel to display current Spotify track information, including track name, artist, cover URL, elapsed time, and remaining time. Uses the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls. Supports PluginSensor for track progression and PluginText for cover URL.
 * Changelog:
 *   - v1.0.23 (Feb 20, 2025): Changed _coverUrl ID to "cover-art" for image recognition.
 *     - Changes: Renamed _coverUrl's ID to "cover-art" to match original _coverArt, using raw Spotify URL, kept _coverArt commented out.
 *     - Purpose: Signal InfoPanel to render _coverUrl as an image without core file changes.
 *   - v1.0.22 (Feb 20, 2025): Appended .jpg to _coverUrl for image display.
 *     - Changes: Added .jpg extension to raw Spotify URL in _coverUrl.Value.
 *   - v1.0.21 (Feb 20, 2025): Commented out _coverArt code, kept _coverUrl with raw URL.
 *     - Changes: Re-commented all _coverArt-related code, ensured _coverUrl uses raw Spotify URL.
 *   - v1.0.20 (Feb 20, 2025): Reverted _coverIconUrl to _coverUrl and restored _coverArt code.
 *     - Changes: Renamed back to _coverUrl, set raw Spotify URL, uncommented _coverArt code.
 *   - v1.0.19 (Feb 20, 2025): Renamed _coverUrl to _coverIconUrl for image display compatibility.
 *     - Changes: Adjusted field ID to "cover_icon_url" to match WeatherPlugin's naming.
 *   - v1.0.18 (Feb 20, 2025): Commented out _coverArt-related code.
 *     - Changes: Removed local cover art download and caching, focusing on _coverUrl.
 *   - v1.0.17 (Feb 20, 2025): Fixed cover URL display and track change update.
 *     - Fixes: Reverted to raw coverArtUrl for _coverUrl.Value, ensured update on track change.
 *   - v1.0.16 (Feb 20, 2025): Fixed cover URL update on track change with dynamic construction.
 *     - Fixes: Used dynamic URL construction (https://i.scdn.co/image/{imageId}).
 *   - v1.0.15 (Feb 20, 2025): Fixed cover URL display.
 *     - Fixes: Ensured _coverUrl.Value always gets the raw Spotify URL.
 *   - v1.0.14 (Feb 20, 2025): Added PluginText for cover art URL.
 *     - Features: Added _coverUrl to store and display the raw Spotify cover art URL.
 *   - v1.0.13 (Feb 20, 2025): Replaced total track time with track progression percentage (fixed float compatibility).
 *     - Features: Added _trackProgress as PluginSensor (0-100%, float) for dynamic playback progress.
 *     - Fixes: Changed _trackProgress.Value to float from double for PluginSensor compatibility.
 *   - v1.0.12 (Feb 20, 2025): Fixed dynamic total track time update.
 *     - Fixes: Ensured _totalTrackTime updated every 1 second (static value).
 *   - v1.0.11 (Feb 20, 2025): Added PluginSensor for total track time.
 *     - Features: Added _totalTrackTime for track duration in milliseconds.
 *   - v1.0.10 (Feb 20, 2025): Added resume animation and track end refinement.
 *     - Features: Shows "Resuming..." for 1 second; "Track Ended" for 3 seconds.
 *   - v1.0.9 (Feb 20, 2025): Added visual pause indication.
 *     - Features: Sets track and artist to "Paused" when paused.
 *   - v1.0.8 (Feb 20, 2025): Fixed pause detection timing.
 *     - Fixes: Widened ProgressToleranceMs to 1500ms; moved pause check to API sync.
 *   - v1.0.7 (Feb 20, 2025): Reliable pause freeze attempt.
 *     - Fixes: Local pause detection with _pauseDetected flag.
 *   - v1.0.6 (Feb 20, 2025): Robust pause detection attempt.
 *     - Fixes: Simplified pause detection, forced sync on stall.
 *   - v1.0.5 (Feb 20, 2025): Adjusted pause detection.
 *     - Fixes: Moved pause check before estimation.
 *   - v1.0.4 (Feb 20, 2025): Pause detection enhancement.
 *     - Features: Added pause detection between syncs.
 *   - v1.0.3 (Feb 20, 2025): Responsiveness improvement.
 *     - Features: Sync interval to 2 seconds; forced sync on track end.
 *   - v1.0.2 (Feb 20, 2025): Performance optimization.
 *     - Features: Caching reduced API calls to 5 seconds; cover art caching.
 *   - v1.0.1: Beta release with core functionality.
 *   - v1.0.0: Internal pre-release.
 * Note: Spotify API rate limits estimated at ~180 requests/minute (https://developer.spotify.com/documentation/web-api/concepts/rate-limits).
 */

namespace InfoPanel.Extras
{
    // Displays current Spotify track information in the InfoPanel application.
    public class SpotifyPlugin : BasePlugin
    {
        // UI display elements (PluginText)
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
        private readonly PluginText _artist = new("artist", "Artist", "-");

        //private readonly PluginText _coverArt = new("cover-art", "Cover Art", "-"); // Commented out
        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00");
        private readonly PluginText _remainingTime = new(
            "remaining-time",
            "Remaining Time",
            "00:00"
        );
        private readonly PluginText _coverUrl = new("cover-art", "Cover URL", ""); // Using "cover-art" ID with raw Spotify URL

        // UI display elements (PluginSensor)
        private readonly PluginSensor _trackProgress = new(
            "track-progress",
            "Track Progress (%)",
            0.0F
        ); // 0 to 100%, float

        // Spotify API and authentication
        private SpotifyClient? _spotifyClient;
        private string? _verifier;
        private EmbedIOAuthServer? _server;
        private string? _apiKey;
        private string? _configFilePath;
        private string? _refreshToken;

        // Rate limiter for API calls
        private readonly RateLimiter _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1));

        // Cache for playback state and cover art
        private string? _lastTrackId; // Last known track ID
        private int _lastProgressMs; // Last known progress in milliseconds
        private int _previousProgressMs; // Progress from the previous sync
        private int _lastDurationMs; // Last known track duration in milliseconds
        private bool _isPlaying; // Whether the track was playing at last sync
        private DateTime _lastApiCallTime = DateTime.MinValue; // Time of last API call
        private bool _pauseDetected; // Flag to limit forced syncs during pause
        private bool _trackEnded; // Flag for track end state
        private DateTime _trackEndTime; // Time when track ended
        private bool _isResuming; // Flag for resume animation
        private DateTime _resumeStartTime; // Time when resume started
        private string? _lastTrackName; // Store last track name for resume
        private string? _lastArtistName; // Store last artist name for resume

        //private readonly Dictionary<string, string> _coverArtCache = new(); // Commented out
        private const int SyncIntervalSeconds = 2; // Sync with API every 2 seconds
        private const int ProgressToleranceMs = 1500; // Tolerance for pause detection (ms)

        private static readonly object _fileLock = new object();

        public SpotifyPlugin()
            : base(
                "spotify-plugin",
                "Spotify Info",
                "Displays the current Spotify track information."
            ) { }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initializes the plugin, setting up authentication and configuration.
        public override void Initialize()
        {
            Debug.WriteLine("Initialize called");

            Assembly assembly = Assembly.GetExecutingAssembly();
            _configFilePath = $"{assembly.ManifestModule.FullyQualifiedName}.ini";

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
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
                    StartAuthentication();
                }
            }

            var container = new PluginContainer("Spotify");
            container.Entries.AddRange(
                [_currentTrack, _artist, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]
            );
            Load([container]);
        }

        // Attempts to refresh the Spotify access token using the stored refresh token.
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
                _refreshToken = null;
                return false;
            }
        }

        // Starts the Spotify authentication process using PKCE.
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
                Process.Start(
                    new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true }
                );
                Debug.WriteLine("Authentication process started.");
            }
            catch (Exception ex)
            {
                HandleError($"Error starting authentication: {ex.Message}");
            }
        }

        // Handles the authorization code response from Spotify.
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

                var authenticator = new PKCEAuthenticator(_apiKey, initialResponse);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);
                await _server.Stop();
                Debug.WriteLine("Authentication completed successfully.");
            }
            catch (APIException apiEx)
            {
                HandleError("API authentication error");
                if (apiEx.Response != null && Debugger.IsAttached)
                {
                    Debug.WriteLine($"API Response Error: {apiEx.Message}");
                }
            }
            catch (Exception ex)
            {
                HandleError($"Authentication failed: {ex.Message}");
            }
        }

        // Saves the refresh token to the config file.
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
                [_currentTrack, _artist, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]
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

        // Updates track info, using caching to reduce API calls and estimate time between syncs.
        private async Task GetSpotifyInfo()
        {
            Debug.WriteLine("GetSpotifyInfo called");

            if (_spotifyClient == null)
            {
                Debug.WriteLine("Spotify client is not initialized.");
                HandleError("Spotify client not initialized");
                return;
            }

            var now = DateTime.UtcNow;
            var timeSinceLastCall = (now - _lastApiCallTime).TotalSeconds;
            bool forceSync = false;

            // Check for track end before estimating
            if (_lastTrackId != null && _isPlaying)
            {
                int elapsedMs = _lastProgressMs + (int)(timeSinceLastCall * 1000);
                if (elapsedMs >= _lastDurationMs)
                {
                    Debug.WriteLine("Track likely ended, forcing API sync.");
                    _trackEnded = true;
                    _trackEndTime = DateTime.UtcNow;
                    forceSync = true;
                }
            }

            // If less than sync interval and no force sync, estimate time if playing
            if (
                timeSinceLastCall < SyncIntervalSeconds
                && !forceSync
                && _lastTrackId != null
                && _isPlaying
            )
            {
                int elapsedMs = _lastProgressMs + (int)(timeSinceLastCall * 1000);
                if (elapsedMs >= _lastDurationMs)
                {
                    _isPlaying = false;
                    _trackEnded = true;
                    _trackEndTime = DateTime.UtcNow;
                    SetDefaultValues("Track Ended");
                    return;
                }

                _elapsedTime.Value = TimeSpan.FromMilliseconds(elapsedMs).ToString(@"mm\:ss");
                _remainingTime.Value = TimeSpan
                    .FromMilliseconds(_lastDurationMs - elapsedMs)
                    .ToString(@"mm\:ss");
                _trackProgress.Value =
                    _lastDurationMs > 0 ? (float)(elapsedMs / (double)_lastDurationMs * 100) : 0.0F; // Progress percentage as float
                Debug.WriteLine(
                    $"Estimated - Elapsed: {_elapsedTime.Value}, Remaining: {_remainingTime.Value}, Progress: {_trackProgress.Value:F1}%"
                );
                return;
            }

            // Sync with API if interval elapsed, forced, or no cached data
            if (!_rateLimiter.TryRequest())
            {
                Debug.WriteLine("Rate limit exceeded, waiting...");
                await Task.Delay(1000);
                HandleError("Rate limit exceeded");
                return;
            }

            try
            {
                var playback = await ExecuteWithRetry(
                    () => _spotifyClient.Player.GetCurrentPlayback()
                );
                _lastApiCallTime = DateTime.UtcNow;

                if (playback?.Item is FullTrack result)
                {
                    // Check for pause by comparing current progress with previous sync
                    if (
                        _isPlaying
                        && _previousProgressMs >= 0
                        && Math.Abs(playback.ProgressMs - _previousProgressMs)
                            <= ProgressToleranceMs
                        && !_pauseDetected
                    )
                    {
                        Debug.WriteLine(
                            "Progress stalled (pause detected), forcing API sync and stopping estimation."
                        );
                        _isPlaying = false;
                        _pauseDetected = true;
                        _currentTrack.Value = "Paused";
                        _artist.Value = "Paused";
                    }

                    // Check for resume transition
                    bool wasPaused = !_isPlaying && _pauseDetected;
                    _previousProgressMs = _lastProgressMs;
                    _lastTrackId = result.Id;
                    _lastProgressMs = playback.ProgressMs;
                    _lastDurationMs = result.DurationMs;
                    _isPlaying = playback.IsPlaying ? playback.IsPlaying : _isPlaying; // Respect API unless paused locally

                    // Handle resume animation
                    if (wasPaused && _isPlaying && !_isResuming)
                    {
                        _isResuming = true;
                        _resumeStartTime = DateTime.UtcNow;
                        _currentTrack.Value = "Resuming...";
                        _artist.Value = "Resuming...";
                    }

                    // Update track and artist based on state
                    if (_isPlaying || _lastTrackId != result.Id)
                    {
                        _pauseDetected = false;
                        _trackEnded = false;
                        _lastTrackName = !string.IsNullOrEmpty(result.Name)
                            ? result.Name
                            : "Unknown Track";
                        _lastArtistName = string.Join(
                            ", ",
                            result.Artists.Select(a => a.Name ?? "Unknown")
                        );

                        if (_isResuming && (DateTime.UtcNow - _resumeStartTime).TotalSeconds >= 1)
                        {
                            _isResuming = false;
                            _currentTrack.Value = _lastTrackName;
                            _artist.Value = _lastArtistName;
                        }
                        else if (!_isResuming)
                        {
                            _currentTrack.Value = _lastTrackName;
                            _artist.Value = _lastArtistName;
                        }
                    }

                    var coverArtUrl = result.Album.Images.FirstOrDefault()?.Url ?? string.Empty;
                    Debug.WriteLine($"Raw cover art URL from Spotify: {coverArtUrl}"); // Debug raw URL
                    _coverUrl.Value = coverArtUrl; // Set raw URL directly, updates on track change

                    /* Commented out cover art download and caching logic
                    if (!string.IsNullOrEmpty(coverArtUrl))
                    {
                        if (_coverArtCache.TryGetValue(_lastTrackId, out string? cachedPath))
                        {
                            _coverArt.Value = cachedPath;
                        }
                        else
                        {
                            var localPath = await DownloadAndSaveCoverArtAsync(coverArtUrl);
                            if (localPath != null)
                            {
                                _coverArtCache[_lastTrackId] = localPath;
                                _coverArt.Value = localPath;
                            }
                            else
                            {
                                _coverArt.Value = string.Empty;
                            }
                        }
                    }
                    else
                    {
                        _coverArt.Value = string.Empty;
                    }
                    */

                    _elapsedTime.Value = TimeSpan
                        .FromMilliseconds(_lastProgressMs)
                        .ToString(@"mm\:ss");
                    _remainingTime.Value = TimeSpan
                        .FromMilliseconds(_lastDurationMs - _lastProgressMs)
                        .ToString(@"mm\:ss");
                    _trackProgress.Value =
                        _lastDurationMs > 0
                            ? (float)(_lastProgressMs / (double)_lastDurationMs * 100)
                            : 0.0F; // Progress percentage as float
                    Debug.WriteLine(
                        $"Synced - Track: {_currentTrack.Value}, Artist: {_artist.Value}, Cover URL: {_coverUrl.Value}"
                    );
                    Debug.WriteLine(
                        $"Elapsed: {_elapsedTime.Value}, Remaining: {_remainingTime.Value}, Progress: {_trackProgress.Value:F1}%"
                    );
                }
                else
                {
                    Debug.WriteLine("No track is currently playing.");
                    _lastTrackId = null;
                    _isPlaying = false;
                    _pauseDetected = false;
                    _isResuming = false;

                    // Handle track end display for 3 seconds
                    if (_trackEnded && (DateTime.UtcNow - _trackEndTime).TotalSeconds < 3)
                    {
                        _currentTrack.Value = "Track Ended";
                        _artist.Value = _lastArtistName ?? "Unknown";
                        _elapsedTime.Value = TimeSpan
                            .FromMilliseconds(_lastDurationMs)
                            .ToString(@"mm\:ss");
                        _remainingTime.Value = "00:00";
                        _trackProgress.Value = 100.0F; // 100% at track end
                        //_coverUrl.Value = _coverArt.Value; // Retain last URL during track end (commented out since _coverArt is out)
                    }
                    else
                    {
                        _trackEnded = false;
                        _previousProgressMs = -1;
                        _lastTrackName = null;
                        _lastArtistName = null;
                        _trackProgress.Value = 0.0F; // Reset progress when no track
                        SetDefaultValues("No track playing");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Spotify playback: {ex.Message}");
                HandleError("Error updating Spotify info");
            }
        }

        // Executes an API call with retry logic for rate limits or network errors.
        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxAttempts = 3)
        {
            int attempts = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);

            while (attempts < maxAttempts)
            {
                try
                {
                    return await operation();
                }
                catch (APIException apiEx)
                    when (apiEx.Response?.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (
                        apiEx.Response.Headers.TryGetValue("Retry-After", out string? retryAfter)
                        && int.TryParse(retryAfter, out int seconds)
                    )
                    {
                        delay = TimeSpan.FromSeconds(seconds);
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(5);
                    }

                    attempts++;
                    if (attempts >= maxAttempts)
                        throw;

                    Debug.WriteLine(
                        $"Rate limit hit, waiting {delay.TotalSeconds}s. Attempt {attempts}/{maxAttempts}"
                    );
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(
                        (int)delay.TotalSeconds * 2 + new Random().Next(1, 3)
                    );
                }
                catch (HttpRequestException httpEx)
                {
                    attempts++;
                    Debug.WriteLine(
                        $"HTTP Error: {httpEx.Message}. Attempt {attempts}/{maxAttempts}"
                    );
                    if (attempts >= maxAttempts)
                        throw;
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 4));
                }
            }
            return default;
        }

        // Resets UI elements to default values on error or no data.
        private void SetDefaultValues(string message = "Unknown")
        {
            _currentTrack.Value = message;
            _artist.Value = message;
            //_coverArt.Value = string.Empty; // Commented out
            _elapsedTime.Value = "00:00";
            _remainingTime.Value = "00:00";
            _trackProgress.Value = 0.0F; // Reset progress
            _coverUrl.Value = string.Empty; // Reset cover URL
            Debug.WriteLine($"Set default values: {message}");
        }

        // Logs errors and sets default UI values.
        private void HandleError(string errorMessage)
        {
            SetDefaultValues(errorMessage);
            Debug.WriteLine($"Error: {errorMessage}");
        }

        /* Commented out cover art download function
        // Downloads and saves cover art, retrying on file lock conflicts.
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
                using (var client = new HttpClient())
                {
                    var imageBytes = await client.GetByteArrayAsync(imageUrl);

                    int retryCount = 0;
                    int maxRetries = 5;
                    int delay = 500;

                    while (retryCount < maxRetries)
                    {
                        try
                        {
                            using (var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                            {
                                await fileStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                            }
                            Debug.WriteLine($"Wrote cover art to: {filePath}");
                            return filePath;
                        }
                        catch (IOException ioEx) when (IsFileLocked(ioEx))
                        {
                            retryCount++;
                            Debug.WriteLine($"File locked, retrying... {retryCount}/{maxRetries}");
                            await Task.Delay(delay);
                            delay *= 2;
                        }
                    }
                    Debug.WriteLine("Failed to write cover art after retries.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error downloading cover art: {ex.Message}");
                return null;
            }
        }

        private bool IsFileLocked(IOException ioEx)
        {
            int errorCode = System.Runtime.InteropServices.Marshal.GetHRForException(ioEx) & ((1 << 16) - 1);
            return errorCode == 32 || errorCode == 33;
        }
        */
    }

    // Manages API request rates to comply with Spotify's limits.
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

        public bool TryRequest()
        {
            var now = DateTime.UtcNow;
            _requestTimes.Enqueue(now);
            while (_requestTimes.TryPeek(out DateTime oldest) && (now - oldest) > _timeWindow)
            {
                _requestTimes.TryDequeue(out _);
            }
            return _requestTimes.Count <= _maxRequests;
        }
    }
}
