using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// Sonarr's and Radarr's v3 API: the connection test, what each already holds, the add
    /// dialog's root folders and quality profiles, and adding a show or movie. The plugin calls
    /// them itself (X-Api-Key), so the key never reaches the browser.
    /// </summary>
    internal sealed class ArrClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

        // The full Sonarr / Radarr lists, kept briefly so the page and an add right after it
        // don't each download thousands of entries.
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, (DateTime At, List<LibraryEntry> List)> LibraryCache = new Dictionary<string, (DateTime, List<LibraryEntry>)>();
        private static readonly Dictionary<string, string> Versions = new Dictionary<string, string>();

        private readonly IJsonSerializer _json;

        internal ArrClient(IJsonSerializer json) => _json = json;

        // ---- JSON shapes ----

        public sealed class SystemStatus { public string? version { get; set; } }

        public sealed class LibraryEntry
        {
            public int id { get; set; }
            public int tvdbId { get; set; }
            public int tmdbId { get; set; }
            public string? imdbId { get; set; }
            public string? title { get; set; }
            public string? titleSlug { get; set; }
            public int year { get; set; }
            public List<SeasonEntry>? seasons { get; set; }
        }

        public sealed class SeasonEntry { public int seasonNumber { get; set; } }
        public sealed class RootFolder { public string? path { get; set; } public long? freeSpace { get; set; } }
        public sealed class Profile { public int id { get; set; } public string? name { get; set; } }

        public sealed class ValidationError
        {
            public string? propertyName { get; set; }
            public string? errorMessage { get; set; }
            public string? errorCode { get; set; }
            public string? attemptedValue { get; set; }
        }

        public sealed class AddResult
        {
            public bool Success { get; set; }
            public bool AlreadyIn { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>How to add: the server's defaults, or what the add dialog picked.</summary>
        internal sealed class AddOptions
        {
            public string RootFolderPath = "";
            public int QualityProfileId;
            public bool Monitored = true;
            public bool Search = true;
            public string MinimumAvailability = "released";
            public string SeriesMonitor = "all";
            public bool SeasonFolder = true;

            internal static AddOptions From(ArrInstance i) => new AddOptions
            {
                RootFolderPath = (i.RootFolderPath ?? "").Trim(),
                QualityProfileId = i.QualityProfileId,
                Monitored = i.Monitored,
                Search = i.SearchOnAdd,
                MinimumAvailability = string.IsNullOrWhiteSpace(i.MinimumAvailability) ? "released" : i.MinimumAvailability,
                SeriesMonitor = string.IsNullOrWhiteSpace(i.SeriesMonitor) ? "all" : i.SeriesMonitor,
                SeasonFolder = i.SeasonFolder
            };
        }

        // ---- HTTP ----

        private static async Task<(int Status, string Body)> Send(HttpMethod method, string baseUrl, string apiKey, string path, string? jsonBody, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(method, baseUrl + path))
            {
                req.Headers.Add("X-Api-Key", apiKey);
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using (var res = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ((int)res.StatusCode, body);
                }
            }
        }

        private async Task<T?> Get<T>(string baseUrl, string apiKey, string path, CancellationToken ct) where T : class
        {
            var (status, body) = await Send(HttpMethod.Get, baseUrl, apiKey, path, null, ct).ConfigureAwait(false);
            if (status == 401) throw new InvalidOperationException("HTTP 401: the API key was not accepted");
            if (status < 200 || status > 299) throw new InvalidOperationException("HTTP " + status);
            return _json.DeserializeFromString<T>(body);
        }

        private Task<T?> Get<T>(ArrInstance i, string path, CancellationToken ct) where T : class => Get<T>(i.BaseUrl, i.Key, path, ct);

        private static string Q(string? s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string B(bool b) => b ? "true" : "false";

        // ---- Test ----

        internal async Task<string> TestAsync(string url, string apiKey, CancellationToken ct)
        {
            var status = await Get<SystemStatus>(url.Trim().TrimEnd('/'), apiKey.Trim(), "/api/v3/system/status", ct).ConfigureAwait(false);
            return status?.version ?? "";
        }

        private async Task<string> VersionAsync(ArrInstance i, CancellationToken ct)
        {
            var key = i.BaseUrl + "|" + i.Key;
            lock (CacheLock) if (Versions.TryGetValue(key, out var v)) return v;
            var version = await TestAsync(i.BaseUrl, i.Key, ct).ConfigureAwait(false);
            lock (CacheLock) Versions[key] = version;
            return version;
        }

        // ---- What it holds ----

        internal async Task<List<LibraryEntry>> LibraryAsync(ArrInstance i, CancellationToken ct)
        {
            var cacheKey = i.Kind + "|" + i.BaseUrl + "|" + i.Key;
            lock (CacheLock)
            {
                if (LibraryCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
                    return hit.List;
            }
            var list = await Get<List<LibraryEntry>>(i, i.Kind == ArrKind.Sonarr ? "/api/v3/series" : "/api/v3/movie", ct).ConfigureAwait(false)
                ?? new List<LibraryEntry>();
            lock (CacheLock) LibraryCache[cacheKey] = (DateTime.UtcNow, list);
            return list;
        }

        internal static void Forget(ArrInstance i)
        {
            lock (CacheLock) LibraryCache.Remove(i.Kind + "|" + i.BaseUrl + "|" + i.Key);
        }

        /// <summary>The ids a server holds, to tell which titles it already has.</summary>
        internal sealed class Holdings
        {
            private readonly HashSet<string> _imdb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<int> _tmdb = new HashSet<int>();
            private readonly HashSet<int> _tvdb = new HashSet<int>();

            internal Holdings(IEnumerable<LibraryEntry> entries)
            {
                foreach (var e in entries)
                {
                    if (!string.IsNullOrEmpty(e.imdbId)) _imdb.Add(e.imdbId!);
                    if (e.tmdbId > 0) _tmdb.Add(e.tmdbId);
                    if (e.tvdbId > 0) _tvdb.Add(e.tvdbId);
                }
            }

            internal bool Has(MissingList.Title t) =>
                (t.Imdb.Length > 0 && _imdb.Contains(t.Imdb)) || (t.Tmdb > 0 && _tmdb.Contains(t.Tmdb)) || (t.Tvdb > 0 && _tvdb.Contains(t.Tvdb));
        }

        // ---- The add dialog ----

        internal async Task<(List<RootFolder> Folders, List<Profile> Profiles)> OptionsAsync(ArrInstance i, CancellationToken ct)
        {
            var folders = Get<List<RootFolder>>(i, "/api/v3/rootfolder", ct);
            var profiles = Get<List<Profile>>(i, "/api/v3/qualityprofile", ct);
            return (await folders.ConfigureAwait(false) ?? new List<RootFolder>(), await profiles.ConfigureAwait(false) ?? new List<Profile>());
        }

        // ---- Add ----

        internal async Task<AddResult> AddAsync(ArrInstance i, MissingList.Title t, AddOptions o, CancellationToken ct)
        {
            if (o.RootFolderPath.Length == 0 || o.QualityProfileId <= 0)
                return new AddResult { Message = $"Pick a root folder and a quality profile for {i.Label} first." };
            try
            {
                var holdings = new Holdings(await LibraryAsync(i, ct).ConfigureAwait(false));
                if (holdings.Has(t)) return new AddResult { AlreadyIn = true, Message = $"Already in {i.Label}." };
                var result = i.Kind == ArrKind.Sonarr
                    ? await AddShowAsync(i, t, o, ct).ConfigureAwait(false)
                    : await AddMovieAsync(i, t, o, ct).ConfigureAwait(false);
                if (result.Success || result.AlreadyIn) Forget(i);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new AddResult { Message = $"{i.Label}: {ex.Message}" };
            }
        }

        private async Task<LibraryEntry?> LookupShowAsync(ArrInstance i, MissingList.Title t, CancellationToken ct)
        {
            // TVDB is Sonarr's own id; the IMDb id is looked up when the file has no TVDB id.
            var terms = new List<string>();
            if (t.Tvdb > 0) terms.Add("tvdb:" + t.Tvdb);
            if (t.Imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) terms.Add("imdb:" + t.Imdb);
            foreach (var term in terms)
            {
                var found = await Get<List<LibraryEntry>>(i, "/api/v3/series/lookup?term=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
                var first = found?.FirstOrDefault(s => s.tvdbId > 0);
                if (first != null) return first;
            }
            return null;
        }

        private async Task<AddResult> AddShowAsync(ArrInstance i, MissingList.Title t, AddOptions o, CancellationToken ct)
        {
            var series = await LookupShowAsync(i, t, ct).ConfigureAwait(false);
            if (series == null)
                return new AddResult { Message = t.Tvdb == 0 && t.Imdb.Length == 0 ? "No TVDB or IMDb id to look the show up by." : $"{i.Label} could not find the show." };
            if (new Holdings(await LibraryAsync(i, ct).ConfigureAwait(false)).Has(new MissingList.Title { Tvdb = series.tvdbId }))
                return new AddResult { AlreadyIn = true, Message = $"Already in {i.Label}." };

            // Sonarr v3 still asks for a language profile; v4 has none.
            string language = "";
            if ((await VersionAsync(i, ct).ConfigureAwait(false)).StartsWith("3.", StringComparison.Ordinal))
            {
                var profiles = await Get<List<Profile>>(i, "/api/v3/languageprofile", ct).ConfigureAwait(false);
                var first = profiles?.FirstOrDefault();
                if (first != null) language = "\"languageProfileId\":" + first.id + ",";
            }
            bool seasonsMonitored = o.Monitored && o.SeriesMonitor != "none";
            var seasons = string.Join(",", (series.seasons ?? new List<SeasonEntry>())
                .Select(s => "{\"seasonNumber\":" + s.seasonNumber + ",\"monitored\":" + B(seasonsMonitored && s.seasonNumber > 0) + "}"));
            var body = "{"
                + "\"tvdbId\":" + series.tvdbId + ","
                + "\"title\":" + Q(series.title ?? t.Name) + ","
                + (string.IsNullOrEmpty(series.titleSlug) ? "" : "\"titleSlug\":" + Q(series.titleSlug) + ",")
                + (series.year > 0 ? "\"year\":" + series.year + "," : "")
                + "\"qualityProfileId\":" + o.QualityProfileId + ","
                + language
                + "\"rootFolderPath\":" + Q(o.RootFolderPath) + ","
                + "\"monitored\":" + B(o.Monitored) + ","
                + "\"seasonFolder\":" + B(o.SeasonFolder) + ","
                + "\"seasons\":[" + seasons + "],"
                + "\"addOptions\":{\"monitor\":" + Q(o.SeriesMonitor) + ",\"searchForMissingEpisodes\":" + B(o.Search) + ",\"searchForCutoffUnmetEpisodes\":false}"
                + "}";
            var (status, resBody) = await Send(HttpMethod.Post, i.BaseUrl, i.Key, "/api/v3/series", body, ct).ConfigureAwait(false);
            if (status >= 200 && status <= 299) return new AddResult { Success = true, Message = $"Added to {i.Label}." };
            return Failed(i, resBody, status, "SeriesExistsValidator");
        }

        private async Task<LibraryEntry?> LookupMovieAsync(ArrInstance i, MissingList.Title t, CancellationToken ct)
        {
            if (t.Tmdb > 0)
            {
                // A TMDb id Radarr does not know (404): the IMDb id may still find it.
                try
                {
                    var byTmdb = await Get<LibraryEntry>(i, "/api/v3/movie/lookup/tmdb?tmdbId=" + t.Tmdb.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    if (byTmdb != null && byTmdb.tmdbId > 0) return byTmdb;
                }
                catch (InvalidOperationException ex) when (t.Imdb.Length > 0) { Log.Debug($"Radarr: TMDb {t.Tmdb} not found ({ex.Message}), trying {t.Imdb}"); }
            }
            if (t.Imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                var found = await Get<List<LibraryEntry>>(i, "/api/v3/movie/lookup?term=" + Uri.EscapeDataString("imdb:" + t.Imdb), ct).ConfigureAwait(false);
                var first = found?.FirstOrDefault(m => m.tmdbId > 0);
                if (first != null) return first;
            }
            return null;
        }

        private async Task<AddResult> AddMovieAsync(ArrInstance i, MissingList.Title t, AddOptions o, CancellationToken ct)
        {
            var movie = await LookupMovieAsync(i, t, ct).ConfigureAwait(false);
            if (movie == null)
                return new AddResult { Message = t.Tmdb == 0 && t.Imdb.Length == 0 ? "No TMDb or IMDb id to look the movie up by." : $"{i.Label} could not find the movie." };
            if (new Holdings(await LibraryAsync(i, ct).ConfigureAwait(false)).Has(new MissingList.Title { Tmdb = movie.tmdbId }))
                return new AddResult { AlreadyIn = true, Message = $"Already in {i.Label}." };

            var body = "{"
                + "\"tmdbId\":" + movie.tmdbId + ","
                + "\"title\":" + Q(movie.title ?? t.Name) + ","
                + (string.IsNullOrEmpty(movie.titleSlug) ? "" : "\"titleSlug\":" + Q(movie.titleSlug) + ",")
                + (movie.year > 0 ? "\"year\":" + movie.year + "," : "")
                + "\"qualityProfileId\":" + o.QualityProfileId + ","
                + "\"rootFolderPath\":" + Q(o.RootFolderPath) + ","
                + "\"monitored\":" + B(o.Monitored) + ","
                + "\"minimumAvailability\":" + Q(o.MinimumAvailability) + ","
                + "\"addOptions\":{\"searchForMovie\":" + B(o.Search) + "}"
                + "}";
            var (status, resBody) = await Send(HttpMethod.Post, i.BaseUrl, i.Key, "/api/v3/movie", body, ct).ConfigureAwait(false);
            if (status >= 200 && status <= 299) return new AddResult { Success = true, Message = $"Added to {i.Label}." };
            return Failed(i, resBody, status, "MovieExistsValidator");
        }

        // Sonarr / Radarr answer a failed add with a list of validation errors.
        private AddResult Failed(ArrInstance i, string body, int status, string existsCode)
        {
            try
            {
                var first = _json.DeserializeFromString<List<ValidationError>>(body)?.FirstOrDefault();
                if (first != null)
                {
                    if (first.errorCode == existsCode) return new AddResult { AlreadyIn = true, Message = $"Already in {i.Label}." };
                    if (!string.IsNullOrEmpty(first.errorMessage)) return new AddResult { Message = $"{i.Label}: {first.errorMessage}" };
                    if (!string.IsNullOrEmpty(first.propertyName)) return new AddResult { Message = $"{i.Label}: {first.propertyName} – {first.attemptedValue ?? "invalid value"}" };
                }
            }
            catch { }
            var shortBody = body.Length > 300 ? body.Substring(0, 300) + "…" : body;
            return new AddResult { Message = $"{i.Label} answered HTTP {status}: {shortBody}" };
        }
    }
}
