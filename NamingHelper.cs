using SpotifyAPI.Web;

namespace MQTT_Automations
{
    public static class NamingHelper
    {
        public static string GetFilename(TagLib.File file)
        {
            var artists = file.Tag.Performers[0];
            var title = file.Tag.Title;
            return GetFilename(artists, title);
        }

        public static string GetFilename(FullTrack track)
        {
            var artists = string.Join(", ", track.Artists.Select(a => a.Name));
            var title = track.Name;
            return GetFilename(artists, title);
        }

        public static string GetFilename(TrackItem track)
        {
            var artists = string.Join(", ", track.ArtistNames);
            var title = track.Name;
            return GetFilename(artists, title);
        }

        private static string GetFilename(string artists, string title)
        {
            var filename = $"{artists} - {title}";
            return string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
        }
    }
}
