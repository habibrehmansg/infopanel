using InfoPanel.Plugins;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.ComponentModel;
using System.Web;
using IniParser;
using IniParser.Model;
using System.Reflection;

namespace InfoPanel.Extras
{
    public class SpotifyPlugin : BasePlugin
    {
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
        private readonly PluginText _artist = new("artist", "Artist", "-");
        private readonly PluginText _coverArt = new("cover-art", "Cover Art", "-");

        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00");
        private readonly PluginText _remainingTime = new("remaining-time", "Remaining Time", "00:00");

        private SpotifyClient? _spotifyClient;
        private string? _verifier;
        private EmbedIOAuthServer? _server;
        private string? _apiKey;
        private string? _configFilePath;

        public SpotifyPlugin() : base("spotify-plugin", "Spotify Info", "Displays the current Spotify track information.")
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

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
                config = new IniData();
                config["Spotify Plugin"]["APIKey"] = "<your-spotify-api-key>"; // Default or placeholder value
                parser.WriteFile(_configFilePath, config);
            }
            else
            {
                config = parser.ReadFile(_configFilePath);

                _apiKey = config["Spotify Plugin"]["APIKey"];

                if (!string.IsNullOrEmpty(_apiKey))
                {
                    // Generate verifier and challenge
                    var (verifier, challenge) = PKCEUtil.GenerateCodes();
                    _verifier = verifier;

                    // Start the embedded HTTP server
                    _server = new EmbedIOAuthServer(new Uri("http://localhost:5000/callback"), 5000);
                    _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                    _server.Start();

                    // Generate login URI with the API key from configuration
                    var loginRequest = new LoginRequest(
                        _server.BaseUri,
                        _apiKey,
                        LoginRequest.ResponseType.Code
                    )
                    {
                        CodeChallengeMethod = "S256",
                        CodeChallenge = challenge,
                        Scope = new[] { Scopes.UserReadPlaybackState, Scopes.UserReadCurrentlyPlaying }
                    };
                    var uri = loginRequest.ToUri();

                    // Redirect user to uri via your favorite web-server or open a local browser window
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uri.ToString(),
                        UseShellExecute = true
                    });

                    Debug.WriteLine("Initialize completed");
                }
                else
                {
                    Debug.WriteLine("Spotify API Key is not set or is invalid.");
                    return;
                }
            }

            // Initialize the container with default values
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _coverArt, _elapsedTime, _remainingTime]);
            Load([container]);
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            if (_verifier == null || _apiKey == null)
            {
                throw new InvalidOperationException("Verifier or API Key is not initialized.");
            }

            // Exchange code for access token and refresh token
            var initialResponse = await new OAuthClient().RequestToken(
                new PKCETokenRequest(_apiKey, response.Code, _server!.BaseUri, _verifier)
            );

            var authenticator = new PKCEAuthenticator(_apiKey, initialResponse);
            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
            _spotifyClient = new SpotifyClient(config);

            // Stop the server after successful authentication
            await _server.Stop();
        }

        public override void Close()
        {
            _server?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _coverArt, _elapsedTime, _remainingTime]);
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

        private async Task GetSpotifyInfo()
        {
            Debug.WriteLine("GetSpotifyInfo called");

            if (_spotifyClient == null)
            {
                Debug.WriteLine("Spotify client is not initialized.");
                SetDefaultValues();
                return;
            }

            try
            {
                var playback = await _spotifyClient.Player.GetCurrentPlayback();
                if (playback?.Item is FullTrack result)
                {
                    // Handle foreign characters by not encoding unless necessary for HTML
                    _currentTrack.Value = !string.IsNullOrEmpty(result.Name) ? result.Name : "Unknown Track";
                    _artist.Value = string.Join(", ", result.Artists.Select(a => !string.IsNullOrEmpty(a.Name) ? a.Name : "Unknown").Where(n => !string.IsNullOrEmpty(n)));
                    _coverArt.Value = result.Album.Images.FirstOrDefault()?.Url ?? string.Empty;

                    // Format elapsed and remaining time in mm:ss
                    var elapsedSeconds = playback.ProgressMs / 1000;
                    var remainingSeconds = (result.DurationMs - playback.ProgressMs) / 1000;
                    
                    _elapsedTime.Value = TimeSpan.FromSeconds(elapsedSeconds).ToString(@"mm\:ss");
                    _remainingTime.Value = TimeSpan.FromSeconds(remainingSeconds).ToString(@"mm\:ss");

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
                SetDefaultValues("Error updating");
            }
        }

        private void SetDefaultValues(string message = "Unknown")
        {
            _currentTrack.Value = message;
            _artist.Value = message;
            _coverArt.Value = string.Empty;

            _elapsedTime.Value = "00:00";
            _remainingTime.Value = "00:00";

            Debug.WriteLine($"Set default values for Spotify Info: {message}");
        }
    }
}