using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan.Parsers;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;

namespace MQTT_Automations
{
    internal class SpotifyAuthorization
    {
        private readonly EmbedIOAuthServer _server;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private SpotifyClient? _spotifyClient;
        private string _tokenFilename;

        public SpotifyAuthorization(string tokenFilename)
        {
            var spotifySection = (NameValueCollection)ConfigurationManager.GetSection("spotifyClient");
            var callbackUri = spotifySection["callbackUri"];
            var callbackPort = int.Parse(spotifySection["callbackPort"]!);
            _clientId = spotifySection["clientId"]!;
            _clientSecret = spotifySection["clientSecret"]!;
            _server = new EmbedIOAuthServer(new Uri(callbackUri!), callbackPort);
            _tokenFilename = tokenFilename;
        }

        public async Task<SpotifyClient> Login()
        {
            var tokenJson = File.ReadAllText(_tokenFilename);
            var token = JsonConvert.DeserializeObject<AuthorizationCodeTokenResponse>(tokenJson)!;
            GetUpdatedToken(token);
            //else
            //await StartAuthentication();
            while (_spotifyClient == null)
                await Task.Delay(500);
            return _spotifyClient;
        }

        private async Task StartAuthentication()
        {
            await _server.Start();
            _server.AuthorizationCodeReceived += async (sender, response) =>
            {
                await _server.Stop();
                var config = SpotifyClientConfig.CreateDefault();
                var tokenResponse = await new OAuthClient(config).RequestToken(
                  new AuthorizationCodeTokenRequest(
                    _clientId, _clientSecret, response.Code, _server.BaseUri
                  )
                );

                SaveToken(tokenResponse);

                GetUpdatedToken(tokenResponse);
            };

            var request = new LoginRequest(_server.BaseUri, _clientId!, LoginRequest.ResponseType.Code)
            {
                Scope = new List<string> {
                    Scopes.PlaylistModifyPrivate
                    ,Scopes.PlaylistModifyPublic
                    ,Scopes.PlaylistReadPrivate
                    ,Scopes.UserLibraryRead
                    ,Scopes.UserLibraryModify
                    ,Scopes.UserModifyPlaybackState
                    ,Scopes.UserReadPlaybackState
                    ,Scopes.UserReadPrivate
                }
            };

            Uri uri = request.ToUri();
            try
            {
                BrowserUtil.Open(uri);
            }
            catch (Exception)
            {
                Debug.WriteLine("Unable to open URL, manually open: {0}", uri);
            }
        }

        private void GetUpdatedToken(AuthorizationCodeTokenResponse token)
        {
            var authenticator = new AuthorizationCodeAuthenticator(_clientId, _clientSecret, token!);
            authenticator.TokenRefreshed += (sender, token) => SaveToken(token);

            var config = SpotifyClientConfig.CreateDefault()
              .WithAuthenticator(authenticator);

            _spotifyClient = new SpotifyClient(config);
        }

        private void SaveToken(AuthorizationCodeTokenResponse token)
        {
            var tokenJson = JsonConvert.SerializeObject(token);
            File.WriteAllText(_tokenFilename, tokenJson);
        }
    }
}
