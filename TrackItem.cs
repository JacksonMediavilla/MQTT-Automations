using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MQTT_Automations
{
    public class TrackItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> ArtistNames { get; set; }
        public bool IsPlayable { get; set; }
        public string Uri { get; set; }
        public LinkedTrack LinkedTrack { get; set; }
        public TrackItem(FullTrack track)
        {
            Id = track.Id;
            Name = track.Name;
            ArtistNames = track.Artists.Select(a => a.Name).ToList();
            IsPlayable = track.IsPlayable;
            Uri = track.Uri;
            LinkedTrack = track.LinkedFrom;
        }
    }
}
