using SpotifyAPI.Web;

namespace MQTT_Automations
{
    internal class PlaylistItem
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string SnapshotId { get; set; }
        public IEnumerable<TrackItem> Tracks { get; set; }
        public bool ContainsTracksToDownload { get; set; } = true;

        public PlaylistItem(SimplePlaylist playlist, IEnumerable<TrackItem> tracks)
        {
            Name = playlist.Name;
            Id = playlist.Id;
            SnapshotId = playlist.SnapshotId;
            Tracks = tracks;
        }
    }
}
