using System.Diagnostics;

namespace MQTT_Automations
{
    public static class MP3Helper
    {
        public static List<TagLib.File> GetTrackMetadata(string directory, DateTime? lastRun = null)
        {
            var mp3s = new List<TagLib.File>();
            var directoryInfo = new DirectoryInfo(directory);
            IEnumerable<FileInfo> files = directoryInfo.GetFiles();
            if (lastRun != null)
                files = files.Where(f => f.LastWriteTime > lastRun);
            foreach (var file in files)
            {
                if (file.Extension != ".mp3")
                    continue;
                var tfile = TagLib.File.Create(file.FullName);
                mp3s.Add(tfile);
            }
            return mp3s;
        }

        public static Dictionary<string, string> GetFilenameByTrackId(string directory, DateTime? lastRun = null)
        {
            var mp3s = new Dictionary<string, string>();
            var directoryInfo = new DirectoryInfo(directory);
            IEnumerable<FileInfo> files = directoryInfo.GetFiles();
            if (lastRun != null)
                files = files.Where(f => f.LastWriteTime > lastRun);
            foreach (var file in files)
            {
                if (file.Extension != ".mp3")
                    continue;
                using var tfile = TagLib.File.Create(file.FullName);
                var comment = tfile.Tag.Comment;
                if (string.IsNullOrEmpty(comment))
                    continue;
                foreach (var id in comment.Split(','))
                    mp3s.Add(id, NamingHelper.GetFilename(tfile));
            }
            return mp3s;
        }
    }
}
