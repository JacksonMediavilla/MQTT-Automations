using Polly;
using Polly.Caching;
using Polly.Retry;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace MQTT_Automations
{
    internal class Spotify
    {
        private readonly SpotifyClient _client;
        private static readonly AsyncRetryPolicy _policy;
        private static readonly int _savedTracksGetLimit;
        private static readonly int _playlistAddTrackLimit;
        private static readonly int _playlistGetLimit;
        private static readonly int _playlistRemoveTrackLimit;
        private static readonly int _trackSearchLimit;
        private static readonly int _playlistTrackGetLimit;

        static Spotify()
        {
            _policy = Policy
                .Handle<APIException>()
                .RetryAsync(5, (exception, retryAttempt) =>
                {
                    if (exception is APITooManyRequestsException tooManyRequestsException)
                    {
                        var retryAfter = tooManyRequestsException.RetryAfter;
                        Debug.WriteLine($"Too many requests exception. Retrying after {retryAfter} seconds.");
                        Thread.Sleep((int)retryAfter.TotalMilliseconds);
                    }
                    else
                    {
                        Debug.WriteLine($"API Exception. {exception.Message}");
                        Thread.Sleep((int)(Math.Pow(1.5, retryAttempt) * 1000));
                    }
                });

            var spotifySection = (NameValueCollection)ConfigurationManager.GetSection("spotifyClient");
            _savedTracksGetLimit = int.Parse(spotifySection["likedTracksGetLimit"]!);
            _playlistAddTrackLimit = int.Parse(spotifySection["playlistAddTrackLimit"]!);
            _playlistGetLimit = int.Parse(spotifySection["playlistGetLimit"]!);
            _playlistRemoveTrackLimit = int.Parse(spotifySection["playlistRemoveTrackLimit"]!);
            _trackSearchLimit = int.Parse(spotifySection["trackSearchLimit"]!);
            _playlistTrackGetLimit = int.Parse(spotifySection["playlistTrackGetLimit"]!);
        }

        public Spotify(SpotifyClient client) =>
            _client = client;

        public async Task SaveTrack(List<string> trackIds)
        {
            await _policy.ExecuteAsync(() => _client.Library.SaveTracks(new LibrarySaveTracksRequest(trackIds)));
        }

        public async Task<CurrentlyPlaying> GetCurrentlyPlaying()
        {
            return await _client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
        }

        public async Task StartPlayback(string deviceName, List<string> contextUris)
        {
            var devicePolicyResult = await _policy.ExecuteAndCaptureAsync(() => _client.Player.GetAvailableDevices());
            var deviceResponse = devicePolicyResult.Result;
            Device? device = null;
            foreach (var d in deviceResponse.Devices)
            {
                if (d.Name == deviceName)
                    device = d;
            }
            if (device == null)
                return;

            var playbackPolicyResult = await _policy.ExecuteAndCaptureAsync(() => _client.Player.GetCurrentPlayback());
            var playbackInfo = playbackPolicyResult.Result;
            if (playbackInfo != null && playbackInfo.IsPlaying)
                await _policy.ExecuteAsync(() => _client.Player.PausePlayback());

            var transferRequest = new PlayerTransferPlaybackRequest(new[] { device.Id });
            await _policy.ExecuteAsync(() => _client.Player.TransferPlayback(transferRequest));

            await _policy.ExecuteAsync(() => _client.Player.SetShuffle(new PlayerShuffleRequest(true)));

            var resumeRequest = new PlayerResumePlaybackRequest();
            resumeRequest.DeviceId = device.Id;
            if (contextUris.Count() == 1)
                resumeRequest.ContextUri = contextUris[0];
            else
                resumeRequest.Uris = contextUris;

            await _policy.ExecuteAsync(() => _client.Player.ResumePlayback(resumeRequest));
        }

        public async Task<List<SimplePlaylist>> GetCurrentUsersPlaylists()
        {
            var limit = _playlistGetLimit;
            var playlists = new List<SimplePlaylist>();
            var policyResult = await _policy.ExecuteAndCaptureAsync(() =>
                _client.Playlists.CurrentUsers(new PlaylistCurrentUsersRequest() { Limit = limit }));
            var initialPage = policyResult.Result;
            int total = initialPage.Total!.Value;
            for (int i = 0; i < total; i += limit)
            {
                Paging<SimplePlaylist> page;
                if (i == 0)
                    page = initialPage;
                else
                {
                    var policyResult2 = await _policy.ExecuteAndCaptureAsync(() =>
                        _client.Playlists.CurrentUsers(new PlaylistCurrentUsersRequest() { Limit = limit, Offset = i }));
                    page = policyResult2.Result;
                }
                foreach (var item in page.Items!)
                {
                    if (item is not null)
                        playlists.Add(item);
                }
            }
            return playlists;
        }

        public async Task<List<FullTrack>> GetCurrentUserSavedTracks()
        {
            var result = new List<FullTrack>();
            var request = new LibraryTracksRequest() { Market = "US", Limit = _savedTracksGetLimit };
            var likedTracksPolicyResult = await _policy.ExecuteAndCaptureAsync(() => _client.Library.GetTracks(request));
            var likedTracksInitialPage = likedTracksPolicyResult.Result;
            int total = likedTracksInitialPage.Total!.Value;
            for (int i = 0; i < total; i += _savedTracksGetLimit)
            {
                Paging<SavedTrack> page;
                if (i == 0)
                    page = likedTracksInitialPage;
                else
                {
                    request.Offset = i;
                    likedTracksPolicyResult = await _policy.ExecuteAndCaptureAsync(() => _client.Library.GetTracks(request));
                    page = likedTracksPolicyResult.Result;
                }
                foreach (var likedTrack in page.Items!)
                    result.Add(likedTrack.Track);
                Debug.WriteLine($"Retrieved {Math.Min(i + _savedTracksGetLimit, total)}/{likedTracksInitialPage.Total} saved tracks.");
            }
            return result;
        }

        public async Task<PrivateUser> GetCurrentUser()
        {
            var result = await _policy.ExecuteAndCaptureAsync(() => _client.UserProfile.Current());
            return result.Result;
        }

        public async Task<List<FullTrack>> GetPlaylistFullTracks(string playlistId)
        {
            var limit = _playlistTrackGetLimit;
            var tracks = new List<FullTrack>();
            var request = new PlaylistGetItemsRequest() { Market = "US", Limit = _playlistTrackGetLimit };
            var policyResult = await _policy.ExecuteAndCaptureAsync(() => _client.Playlists.GetItems(playlistId, request));
            var initialPage = policyResult.Result;
            int total = initialPage.Total!.Value;
            for (int i = 0; i < total; i += limit)
            {
                Paging<PlaylistTrack<IPlayableItem>> page;
                if (i == 0)
                    page = initialPage;
                else
                {
                    request.Offset = i;
                    var policyResult2 = await _policy.ExecuteAndCaptureAsync(() => _client.Playlists.GetItems(playlistId, request));
                    page = policyResult2.Result;
                }
                foreach (var track in page.Items!)
                {
                    if (track.Track is FullTrack ft)
                        tracks.Add(ft);
                }
                Debug.WriteLine($"Retrieved {Math.Min(i + limit, total)}/{total} tracks.");
            }
            return tracks;
        }

        public async Task<SnapshotResponse?> AddToPlaylist(string playlistId, List<string> trackUris)
        {
            var total = trackUris.Count();
            SnapshotResponse? snapshotResponse = null;
            for (int i = 0; i < total; i += _playlistAddTrackLimit)
            {
                var count = i + _playlistAddTrackLimit > total ? total - i : _playlistAddTrackLimit;
                var range = trackUris.GetRange(i, count);
                snapshotResponse = (await _policy.ExecuteAndCaptureAsync(() => _client.Playlists.AddItems(playlistId, new PlaylistAddItemsRequest(range)))).Result;
                Debug.WriteLine($"Added {i + count}/{total} tracks.");
            }
            return snapshotResponse;
        }

        public async Task RemoveFromPlaylist(string playlistId, List<string> trackUris)
        {
            for (int i = 0; i < trackUris.Count; i += _playlistRemoveTrackLimit)
            {
                int limit = Math.Min(i + _playlistRemoveTrackLimit, trackUris.Count);
                var items = new List<PlaylistRemoveItemsRequest.Item>();
                for (int j = i; j < limit; j++)
                    items.Add(new PlaylistRemoveItemsRequest.Item() { Uri = trackUris[j] });
                var request = new PlaylistRemoveItemsRequest() { Tracks = items };
                await _policy.ExecuteAsync(() => _client.Playlists.RemoveItems(playlistId, request));
            }
        }

        public async Task Next()
        {
            await _policy.ExecuteAsync(() => _client.Player.SkipNext());
        }

        public async Task Previous()
        {
            await _policy.ExecuteAsync(() => _client.Player.SkipPrevious());
        }

        public async Task<string> TogglePlay()
        {
            var playback = (await _policy.ExecuteAndCaptureAsync(() => _client.Player.GetCurrentPlayback())).Result;
            bool playing = playback.IsPlaying;
            if (playing)
            {
                await _policy.ExecuteAsync(() => _client.Player.PausePlayback());
                return "paused";
            }
            else
            {
                await _policy.ExecuteAsync(() => _client.Player.ResumePlayback());
                return "resumed";
            }
        }
    }
}
