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
    /// Posters of the missing titles, from TMDb: the plugin finds each title's poster path (with
    /// Arr Companion's TMDb key, else Home Screen Companion's) and keeps it in posters.json; the
    /// browser loads the image itself from TMDb's image server, which needs no key.
    /// </summary>
    internal static class Posters
    {
        private const string ImageBase = "https://image.tmdb.org/t/p/";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan KeepFound = TimeSpan.FromDays(30);
        private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(7);
        private static readonly object Gate = new object();
        private static Dictionary<string, Entry>? _cache;

        public sealed class FileShape
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        public sealed class Entry
        {
            public string Key { get; set; } = "";
            public string Path { get; set; } = "";     // TMDb's poster_path; "" = TMDb has none
            public DateTime CheckedUtc { get; set; }
        }

        public sealed class TmdbDetails { public string? poster_path { get; set; } }
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

        internal static string Url(string path, string size = "w185") => path.Length == 0 ? "" : ImageBase + size + path;

        private static Dictionary<string, Entry> Cache(IJsonSerializer json)
        {
            if (_cache != null) return _cache;
            var entries = new List<Entry>();
            try { if (File.Exists(FilePath)) entries = json.DeserializeFromFile<FileShape>(FilePath)?.Entries ?? entries; }
            catch (Exception ex) { Log.Debug("The poster cache could not be read: " + ex.Message); }
            return _cache = entries.Where(e => !string.IsNullOrEmpty(e.Key))
                .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static bool Fresh(Entry e) => DateTime.UtcNow - e.CheckedUtc < (e.Path.Length > 0 ? KeepFound : RetryNotFound);

        /// <summary>The poster paths of the titles: from the cache, the rest asked of TMDb (4 at a time).</summary>
        internal static async Task<Dictionary<string, string>> PathsAsync(IEnumerable<MissingList.Title> titles, string apiKey, IJsonSerializer json, CancellationToken ct)
        {
            var list = titles.ToList();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<MissingList.Title> toAsk;
            lock (Gate)
            {
                var cache = Cache(json);
                foreach (var t in list)
                    if (cache.TryGetValue(t.Key, out var e) && (Fresh(e) || apiKey.Length == 0)) result[t.Key] = e.Path;
                toAsk = list.Where(t => !result.ContainsKey(t.Key)).ToList();
            }
            if (toAsk.Count == 0 || apiKey.Length == 0) return result;

            var gate = new SemaphoreSlim(4);
            var found = await Task.WhenAll(toAsk.Select(async t =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { return (t.Key, Path: await AskAsync(t, apiKey, json, ct).ConfigureAwait(false)); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

            lock (Gate)
            {
                var cache = Cache(json);
                foreach (var (key, path) in found)
                {
                    if (path == null) continue;   // failed: asked again next time
                    cache[key] = new Entry { Key = key, Path = path, CheckedUtc = DateTime.UtcNow };
                    result[key] = path;
                }
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                    json.SerializeToFile(new FileShape { Entries = cache.Values.ToList() }, FilePath);
                }
                catch (Exception ex) { Log.Debug("The poster cache could not be saved: " + ex.Message); }
            }
            return result;
        }

        // The poster path; "" when TMDb has none, null when it could not be asked.
        private static async Task<string?> AskAsync(MissingList.Title t, string apiKey, IJsonSerializer json, CancellationToken ct)
        {
            try
            {
                bool show = t.Type == MediaType.Show;
                if (t.Tmdb > 0)
                {
                    var details = await Get<TmdbDetails>((show ? "tv/" : "movie/") + t.Tmdb, apiKey, json, ct).ConfigureAwait(false);
                    if (details != null) return details.poster_path ?? "";
                }
                foreach (var (id, source) in new[] { (t.Imdb, "imdb_id"), (t.Tvdb > 0 ? t.Tvdb.ToString() : "", "tvdb_id") })
                {
                    if (id.Length == 0) continue;
                    var find = await Get<TmdbFind>($"find/{Uri.EscapeDataString(id)}?external_source={source}", apiKey, json, ct).ConfigureAwait(false);
                    var hit = (show ? find?.tv_results : find?.movie_results)?.FirstOrDefault()
                        ?? find?.movie_results?.FirstOrDefault() ?? find?.tv_results?.FirstOrDefault();
                    if (hit != null) return hit.poster_path ?? "";
                }
                return "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"TMDb poster for {t.Name}: {ex.Message}");
                return null;
            }
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
