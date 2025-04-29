using MQTTnet.Client;
using MQTTnet;
using System.Text;
using System.Collections.Specialized;
using System.Configuration;
using SpotifyAPI.Web;
using System.IO;
using TagLib.Matroska;

namespace MQTT_Automations
{
    internal class Application
    {
        private readonly IMqttClient _mqttClient;
        private readonly MqttFactory _mqttFactory;
        private MqttClientOptions _mqttClientOptions;
        private List<string> _topics;
        private Spotify _spotifyJackson;
        private readonly Dictionary<string, string> _playlistNamesJackson = new Dictionary<string, string>();
        private readonly Dictionary<string, PlaylistItem> _playlistCacheJackson = new Dictionary<string, PlaylistItem>();

        private static string _baseDirectory;
        private static string _processedDirectory;
        private static string _noteBurnerDirectory;
        private static string _mixedInKeyDirectory;
        private List<string> _playlistsToSkip = new List<string>();
        private readonly string _downloadPlaylist;

        private static string _homeAssistantBackupsDirectory;
        private static string _dropboxBackupsDirectory;

        public Application(IMqttClient mqttClient, MqttFactory mqttFactory)
        {
            _mqttClient = mqttClient;
            _mqttFactory = mqttFactory;

            var playlistsSection = (NameValueCollection)ConfigurationManager.GetSection("spotifyPlaylists");
            foreach (var key in playlistsSection.AllKeys)
                _playlistNamesJackson.TryAdd(key!, playlistsSection[key]!);

            var directorySection = (NameValueCollection)ConfigurationManager.GetSection("directory");
            _baseDirectory = directorySection["baseDirectory"]!;
            _processedDirectory = _baseDirectory + directorySection["processed"];
            _noteBurnerDirectory = _baseDirectory + directorySection["noteBurner"];
            _mixedInKeyDirectory = _baseDirectory + directorySection["mixedInKey"];

            var playlistsToSkipSection = (NameValueCollection)ConfigurationManager.GetSection("spotifyPlaylistsToSkip");
            foreach (var key in playlistsToSkipSection.AllKeys)
                _playlistsToSkip.Add(playlistsToSkipSection[key]!);
            _downloadPlaylist = playlistsToSkipSection["temp"]!;

            _homeAssistantBackupsDirectory = directorySection["homeAssistantBackups"]!;
            _dropboxBackupsDirectory = directorySection["dropboxBackups"]!;
        }

        public async Task LoginToSpotify()
        {
            var spotifyUsersSection = (NameValueCollection)ConfigurationManager.GetSection("spotifyUsers");
            var tokenFilenameJackson = spotifyUsersSection["tokenFilenameJackson"]!;
            _spotifyJackson = await GetSpotify(tokenFilenameJackson);
        }

        private static async Task<Spotify> GetSpotify(string tokenFilename)
        {
            var spotifyAuth = new SpotifyAuthorization(tokenFilename);
            var spotifyClient = await spotifyAuth.Login();
            return new Spotify(spotifyClient);
        }

        public async Task Connect(List<string> topics)
        {
            _topics = topics;
            var mqttSection = (NameValueCollection)ConfigurationManager.GetSection("mqtt");
            var tcpServer = mqttSection["tcpServer"];
            var port = int.Parse(mqttSection["tcpServerPort"]!);
            var username = mqttSection["username"];
            var password = mqttSection["password"];
            _mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(tcpServer, port)
                .WithCredentials(username, password)
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var message = e.ApplicationMessage;
                var payload = GetPayload(message);
                Console.WriteLine($"Message received. Topic: {message.Topic}, Payload: {payload}");
                switch (message.Topic)
                {
                    case "SpotifyCurrentlyPlaying/Add":
                        try 
                        { 
                            var response = await SpotifyCurrentlyPlayingToPlaylist(payload);
                            await PublishMessage("SpotifyCurrentlyPlaying/Result", response);
                            Console.WriteLine(response);
                        }
                        catch (Exception ex)
                            { await PublishMessage("SpotifyCurrentlyPlaying/Error", $"{DateTime.Now.ToShortTimeString()}\rERROR adding track to playlist(s)\r{ex.Message}"); }
                        break;
                    case "SpotifyControl":
                        try
                        {
                            var response = await ControlSpotifyPlayer(payload);
                            Console.WriteLine($"Jackson's Spotify {response}");
                        }
                        catch (Exception ex)
                            { await PublishMessage("SpotifyControl/Error", $"ERROR controlling Spotify player.\r{ex.Message}"); }
                        break;
                    case "PlaySpotifyInKitchen":
                        try { await PlaySpotifyInKitchen(payload); }
                        catch (Exception ex)
                            { await PublishMessage("PlaySpotifyInKitchen/Error", $"ERROR starting playback on Kitchen Echo Dot\r{ex.Message}"); }
                        break;
                    case "PopulateSpotifyDownloadPlaylist":
                        try
                        {
                            var result = new StringBuilder();
                            try 
                            { 
                                result.AppendLine(await ProcessRecentlyDownloadedTracks()); 
                            }
                            catch (Exception ex)
                            { 
                                await PublishMessage("PopulateSpotifyDownloadPlaylist/Error", $"ERROR processing downloaded tracks. Download playlist not populated.\r{ex.Message}");
                                break;
                            }
                            var downloadedTracks = GetDownloadedTracks();
                            var allTracksForDownload = await GetAllTracksForDownload();
                            var notDownloadedTracks = GetNotDownloadedTracks(allTracksForDownload, downloadedTracks);
                            int tracksAdded = await AddTracksToDownloadPlaylist(notDownloadedTracks);
                            foreach (var playlist in _playlistCacheJackson.Values)
                                playlist.ContainsTracksToDownload = false;
                            result.AppendLine($"Added {tracksAdded} tracks to your download playlist.");
                            var resultString = result.ToString();
                            Console.WriteLine(resultString);
                            await PublishMessage("PopulateSpotifyDownloadPlaylist/Success", resultString);
                            if (tracksAdded > 0)
                                await PublishMessage("TracksToDownload", tracksAdded.ToString());
                        }
                        catch (Exception ex)
                            { await PublishMessage("PopulateSpotifyDownloadPlaylist/Error", $"ERROR populating download playlist\r{ex.Message}"); }
                        break;
                    case "Backup/CopyToDropbox":
                        try
                        {
                            int copiedFiles = CopyBackupsToDropbox();
                            if (copiedFiles < 1)
                                await PublishMessage("Backup/Error", "ALERT! No new home assistant backup files.");
                        }
                        catch (Exception ex)
                            { await PublishMessage("Backup/Error", $"ERROR occured while copying backups to Dropbox]r{ex.Message}"); }
                        break;
                }
            };

            _mqttClient.DisconnectedAsync += async e =>
            {
                Console.WriteLine("Disconnected from MQTT server. Attempting reconnection.");
                while (!_mqttClient.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await ConnectAndSubscribe();
                }
            };

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Connected to MQTT server.");
            };

            await ConnectAndSubscribe();
        }

        private async Task ConnectAndSubscribe()
        {
            await _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);

            foreach (var topic in _topics)
            {
                var mqttSubscribeOptions = _mqttFactory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => { f.WithTopic(topic); })
                    .Build();
                await _mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
                Console.WriteLine($"MQTT client subscribed to {topic}.");
            }
        }

        public async Task PublishMessage(string topic, string? payload = null)
        {
            MqttApplicationMessage applicationMessage;
            if (!string.IsNullOrEmpty(payload))
            {
                applicationMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .Build();
            }
            else
            {
                applicationMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .Build();
            }
            await _mqttClient.PublishAsync(applicationMessage, CancellationToken.None);
        }

        private async Task<string> ControlSpotifyPlayer(string command)
        {
            switch (command)
            {
                case "togglePlay":
                    return await _spotifyJackson.TogglePlay();
                case "next":
                    await _spotifyJackson.Next();
                    return "skipped next";
                case "previous":
                    await _spotifyJackson.Previous();
                    return "skipped previous";
                default:
                    return "not supported";
            }
        }

        private int CopyBackupsToDropbox()
        {
            int copiedFiles = 0;
            var haDirectoryInfo = new DirectoryInfo(_homeAssistantBackupsDirectory);
            IEnumerable<FileInfo> haFiles = haDirectoryInfo.GetFiles();
            foreach (var file in haFiles)
            {
                if (file.CreationTime < DateTime.Now.AddDays(-30))
                {
                    file.Delete();
                    continue;
                }
                var newPath = Path.Combine(_dropboxBackupsDirectory, file.Name);
                if (!System.IO.File.Exists(newPath))
                {
                    System.IO.File.Copy(file.FullName, newPath);
                    copiedFiles++;
                }
            }
            var dbDirectoryInfo = new DirectoryInfo(_dropboxBackupsDirectory);
            IEnumerable<FileInfo> dbFiles = dbDirectoryInfo.GetFiles();
            foreach (var file in dbFiles)
            {
                if (file.LastWriteTime < DateTime.Now.AddDays(-30))
                    file.Delete();
            }
            Console.WriteLine($"\rCopied {copiedFiles} backup files to Dropbox.\r");
            return copiedFiles;
        }

        private async Task<Dictionary<string, TrackItem>> GetAllTracksForDownload()
        {
            var allTracks = new Dictionary<string, TrackItem>();
            var savedTracks = await _spotifyJackson.GetCurrentUserSavedTracks();
            foreach (var track in savedTracks)
                allTracks.TryAdd(track.Id, new TrackItem(track));
            var targetPlaylists = await GetPlaylistsForDownload(_playlistCacheJackson);
            foreach (var playlist in targetPlaylists)
            {
                var fullTracks = await GetPlaylistFullTracks(playlist.Id);
                var playlistTracks = fullTracks.Select(t => new TrackItem(t));
                UpdatePlaylistCache(playlist, fullTracks);
                foreach (var track in playlistTracks)
                    allTracks.TryAdd(track.Id, track);
            }
            return allTracks;
        }

        private void UpdatePlaylistCache(SimplePlaylist playlist, List<FullTrack> tracks)
        {
            var trackItems = tracks.Select(t => new TrackItem(t));
            if (!_playlistCacheJackson.TryGetValue(playlist.Id, out var cachedPlaylist))
            {
                _playlistCacheJackson[playlist.Id] = new PlaylistItem(playlist, trackItems);
            }
            else
            {
                cachedPlaylist.SnapshotId = playlist.SnapshotId;
                cachedPlaylist.Tracks = trackItems;
                cachedPlaylist.ContainsTracksToDownload = true;
            }
        }

        private void AddTrackToPlaylistCache(string playlistId, string snapshotId, FullTrack track)
        {
            var cachedPlaylist = _playlistCacheJackson[playlistId];
            cachedPlaylist.SnapshotId = snapshotId;
            cachedPlaylist.Tracks = cachedPlaylist.Tracks.Append(new TrackItem(track));
            cachedPlaylist.ContainsTracksToDownload = true;
        }

        public async Task<string> SpotifyCurrentlyPlayingToPlaylist(string payload)
        {
            var response = new StringBuilder();
            var currentlyPlaying = await _spotifyJackson.GetCurrentlyPlaying();
            if (currentlyPlaying.Item is not FullTrack ft)
                return "Currently playing track is not a \"FullTrack\"";

            var trackName = NamingHelper.GetFilename(ft);
            response.AppendLine(trackName);

            if (payload == "Like")
            {
                var likedTracks = await _spotifyJackson.GetCurrentUserSavedTracks();
                if (!TrackAlreadySaved(likedTracks, ft))
                {
                    await _spotifyJackson.SaveTrack(new List<string>() { ft.Id });
                    response.Append("Saved to your library.");
                }
                else
                    response.Append("Already exists in your library.");
            }
            else
            {
                var allPlaylists = await _spotifyJackson.GetCurrentUsersPlaylists();
                foreach (var playlistName in payload.Split(','))
                {
                    var playlist = allPlaylists.Find(p => p.Name == playlistName);
                    IEnumerable<string> playlistTrackIds;
                    if (_playlistCacheJackson.TryGetValue(playlist!.Id, out var cachedPlaylist) && cachedPlaylist.SnapshotId == playlist.SnapshotId)
                        playlistTrackIds = cachedPlaylist.Tracks.Select(t => t.Id);
                    else
                    {
                        var tracks = await _spotifyJackson.GetPlaylistFullTracks(playlist.Id);
                        playlistTrackIds = tracks.Select(t => t.Id);
                        UpdatePlaylistCache(playlist, tracks);
                    }

                    if (!playlistTrackIds.Contains(ft.Id) && (ft.LinkedFrom == null || !playlistTrackIds.Contains(ft.LinkedFrom.Id)))
                    {
                        var snapshotResponse = await _spotifyJackson.AddToPlaylist(playlist.Id, new List<string>() { ft.Uri });
                        AddTrackToPlaylistCache(playlist.Id, snapshotResponse!.SnapshotId, ft);
                        response.AppendLine($"Added to {playlistName}.");
                    }
                    else
                        response.AppendLine($"{playlistName} already contains track.");

                }
            }
            return response.ToString();
        }

        private bool TrackAlreadySaved(List<FullTrack> tracks, FullTrack track)
        {
            var trackIds = tracks.Select(t => t.Id);
            return (trackIds.Contains(track.Id) || (track.LinkedFrom != null && trackIds.Contains(track.LinkedFrom.Id)));
        }

        public async Task PlaySpotifyInKitchen(string payload)
        {
            if (payload == "Jackson")
                await _spotifyJackson.StartPlayback("Kitchen Echo Dot", new List<string>() { $"spotify:playlist:{_playlistNamesJackson["Electronic"]}" });
            return;
        }

        private string GetPayload(MqttApplicationMessage message)
        {
            var payloadBytes = message.Payload;
            return payloadBytes == null ? string.Empty : Encoding.UTF8.GetString(message.Payload, 0, payloadBytes.Length);
        }

        public Dictionary<string, string> GetDownloadedTracks()
        {
            var downloadedTracks = new Dictionary<string, string>();
            var processed = MP3Helper.GetFilenameByTrackId(_processedDirectory);
            var mixedInKey = MP3Helper.GetFilenameByTrackId(_mixedInKeyDirectory);
            foreach (var (id, file) in processed)
                downloadedTracks.TryAdd(id, file);
            foreach (var (id, file) in mixedInKey)
                downloadedTracks.TryAdd(id, file);
            return downloadedTracks;
        }

        public async Task<List<FullTrack>> GetPlaylistFullTracks(string playlistId)
        {
            return await _spotifyJackson.GetPlaylistFullTracks(playlistId);
        }

        public async Task<List<SimplePlaylist>> GetPlaylistsForDownload(Dictionary<string, PlaylistItem> playlistCache)
        {
            var targetPlaylists = new List<SimplePlaylist>();
            var user = await _spotifyJackson.GetCurrentUser();
            var playlists = await _spotifyJackson.GetCurrentUsersPlaylists();
            foreach (var playlist in playlists)
            {
                if (playlist.Owner.Id != user.Id || playlist.Collaborative || _playlistsToSkip.Contains(playlist.Id))
                    continue;

                if (!playlistCache.TryGetValue(playlist.Id, out var cachedPlaylist) || playlist.SnapshotId != cachedPlaylist.SnapshotId || cachedPlaylist.ContainsTracksToDownload)
                    targetPlaylists.Add(playlist);
            }
            return targetPlaylists;
        }

        private async Task<List<FullTrack>> GetDownloadPlaylistTracks()
        {
            return await _spotifyJackson.GetPlaylistFullTracks(_downloadPlaylist);
        }

        public static List<TrackItem> GetNotDownloadedTracks(Dictionary<string, TrackItem> allTracks, Dictionary<string, string> downloadedTracksByTrackId)
        {
            var notDownloadedTracks = new List<TrackItem>();
            foreach (var (id, track) in allTracks)
            {
                if (downloadedTracksByTrackId.ContainsKey(id))
                    continue;

                if (track.LinkedTrack != null && downloadedTracksByTrackId.TryGetValue(track.LinkedTrack.Id, out var existingFile))
                {
                    var existingPath = Path.Combine(_processedDirectory, $"{existingFile}.mp3");
                    AddIdToFile(existingPath, track.LinkedTrack.Id);
                    continue;
                }

                var filename = NamingHelper.GetFilename(track);
                var path = Path.Combine(_processedDirectory, $"{filename}.mp3");
                if (System.IO.File.Exists(path))
                {
                    AddIdToFile(path, track.Id);
                    continue;
                }

                if (track.IsPlayable)
                    notDownloadedTracks.Add(track);
            }

            return notDownloadedTracks;
        }

        private static void AddIdToFile(string path, string id)
        {
            using var file = TagLib.File.Create(path);
            var ids = file.Tag.Comment.Split(',');
            if (!ids.Contains(id))
            {
                file.Tag.Comment = string.Join(",", file.Tag.Comment, id);
                file.Save();
            }
        }

        public async Task<int> AddTracksToDownloadPlaylist(List<TrackItem> notDownloadedTracks)
        {
            var downloadPlaylistTracks = await GetDownloadPlaylistTracks();

            var downloadPlaylistTrackIds = new HashSet<string>();
            foreach (var downloadTrack in downloadPlaylistTracks)
                downloadPlaylistTrackIds.Add(downloadTrack.Id);

            var tracksToAdd = new List<string>();
            foreach (var track in notDownloadedTracks)
            {
                if (!downloadPlaylistTrackIds.Contains(track.Id))
                    tracksToAdd.Add(track.Uri);
            }

            await _spotifyJackson.AddToPlaylist(_downloadPlaylist, tracksToAdd);

            return tracksToAdd.Count;
        }

        public async Task<string> ProcessRecentlyDownloadedTracks()
        {
            var tracksToRemove = new List<string>();
            var recentlyDownloadedFiles = MP3Helper.GetTrackMetadata(_noteBurnerDirectory);

            if (!recentlyDownloadedFiles.Any())
                return "No downloaded tracks to process.";

            try
            {
                var playlistTracks = await GetDownloadPlaylistTracks();

                foreach (var file in recentlyDownloadedFiles)
                {
                    var downloadedFilename = NamingHelper.GetFilename(file);

                    foreach (var track in playlistTracks)
                    {
                        var trackFilename = NamingHelper.GetFilename(track);

                        if (downloadedFilename == trackFilename)
                        {
                            file.Tag.Comment = track.Id;
                            file.Save();
                            var oldPath = file.Name;
                            var newPath = Path.Combine(_mixedInKeyDirectory, $"{downloadedFilename}.mp3");
                            if (!System.IO.File.Exists(newPath))
                            {
                                System.IO.File.Move(oldPath, newPath);
                                file.Mode = TagLib.File.AccessMode.Closed;
                            }
                            else
                            {
                                file.Mode = TagLib.File.AccessMode.Closed;
                                System.IO.File.Delete(oldPath);
                            }
                            tracksToRemove.Add(track.Uri);
                            break;
                        }
                    }
                }
            }
            finally
            {
                await _spotifyJackson.RemoveFromPlaylist(_downloadPlaylist, tracksToRemove);
            }

            return $"Processed {tracksToRemove.Count}/{recentlyDownloadedFiles.Count} downloaded tracks.";
        }
    }
}
