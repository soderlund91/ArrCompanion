using MediaBrowser.Common;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArrCompanion
{
    /// <summary>
    /// The missing titles file Home Screen Companion keeps (its Missing tab, "Keep the list in a
    /// file" with JSON): the titles on its lists that are not in the library, with their places.
    /// </summary>
    internal static class MissingList
    {
        internal const string FileName = "missing_titles.json";
        private static readonly Guid HscId = new Guid("7c10708f-43e4-4d69-923c-77d01802315b");

        // ---- The file, as Home Screen Companion writes it ----

        public sealed class FileShape
        {
            public string? generated { get; set; }
            public List<FileTitle>? movies { get; set; }
            public List<FileTitle>? shows { get; set; }
        }

        public sealed class FileTitle
        {
            public string? title { get; set; }
            public int? year { get; set; }
            public string? imdb_id { get; set; }
            public int? tmdb_id { get; set; }
            public int? tvdb_id { get; set; }
            public bool in_library { get; set; }
            public List<string>? lists { get; set; }
            public List<FileRank>? ranks { get; set; }
        }

        public sealed class FileRank
        {
            public string? list { get; set; }
            public int? rank { get; set; }
            public int of { get; set; }
        }

        // ---- What the plugin works with ----

        public sealed class Title
        {
            public string Key { get; set; } = "";
            public string Type { get; set; } = MediaType.Movie;
            public string Name { get; set; } = "";
            public int? Year { get; set; }
            public string Imdb { get; set; } = "";
            public int Tmdb { get; set; }
            public int Tvdb { get; set; }
            public List<Place> Places { get; set; } = new List<Place>();
            public int? BestRank { get; set; }
        }

        public sealed class Place
        {
            public string List { get; set; } = "";
            public int? Rank { get; set; }
            public int Of { get; set; }
        }

        public sealed class Snapshot
        {
            public string Path { get; set; } = "";
            public bool Found { get; set; }
            public string? Problem { get; set; }       // why there are no titles, for the page
            public string? GeneratedUtc { get; set; }
            public List<Title> Titles { get; set; } = new List<Title>();
        }

        /// <summary>One key per title, the same over runs: by IMDb id, else the TMDb / TVDB id, else name and year.</summary>
        internal static string TitleKey(string type, string? imdb, int tmdb, int tvdb, string? name, int? year)
        {
            if (!string.IsNullOrWhiteSpace(imdb)) return "imdb:" + imdb!.Trim().ToLowerInvariant();
            if (type == MediaType.Show && tvdb > 0) return "tvdb:" + tvdb;
            if (tmdb > 0) return "tmdb:" + type + ":" + tmdb;
            var norm = new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return "name:" + type + ":" + norm + "|" + year;
        }

        // ---- Where the file is ----

        private static IApplicationHost? _appHost;
        internal static void Initialize(IApplicationHost appHost) => _appHost = appHost;

        /// <summary>The Home Screen Companion plugin's export settings, read without a reference to it.</summary>
        internal sealed class HscExport
        {
            public bool Installed;
            public string Folder = "";
            public string SourcesPath = "";   // what each list held, in its order (missing_titles/sources.json)
            public string TmdbApiKey = "";
            public bool On;     // its Missing tab with "Keep the list in a file" and JSON on
        }

        internal static HscExport Hsc()
        {
            var result = new HscExport();
            try
            {
                var plugin = _appHost?.Plugins?.FirstOrDefault(p => p.Id == HscId);
                if (plugin == null) return result;
                result.Installed = true;
                result.Folder = Path.Combine(plugin.DataFolderPath, "missing_titles", "export");
                result.SourcesPath = Path.Combine(plugin.DataFolderPath, "missing_titles", "sources.json");
                var config = plugin.GetType().GetProperty("Configuration")?.GetValue(plugin);
                if (config == null) return result;
                bool Flag(string name) => config.GetType().GetProperty(name)?.GetValue(config) as bool? ?? false;
                var folder = config.GetType().GetProperty("MissingExportFolder")?.GetValue(config) as string;
                if (!string.IsNullOrWhiteSpace(folder)) result.Folder = folder!.Trim();
                result.TmdbApiKey = ((config.GetType().GetProperty("TmdbApiKey")?.GetValue(config) as string) ?? "").Trim();
                result.On = Flag("ShowMissingTitles") && Flag("MissingExportToFile") && Flag("MissingExportJson");
            }
            catch (Exception swallowed) { Log.Debug("Could not read Home Screen Companion's settings: " + swallowed.Message); }
            return result;
        }

        internal static string FilePath(PluginConfiguration config)
        {
            var own = (config.MissingListPath ?? "").Trim();
            if (own.Length > 0)
                return Directory.Exists(own) ? Path.Combine(own, FileName) : own;
            var hsc = Hsc();
            return hsc.Folder.Length > 0 ? Path.Combine(hsc.Folder, FileName) : "";
        }

        // ---- Reading it ----

        internal static Snapshot Read(PluginConfiguration config, IJsonSerializer json)
        {
            var snap = new Snapshot { Path = FilePath(config) };
            if (snap.Path.Length == 0)
            {
                snap.Problem = "Home Screen Companion is not installed. Install it, or set the path to a missing titles file under Settings.";
                return snap;
            }
            if (!File.Exists(snap.Path))
            {
                var hsc = (config.MissingListPath ?? "").Trim().Length == 0 ? Hsc() : null;
                snap.Problem = hsc != null && !hsc.On
                    ? "Turn on Home Screen Companion's Missing titles, with \"Keep the list in a file\" and JSON, then run its sync."
                    : "No file at " + snap.Path + " yet. It is written after Home Screen Companion's next sync.";
                return snap;
            }

            FileShape? file;
            try { file = json.DeserializeFromFile<FileShape>(snap.Path); }
            catch (Exception ex)
            {
                snap.Problem = "The file could not be read: " + ex.Message;
                return snap;
            }
            snap.Found = true;
            snap.GeneratedUtc = file?.generated;

            void Add(FileTitle f, string type)
            {
                // A show in the library is there for its missing episodes: not a whole series to add.
                if (f.in_library || string.IsNullOrWhiteSpace(f.title)) return;
                var t = new Title
                {
                    Type = type,
                    Name = f.title!.Trim(),
                    Year = f.year,
                    Imdb = (f.imdb_id ?? "").Trim(),
                    Tmdb = f.tmdb_id ?? 0,
                    Tvdb = type == MediaType.Show ? f.tvdb_id ?? 0 : 0
                };
                t.Key = TitleKey(type, t.Imdb, t.Tmdb, t.Tvdb, t.Name, t.Year);
                // A file from before places were written: the list names alone.
                t.Places = f.ranks != null && f.ranks.Count > 0
                    ? f.ranks.Where(r => !string.IsNullOrWhiteSpace(r.list)).Select(r => new Place { List = r.list!.Trim(), Rank = r.rank, Of = r.of }).ToList()
                    : (f.lists ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => new Place { List = l.Trim() }).ToList();
                snap.Titles.Add(t);
            }
            foreach (var m in file?.movies ?? new List<FileTitle>()) Add(m, MediaType.Movie);
            foreach (var s in file?.shows ?? new List<FileTitle>()) Add(s, MediaType.Show);

            if (snap.Titles.Any(t => t.Places.Any(p => p.Rank == null))) FillRanks(snap.Titles, json);
            foreach (var t in snap.Titles)
                t.BestRank = t.Places.Where(p => p.Rank != null).Select(p => p.Rank).DefaultIfEmpty(null).Min();
            return snap;
        }

        // ---- Places from Home Screen Companion's own record of its lists ----

        // missing_titles/sources.json: every list's entries in the list's order, as the last sync read them.
        public sealed class SourcesShape
        {
            public List<SourceShape>? Sources { get; set; }
        }

        public sealed class SourceShape
        {
            public string? Name { get; set; }
            public List<SourceItem>? Items { get; set; }
        }

        public sealed class SourceItem
        {
            public string? Name { get; set; }
            public string? Imdb { get; set; }
            public string? Tmdb { get; set; }
            public string? Type { get; set; }
            public int? Year { get; set; }
            public int? Rank { get; set; }
        }

        private static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>
        /// The places a file without them (an older Home Screen Companion) lacks: an entry's place
        /// is its position on the list. A list of several URLs gives a title its best place.
        /// </summary>
        private static void FillRanks(List<Title> titles, IJsonSerializer json)
        {
            var path = Hsc().SourcesPath;
            if (path.Length == 0 || !File.Exists(path)) return;
            SourcesShape? file;
            try { file = json.DeserializeFromFile<SourcesShape>(path); }
            catch (Exception ex) { Log.Debug("Could not read " + path + ": " + ex.Message); return; }

            // (list, id) -> (rank, of); ids are "imdb:", "tmdb:<type>:" and "name:<name>|<year>".
            var places = new Dictionary<string, (int Rank, int Of)>(StringComparer.OrdinalIgnoreCase);
            void Put(string list, string id, int rank, int of)
            {
                var k = list + " | " + id;
                if (!places.TryGetValue(k, out var had) || rank < had.Rank) places[k] = (rank, of);
            }
            foreach (var src in file?.Sources ?? new List<SourceShape>())
            {
                var list = (src.Name ?? "").Trim();
                var items = src.Items ?? new List<SourceItem>();
                if (list.Length == 0 || items.Count == 0) continue;
                int of = Math.Max(items.Count, items.Max(i => i.Rank) ?? 0);
                for (int n = 0; n < items.Count; n++)
                {
                    var i = items[n];
                    int rank = i.Rank ?? n + 1;
                    var type = i.Type == "show" ? MediaType.Show : i.Type == "movie" ? MediaType.Movie : "";
                    if (!string.IsNullOrWhiteSpace(i.Imdb)) Put(list, "imdb:" + i.Imdb!.Trim(), rank, of);
                    if (!string.IsNullOrWhiteSpace(i.Tmdb)) Put(list, "tmdb:" + type + ":" + i.Tmdb!.Trim(), rank, of);
                    Put(list, "name:" + Norm(i.Name) + "|" + i.Year, rank, of);
                }
            }

            foreach (var t in titles)
                foreach (var p in t.Places.Where(p => p.Rank == null))
                {
                    var ids = new List<string>();
                    if (t.Imdb.Length > 0) ids.Add("imdb:" + t.Imdb);
                    if (t.Tmdb > 0) { ids.Add("tmdb:" + t.Type + ":" + t.Tmdb); ids.Add("tmdb::" + t.Tmdb); }
                    ids.Add("name:" + Norm(t.Name) + "|" + t.Year);
                    foreach (var id in ids)
                        if (places.TryGetValue(p.List + " | " + id, out var found)) { p.Rank = found.Rank; p.Of = found.Of; break; }
                }
        }
    }
}
