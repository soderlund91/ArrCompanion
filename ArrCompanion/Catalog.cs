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
            // From the poster lookup (TMDb), once it has been made:
            public string Language { get; set; } = "";      // original language, ISO 639-1; "" = not known (yet)
            public string LanguageName { get; set; } = "";  // its English name: "Chinese"
            public List<string> Countries { get; set; } = new List<string>();   // origin countries, by name
            public bool HasDetails { get; set; }            // looked up (a language may still be unknown)
            // How well it matches what the server watched, 0–100; null = not known.
            public int? Match { get; set; }
            public string MatchReason { get; set; } = "";
        }

        public sealed class Result
        {
            public MissingList.Snapshot File { get; set; } = new MissingList.Snapshot();
            public List<Row> Rows { get; set; } = new List<Row>();
            public List<string> Lists { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();   // a server that could not be read
            public bool Refreshing { get; set; }    // built from the last stored Sonarr / Radarr lists; newer ones are being read
            public bool Enriching { get; set; }     // titles' details or the taste profile are being looked up / built
            public bool HasMatch { get; set; }      // a taste profile exists: rows carry Match
        }

        /// <summary>
        /// stored: label with the last Sonarr / Radarr lists the plugin kept (instant), and with
        /// refresh, read them again in the background (Refreshing says so). Otherwise wait for
        /// current lists, as an add and the rules do.
        /// </summary>
        internal static async Task<Result> BuildAsync(PluginConfiguration config, IJsonSerializer json, CancellationToken ct, bool stored = false, bool refresh = false)
        {
            var result = new Result { File = MissingList.Read(config, json) };
            var titles = result.File.Titles;
            result.File.Titles = new List<MissingList.Title>();   // sent once, as the rows
            result.Lists = titles.SelectMany(t => t.Places.Select(p => p.List)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase).ToList();

            var client = new ArrClient(json);
            var reads = new List<(string Kind, ArrInstance Instance, Task<(ArrClient.Holdings? Holdings, string? Error, bool Stored)> Read)>();
            foreach (var kind in new[] { ArrKind.Radarr, ArrKind.Sonarr })
            {
                var instance = config.InstanceFor(kind);
                if (instance == null || titles.All(t => ArrKind.For(t.Type) != kind)) continue;
                reads.Add((kind, instance, HoldingsAsync(client, instance, stored, refresh, ct)));   // both servers at once
            }
            var holdings = new Dictionary<string, ArrClient.Holdings?>();
            foreach (var r in reads)
            {
                var (held, error, fromStore) = await r.Read.ConfigureAwait(false);
                holdings[r.Kind] = held;
                if (error != null) result.Errors.Add(error);
                if (fromStore && ArrClient.IsRefreshing(r.Instance)) result.Refreshing = true;
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
            AddDetails(result, json);
            result.Rows = result.Rows.OrderBy(r => r.BestRank ?? int.MaxValue).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }

        private static async Task<(ArrClient.Holdings? Holdings, string? Error, bool Stored)> HoldingsAsync(ArrClient client, ArrInstance instance, bool stored, bool refresh, CancellationToken ct)
        {
            if (stored)
            {
                var kept = client.Stored(instance);
                if (kept != null)
                {
                    if (refresh) client.RefreshInBackground(instance);
                    return (new ArrClient.Holdings(kept), null, true);
                }
            }
            try { return (new ArrClient.Holdings(await client.LibraryAsync(instance, ct).ConfigureAwait(false)), null, false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Not reachable now: the last list it gave still tells what it holds.
                var kept = client.Stored(instance);
                return (kept != null ? new ArrClient.Holdings(kept) : null, $"{instance.Label} could not be read: {ex.Message}", false);
            }
        }

        /// <summary>Language, countries and match from what is kept: nothing is asked here, so the tab stays instant.</summary>
        private static void AddDetails(Result result, IJsonSerializer json)
        {
            var known = Posters.Known(result.Rows.Select(r => r.Key), json);
            var profile = Taste.Current(json);
            result.HasMatch = profile != null && profile.Titles > 0;
            foreach (var row in result.Rows)
            {
                if (!known.TryGetValue(row.Key, out var d)) continue;
                row.HasDetails = true;
                row.Language = d.Language;
                row.LanguageName = Languages.Name(d.Language);
                row.Countries = d.Countries.Select(Languages.CountryName).ToList();
                var match = Taste.Score(profile, d);
                if (match != null) { row.Match = match.Score; row.MatchReason = match.Reason; }
            }
        }

        internal static MissingList.Title ToTitle(Row r) => new MissingList.Title
        {
            Key = r.Key, Type = r.Type, Name = r.Title, Year = r.Year, Imdb = r.Imdb, Tmdb = r.Tmdb, Tvdb = r.Tvdb, Places = r.Places, BestRank = r.BestRank
        };
    }
}
