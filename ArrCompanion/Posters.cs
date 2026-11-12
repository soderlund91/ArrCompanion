using MediaBrowser.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// Posters and details of the missing titles, from TMDb: the plugin finds each title's poster
    /// path, original language, countries, genres and main cast and director (with Arr Companion's
    /// TMDb key, else Home Screen Companion's) and keeps them in posters.json; the browser loads
    /// the image itself from TMDb's image server, which needs no key.
    /// Without a TMDb key, Emby's own metadata providers (the ones Identify uses, e.g. TheMovieDb)
    /// are asked by the title's ids instead. They give no original language: it is then read from
    /// the script of the original title (Chinese, Korean, Japanese, …), else not known.
    /// </summary>
    internal static class Posters
    {
        private const string ImageBase = "https://image.tmdb.org/t/p/";
        // Entries from before the details were kept (or from an older form of them) are asked again.
        private const int DetailsVersion = 1;
        private const int MaxActors = 5, MaxDirectors = 2;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan KeepFound = TimeSpan.FromDays(30);
        private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(7);
        private static readonly object Gate = new object();
        private static Dictionary<string, Entry>? _cache;
        private static IProviderManager? _providers;
        private static IApplicationHost? _appHost;
        private static IServerConfigurationManager? _serverConfig;
        private static List<IRemoteMetadataProvider<Movie, MovieInfo>>? _movieProviders;
        private static List<IRemoteMetadataProvider<Series, SeriesInfo>>? _seriesProviders;
        private static int _enriching;

        internal static void Initialize(IProviderManager providers) => _providers ??= providers;

        internal static void Initialize(IApplicationHost appHost, IServerConfigurationManager serverConfig)
        {
            _appHost ??= appHost;
            _serverConfig ??= serverConfig;
        }

        /// <summary>Posters can be looked up: a TMDb key, or Emby's metadata search.</summary>
        internal static bool CanLookUp(string apiKey) => apiKey.Length > 0 || _providers != null;

        /// <summary>The details of every missing title are being looked up in the background.</summary>
        internal static bool Enriching => Volatile.Read(ref _enriching) == 1;

        public sealed class FileShape
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        public sealed class Entry
        {
            public string Key { get; set; } = "";
            public string Path { get; set; } = "";     // TMDb's poster_path, or the image URL from Emby's search; "" = none
            public DateTime CheckedUtc { get; set; }
            public int Details { get; set; }           // DetailsVersion once the fields below were asked for
            public string Language { get; set; } = ""; // original language, ISO 639-1; "" = not known
            public List<string> Countries { get; set; } = new List<string>();   // origin / production countries, ISO 3166-1
            public List<string> Genres { get; set; } = new List<string>();
            public List<string> People { get; set; } = new List<string>();      // main cast, then director(s) / creator(s)
        }

        // ---- TMDb's answers ----
        public sealed class TmdbNamed { public string? name { get; set; } public string? job { get; set; } public string? iso_3166_1 { get; set; } public int? order { get; set; } }
        public sealed class TmdbCredits
        {
            public List<TmdbNamed>? cast { get; set; }
            public List<TmdbNamed>? crew { get; set; }
        }
        public sealed class TmdbDetails
        {
            public int id { get; set; }
            public string? poster_path { get; set; }
            public string? original_language { get; set; }
            public List<string>? origin_country { get; set; }
            public List<TmdbNamed>? production_countries { get; set; }
            public List<TmdbNamed>? genres { get; set; }
            public List<TmdbNamed>? created_by { get; set; }
            public TmdbCredits? credits { get; set; }
        }
        public sealed class TmdbFind
        {
            public List<TmdbDetails>? movie_results { get; set; }
            public List<TmdbDetails>? tv_results { get; set; }
        }

        private static string FilePath => System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "posters.json");

        internal static string ApiKey(PluginConfiguration config)
        {
            var own = (config.TmdbApiKey ?? "").Trim();
            return own.Length > 0 ? own : MissingList.Hsc().TmdbApiKey;
        }

        internal static string Url(string path, string size = "w185") =>
            path.Length == 0 ? "" : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ImageBase + size + path;

        private static Dictionary<string, Entry> Cache(IJsonSerializer json)
        {
            if (_cache != null) return _cache;
            var entries = new List<Entry>();
            try { if (File.Exists(FilePath)) entries = json.DeserializeFromFile<FileShape>(FilePath)?.Entries ?? entries; }
            catch (Exception ex) { Log.Debug("The poster cache could not be read: " + ex.Message); }
            foreach (var e in entries)
            {
                e.Countries ??= new List<string>();
                e.Genres ??= new List<string>();
                e.People ??= new List<string>();
                e.Language ??= "";
                e.Path ??= "";
            }
            return _cache = entries.Where(e => !string.IsNullOrEmpty(e.Key))
                .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static bool Fresh(Entry e) =>
            e.Details >= DetailsVersion && DateTime.UtcNow - e.CheckedUtc < (e.Path.Length > 0 || e.Genres.Count > 0 ? KeepFound : RetryNotFound);

        /// <summary>What is kept for these keys, without asking anyone (the Missing tab and the match score read it).</summary>
        internal static Dictionary<string, Entry> Known(IEnumerable<string> keys, IJsonSerializer json)
        {
            var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                var cache = Cache(json);
                foreach (var k in keys)
                    if (cache.TryGetValue(k, out var e) && e.Details >= DetailsVersion) result[k] = e;
            }
            return result;
        }

        /// <summary>The titles whose details are not kept yet (or are old).</summary>
        internal static List<MissingList.Title> Unknown(IEnumerable<MissingList.Title> titles, string apiKey, IJsonSerializer json)
        {
            if (!CanLookUp(apiKey)) return new List<MissingList.Title>();
            lock (Gate)
            {
                var cache = Cache(json);
                return titles.Where(t => !cache.TryGetValue(t.Key, out var e) || !Fresh(e)).ToList();
            }
        }

        /// <summary>
        /// Looks up the details of these titles in the background, one run at a time; true when a
        /// run was started (or one is running). The page asks again while Enriching is on.
        /// </summary>
        internal static bool EnrichInBackground(List<MissingList.Title> titles, string apiKey, IJsonSerializer json, Action? done = null)
        {
            if (titles.Count == 0 || !CanLookUp(apiKey)) return Enriching;
            if (Interlocked.CompareExchange(ref _enriching, 1, 0) != 0) return true;
            Task.Run(async () =>
            {
                var started = DateTime.UtcNow;
                try
                {
                    await PathsAsync(titles, apiKey, json, CancellationToken.None).ConfigureAwait(false);
                    Log.Debug($"Details of {titles.Count} titles looked up in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
                }
                catch (Exception ex) { Log.Debug("The details could not be looked up: " + ex.Message); }
                finally
                {
                    Volatile.Write(ref _enriching, 0);
                    try { done?.Invoke(); } catch (Exception ex) { Log.Debug(ex.Message); }
                }
            });
            return true;
        }

        /// <summary>The poster paths of the titles: from the cache, the rest asked of TMDb (4 at a time, kept every 20).</summary>
        internal static async Task<Dictionary<string, string>> PathsAsync(IEnumerable<MissingList.Title> titles, string apiKey, IJsonSerializer json, CancellationToken ct)
        {
            var list = titles.ToList();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<MissingList.Title> toAsk;
            lock (Gate)
            {
                var cache = Cache(json);
                foreach (var t in list)
                    if (cache.TryGetValue(t.Key, out var e) && (Fresh(e) || !CanLookUp(apiKey))) result[t.Key] = e.Path;
                toAsk = list.Where(t => !result.ContainsKey(t.Key)).GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            }
            if (toAsk.Count == 0 || !CanLookUp(apiKey)) return result;

            var gate = new SemaphoreSlim(4);
            for (int i = 0; i < toAsk.Count; i += 20)
            {
                var chunk = toAsk.Skip(i).Take(20).ToList();
                var found = await Task.WhenAll(chunk.Select(async t =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { return apiKey.Length > 0 ? await AskAsync(t, apiKey, json, ct).ConfigureAwait(false) : await AskEmbyAsync(t, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);

                lock (Gate)
                {
                    var cache = Cache(json);
                    foreach (var e in found)
                    {
                        if (e == null) continue;   // failed: asked again next time
                        cache[e.Key] = e;
                        result[e.Key] = e.Path;
                    }
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                        json.SerializeToFile(new FileShape { Entries = cache.Values.ToList() }, FilePath);
                    }
                    catch (Exception ex) { Log.Debug("The poster cache could not be saved: " + ex.Message); }
                }
            }
            return result;
        }

        private static string MetadataLanguage()
        {
            var lang = (_serverConfig?.Configuration?.PreferredMetadataLanguage ?? "").Trim();
            return lang.Length > 0 ? lang : "en";
        }

        // The poster and details; Path "" when TMDb has no poster, null when it could not be asked.
        private static async Task<Entry?> AskAsync(MissingList.Title t, string apiKey, IJsonSerializer json, CancellationToken ct)
        {
            try
            {
                bool show = t.Type == MediaType.Show;
                int id = t.Tmdb;
                if (id <= 0)
                {
                    foreach (var (ext, source) in new[] { (t.Imdb, "imdb_id"), (t.Tvdb > 0 ? t.Tvdb.ToString() : "", "tvdb_id") })
                    {
                        if (ext.Length == 0) continue;
                        var find = await Get<TmdbFind>($"find/{Uri.EscapeDataString(ext)}?external_source={source}", apiKey, json, ct).ConfigureAwait(false);
                        var hit = (show ? find?.tv_results : find?.movie_results)?.FirstOrDefault();
                        if (hit == null)
                        {
                            // Listed as the other kind: TMDb has it as that.
                            hit = find?.movie_results?.FirstOrDefault() ?? find?.tv_results?.FirstOrDefault();
                            if (hit != null) show = find?.tv_results?.Contains(hit) == true;
                        }
                        if (hit != null && hit.id > 0) { id = hit.id; break; }
                    }
                }
                var entry = new Entry { Key = t.Key, CheckedUtc = DateTime.UtcNow, Details = DetailsVersion };
                if (id <= 0) return entry;
                var lang = Uri.EscapeDataString(MetadataLanguage());
                var d = await Get<TmdbDetails>((show ? "tv/" : "movie/") + id + "?append_to_response=credits&language=" + lang, apiKey, json, ct).ConfigureAwait(false);
                if (d == null) return entry;
                entry.Path = d.poster_path ?? "";
                entry.Language = Languages.Code(d.original_language);
                entry.Countries = (d.origin_country ?? new List<string>())
                    .Concat((d.production_countries ?? new List<TmdbNamed>()).Select(c => c.iso_3166_1 ?? c.name ?? ""))
                    .Select(Languages.Country).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
                entry.Genres = Languages.Genres((d.genres ?? new List<TmdbNamed>()).Select(g => g.name ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var cast = (d.credits?.cast ?? new List<TmdbNamed>()).OrderBy(c => c.order ?? int.MaxValue).Select(c => c.name ?? "").Where(n => n.Length > 0).Take(MaxActors);
                var directors = (d.credits?.crew ?? new List<TmdbNamed>()).Where(c => c.job == "Director").Select(c => c.name ?? "")
                    .Concat((d.created_by ?? new List<TmdbNamed>()).Select(c => c.name ?? "")).Where(n => n.Length > 0).Take(MaxDirectors);
                entry.People = cast.Concat(directors).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return entry;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"TMDb details for {t.Name}: {ex.Message}");
                return null;
            }
        }

        // Emby's metadata providers by the title's ids: the image URL from its search, the details
        // from the provider itself. null when it could not be asked.
        private static async Task<Entry?> AskEmbyAsync(MissingList.Title t, CancellationToken ct)
        {
            var ids = new ProviderIdDictionary();
            if (t.Imdb.Length > 0) ids[MetadataProviders.Imdb.ToString()] = t.Imdb;
            if (t.Tmdb > 0) ids[MetadataProviders.Tmdb.ToString()] = t.Tmdb.ToString();
            if (t.Type == MediaType.Show && t.Tvdb > 0) ids[MetadataProviders.Tvdb.ToString()] = t.Tvdb.ToString();
            var entry = new Entry { Key = t.Key, CheckedUtc = DateTime.UtcNow, Details = DetailsVersion };
            if (ids.Count == 0) return entry;
            try
            {
                IEnumerable<RemoteSearchResult> results = t.Type == MediaType.Show
                    ? await _providers!.GetRemoteSearchResults<Series, SeriesInfo>(
                        new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { ProviderIds = ids }, IncludeDisabledProviders = true }, ct).ConfigureAwait(false)
                    : await _providers!.GetRemoteSearchResults<Movie, MovieInfo>(
                        new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { ProviderIds = ids }, IncludeDisabledProviders = true }, ct).ConfigureAwait(false);
                // A metadata plugin may answer with titles that are not the one asked for: none whose ids differ.
                bool Agrees(IHasProviderIds r) => r.ProviderIds == null || ids.All(a =>
                    !r.ProviderIds.TryGetValue(a.Key, out var theirs) || string.IsNullOrWhiteSpace(theirs) || string.Equals(theirs.Trim(), a.Value, StringComparison.OrdinalIgnoreCase));
                var url = (results ?? Enumerable.Empty<RemoteSearchResult>()).Where(r => Agrees(r)).Select(r => r.ImageUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "";
                // TheMovieDb gives the original size: the page needs far less.
                const string original = ImageBase + "original/";
                entry.Path = url.StartsWith(original, StringComparison.OrdinalIgnoreCase) ? ImageBase + "w185/" + url.Substring(original.Length) : url;

                var (item, people) = await EmbyDetailsAsync(t, ids, ct).ConfigureAwait(false);
                if (item != null && Agrees(item))
                {
                    entry.Countries = (item.ProductionLocations ?? Array.Empty<string>()).Select(Languages.Country)
                        .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
                    entry.Genres = Languages.Genres(item.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    entry.Language = Languages.FromScript(item.OriginalTitle, entry.Countries);
                    var list = people ?? new List<PersonInfo>();
                    entry.People = list.Where(p => p.Type == PersonType.Actor).Select(p => p.Name ?? "").Where(n => n.Length > 0).Take(MaxActors)
                        .Concat(list.Where(p => p.Type == PersonType.Director).Select(p => p.Name ?? "").Where(n => n.Length > 0).Take(MaxDirectors))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                return entry;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"Emby's metadata search, details for {t.Name}: {ex.Message}");
                return null;
            }
        }

        // The title as Emby's metadata providers have it (TheMovieDb first): the first that knows it.
        private static async Task<(BaseItem? Item, List<PersonInfo>? People)> EmbyDetailsAsync(MissingList.Title t, ProviderIdDictionary ids, CancellationToken ct)
        {
            if (_appHost == null) return (null, null);
            var lang = MetadataLanguage();
            if (t.Type == MediaType.Show)
            {
                _seriesProviders ??= Ordered(_appHost.GetExports<IRemoteMetadataProvider<Series, SeriesInfo>>(false));
                foreach (var p in _seriesProviders)
                {
                    try
                    {
                        var r = await p.GetMetadata(new SeriesInfo { ProviderIds = ids, Name = t.Name, Year = t.Year, MetadataLanguage = lang }, ct).ConfigureAwait(false);
                        if (r != null && r.HasMetadata && r.Item != null) return (r.Item, r.People);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log.Debug($"{p.Name}, details for {t.Name}: {ex.Message}"); }
                }
            }
            else
            {
                _movieProviders ??= Ordered(_appHost.GetExports<IRemoteMetadataProvider<Movie, MovieInfo>>(false));
                foreach (var p in _movieProviders)
                {
                    try
                    {
                        var r = await p.GetMetadata(new MovieInfo { ProviderIds = ids, Name = t.Name, Year = t.Year, MetadataLanguage = lang }, ct).ConfigureAwait(false);
                        if (r != null && r.HasMetadata && r.Item != null) return (r.Item, r.People);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log.Debug($"{p.Name}, details for {t.Name}: {ex.Message}"); }
                }
            }
            return (null, null);

            static List<T> Ordered<T>(IEnumerable<T> providers) where T : IMetadataProvider =>
                (providers ?? Enumerable.Empty<T>()).OrderBy(p => p.Name == "TheMovieDb" ? 0 : 1).ToList();
        }

        private static async Task<T?> Get<T>(string path, string key, IJsonSerializer json, CancellationToken ct) where T : class
        {
            // A v4 read access token is sent as a bearer token, a v3 key as api_key (as Home Screen Companion does).
            bool isToken = key.Length > 100 || key.Contains(".");
            var url = "https://api.themoviedb.org/3/" + path;
            if (!isToken) url += (url.Contains("?") ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (isToken) req.Headers.Add("Authorization", "Bearer " + key);
                using (var res = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if ((int)res.StatusCode == 404) return null;
                    if (!res.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)res.StatusCode);
                    return json.DeserializeFromString<T>(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
    }
}
