using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// The missing titles with where each one stands: still missing, already in Sonarr / Radarr,
    /// added by the plugin, or ignored. Both the page and the rules read it.
    /// </summary>
    internal static class Catalog
    {
        internal static class Status
        {
            public const string Missing = "missing";
            public const string InArr = "inArr";       // the server already holds it
            public const string Ignored = "ignored";
            public const string NoServer = "noServer"; // no Sonarr / Radarr set up for its kind
        }

        public sealed class Row
        {
            public string Key { get; set; } = "";
            public string Type { get; set; } = "";
            public string Title { get; set; } = "";
            public int? Year { get; set; }
            public string Imdb { get; set; } = "";
            public int Tmdb { get; set; }
            public int Tvdb { get; set; }
            public List<MissingList.Place> Places { get; set; } = new List<MissingList.Place>();
            public int? BestRank { get; set; }
            public string Status { get; set; } = Catalog.Status.Missing;
            public bool AddedBefore { get; set; }       // in the history: added (or found there) by the plugin
            public string InstanceId { get; set; } = "";
            // Already in Sonarr / Radarr: how far along it is there.
            public bool ArrMonitored { get; set; }
            public bool ArrDownloaded { get; set; }     // Radarr: the movie has its file
            public int ArrEpisodeFiles { get; set; }    // Sonarr: episodes it has a file of …
            public int ArrEpisodes { get; set; }        // … of its monitored, aired episodes
        }

        public sealed class Result
        {
            public MissingList.Snapshot File { get; set; } = new MissingList.Snapshot();
            public List<Row> Rows { get; set; } = new List<Row>();
            public List<string> Lists { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();   // a server that could not be read
        }

        internal static async Task<Result> BuildAsync(PluginConfiguration config, IJsonSerializer json, CancellationToken ct)
        {
            var result = new Result { File = MissingList.Read(config, json) };
            var titles = result.File.Titles;
            result.File.Titles = new List<MissingList.Title>();   // sent once, as the rows
            result.Lists = titles.SelectMany(t => t.Places.Select(p => p.List)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase).ToList();

            var client = new ArrClient(json);
            var holdings = new Dictionary<string, ArrClient.Holdings?>();
            foreach (var kind in new[] { ArrKind.Radarr, ArrKind.Sonarr })
            {
                var instance = config.InstanceFor(kind);
                if (instance == null || titles.All(t => ArrKind.For(t.Type) != kind)) continue;
                try { holdings[kind] = new ArrClient.Holdings(await client.LibraryAsync(instance, ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    holdings[kind] = null;
                    result.Errors.Add($"{instance.Label} could not be read: {ex.Message}");
                }
            }

            var ignored = new HashSet<string>(config.Ignored ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var added = History.AddedKeys(json);
            foreach (var t in titles)
            {
                var kind = ArrKind.For(t.Type);
                var instance = config.InstanceFor(kind);
                var row = new Row
                {
                    Key = t.Key, Type = t.Type, Title = t.Name, Year = t.Year, Imdb = t.Imdb, Tmdb = t.Tmdb, Tvdb = t.Tvdb,
                    Places = t.Places, BestRank = t.BestRank, AddedBefore = added.Contains(t.Key), InstanceId = instance?.Id ?? ""
                };
                var held = instance != null && holdings.TryGetValue(kind, out var h) && h != null ? h.Find(t) : null;
                if (held != null)
                {
                    row.Status = Status.InArr;
                    row.ArrMonitored = held.monitored;
                    row.ArrDownloaded = held.hasFile;
                    row.ArrEpisodeFiles = held.statistics?.episodeFileCount ?? 0;
                    row.ArrEpisodes = held.statistics?.episodeCount ?? 0;
                }
                else if (ignored.Contains(t.Key)) row.Status = Status.Ignored;
                else if (instance == null) row.Status = Status.NoServer;
                result.Rows.Add(row);
            }
            result.Rows = result.Rows.OrderBy(r => r.BestRank ?? int.MaxValue).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }

        internal static MissingList.Title ToTitle(Row r) => new MissingList.Title
        {
            Key = r.Key, Type = r.Type, Name = r.Title, Year = r.Year, Imdb = r.Imdb, Tmdb = r.Tmdb, Tvdb = r.Tvdb, Places = r.Places, BestRank = r.BestRank
        };
    }
}
