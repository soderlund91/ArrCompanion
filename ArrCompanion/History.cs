using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArrCompanion
{
    /// <summary>
    /// What the plugin added (or tried to), by hand or by a rule, in the plugin's data folder.
    /// A title a rule has added once is never added by a rule again, so one removed from Sonarr
    /// or Radarr stays removed.
    /// </summary>
    internal static class History
    {
        private const int Keep = 1000;
        private static readonly object Gate = new object();
        private static List<Entry>? _entries;

        public sealed class FileShape
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        public sealed class Entry
        {
            public DateTime AtUtc { get; set; }
            public string Key { get; set; } = "";
            public string Title { get; set; } = "";
            public int? Year { get; set; }
            public string Type { get; set; } = "";
            public string InstanceId { get; set; } = "";
            public string InstanceName { get; set; } = "";
            public string By { get; set; } = "";       // "manual" or the rule's name
            public string List { get; set; } = "";     // the place that made a rule pick it
            public int? Rank { get; set; }
            public bool Success { get; set; }
            public bool AlreadyIn { get; set; }
            public string Message { get; set; } = "";
        }

        private static string FilePath => Path.Combine(Plugin.Instance.DataFolderPath, "history.json");

        private static List<Entry> Entries(IJsonSerializer json)
        {
            if (_entries != null) return _entries;
            try { _entries = File.Exists(FilePath) ? json.DeserializeFromFile<FileShape>(FilePath)?.Entries : null; }
            catch (Exception ex) { Log.Warn("The history could not be read: " + ex.Message); }
            return _entries ??= new List<Entry>();
        }

        internal static void Add(IEnumerable<Entry> added, IJsonSerializer json)
        {
            lock (Gate)
            {
                var list = Entries(json);
                list.AddRange(added);
                if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                json.SerializeToFile(new FileShape { Entries = list }, FilePath);
            }
        }

        /// <summary>Newest first.</summary>
        internal static List<Entry> Recent(int count, IJsonSerializer json)
        {
            lock (Gate) return Entries(json).AsEnumerable().Reverse().Take(count).ToList();
        }

        /// <summary>The titles that were added (or found already there), by key.</summary>
        internal static HashSet<string> AddedKeys(IJsonSerializer json)
        {
            lock (Gate)
                return new HashSet<string>(Entries(json).Where(e => e.Success || e.AlreadyIn).Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        }
    }
}
