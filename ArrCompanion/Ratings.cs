using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// The missing titles' ratings from MDBList (IMDb, Rotten Tomatoes and its audience score,
    /// Metacritic, Letterboxd and MDBList's own score), with the MDBList key Home Screen Companion
    /// has. Asked by IMDb id, up to 200 titles in one request (MDBList's batch lookup), kept in
    /// ratings.json and asked again after 7 days. A free MDBList account has 1,000 requests a day,
    /// shared with Home Screen Companion's lists: a run stops well before that, and waits as long
    /// as MDBList says after a 429.
    /// </summary>
    internal static class Ratings
    {
        private const string Api = "https://api.mdblist.com/";
        private const int BatchSize = 200;
        private const int KeepRemaining = 50;   // requests left today that a run leaves for Home Screen Companion
        private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly object Gate = new object();
        private static Dictionary<string, Entry>? _cache;
        private static int _running;
        private static DateTime _pausedUntilUtc = DateTime.MinValue;
        private static long _requests;

        public sealed class FileShape
        {
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        public sealed class Entry
        {
            public string Imdb { get; set; } = "";
            public DateTime CheckedUtc { get; set; }
            public bool Found { get; set; }
            public double? ImdbRating { get; set; }     // 0–10
            public int? Tomatoes { get; set; }          // Rotten Tomatoes critics, %
            public int? Popcorn { get; set; }           // Rotten Tomatoes audience, %
            public int? Metacritic { get; set; }        // 0–100
            public double? Letterboxd { get; set; }     // 0–5
            public int? Score { get; set; }             // MDBList's score, 0–100
        }

        // ---- MDBList's answer ----
        public sealed class MdbIds { public string? imdb { get; set; } }
        public sealed class MdbRating { public string? source { get; set; } public double? value { get; set; } public double? score { get; set; } }
        public sealed class MdbItem
        {
            public MdbIds? ids { get; set; }
            public double? score { get; set; }
            public List<MdbRating>? ratings { get; set; }
        }

        private static string FilePath => Path.Combine(Plugin.Instance.DataFolderPath, "ratings.json");

        internal static string ApiKey() => MissingList.Hsc().MdblistApiKey;

        /// <summary>Ratings are being looked up in the background.</summary>
        internal static bool Running => Volatile.Read(ref _running) == 1;

        /// <summary>MDBList requests made since Emby started.</summary>
        internal static long Requests => Interlocked.Read(ref _requests);

        private static string Id(string? imdb) => (imdb ?? "").Trim().ToLowerInvariant();

        private static Dictionary<string, Entry> Cache(IJsonSerializer json)
        {
            if (_cache != null) return _cache;
            var entries = new List<Entry>();
            try { if (File.Exists(FilePath)) entries = json.DeserializeFromFile<FileShape>(FilePath)?.Entries ?? entries; }
            catch (Exception ex) { Log.Debug("The ratings cache could not be read: " + ex.Message); }
            return _cache = entries.Where(e => !string.IsNullOrEmpty(e.Imdb))
                .GroupBy(e => Id(e.Imdb)).ToDictionary(g => g.Key, g => g.First());
        }

        private static bool Fresh(Entry e) => DateTime.UtcNow - e.CheckedUtc < Keep;

        /// <summary>What is kept for these IMDb ids, without asking anyone.</summary>
        internal static Dictionary<string, Entry> Known(IEnumerable<string> imdbIds, IJsonSerializer json)
        {
            var result = new Dictionary<string, Entry>();
            lock (Gate)
            {
                var cache = Cache(json);
                foreach (var id in imdbIds.Select(Id))
                    if (id.Length > 0 && cache.TryGetValue(id, out var e) && e.Found) result[id] = e;
            }
            return result;
        }

        /// <summary>The IMDb ids whose ratings are not kept yet (or are old); none without a key.</summary>
        private static List<string> Unknown(IEnumerable<string> imdbIds, IJsonSerializer json)
        {
            if (ApiKey().Length == 0 || DateTime.UtcNow < _pausedUntilUtc) return new List<string>();
            lock (Gate)
            {
                var cache = Cache(json);
                return imdbIds.Select(Id).Where(id => id.StartsWith("tt")).Distinct()
                    .Where(id => !cache.TryGetValue(id, out var e) || !Fresh(e)).ToList();
            }
        }

        /// <summary>Looks up the ratings not kept yet in the background, one run at a time.</summary>
        internal static void RefreshInBackground(IEnumerable<string> imdbIds, IJsonSerializer json)
        {
            var ids = Unknown(imdbIds, json);
            if (ids.Count == 0) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try { await AskAsync(ids, json, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log.Debug("The ratings could not be looked up: " + ex.Message); }
                finally { Volatile.Write(ref _running, 0); }
            });
        }

        /// <summary>The ratings not kept yet, asked now (the rules, before they run); waits for a background run first.</summary>
        internal static async Task EnsureAsync(IEnumerable<string> imdbIds, IJsonSerializer json, CancellationToken ct)
        {
            while (Running && !ct.IsCancellationRequested) await Task.Delay(500, ct).ConfigureAwait(false);
            var ids = Unknown(imdbIds, json);
            if (ids.Count == 0) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try { await AskAsync(ids, json, ct).ConfigureAwait(false); }
            finally { Volatile.Write(ref _running, 0); }
        }

        private static async Task AskAsync(List<string> ids, IJsonSerializer json, CancellationToken ct)
        {
            var key = ApiKey();
            if (key.Length == 0) return;
            var started = DateTime.UtcNow;
            long before = Requests;
            int size = BatchSize, found = 0;
            for (int i = 0; i < ids.Count;)
            {
                var chunk = ids.Skip(i).Take(size).ToList();
                var (items, status, remaining) = await PostAsync(chunk, key, json, ct).ConfigureAwait(false);
                if (status == 400 && size > 25) { size /= 2; continue; }   // more ids than MDBList takes at once
                if (items == null) break;                                   // a 429, or MDBList not reachable: asked again later
                lock (Gate)
                {
                    var cache = Cache(json);
                    var now = DateTime.UtcNow;
                    // An id MDBList does not know is kept as not found, so it is not asked again for a while.
                    foreach (var id in chunk) cache[id] = new Entry { Imdb = id, CheckedUtc = now };
                    foreach (var item in items)
                    {
                        var e = ToEntry(item, now);
                        if (e == null) continue;
                        cache[e.Imdb] = e;
                        found++;
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                        json.SerializeToFile(new FileShape { Entries = cache.Values.ToList() }, FilePath);
                    }
                    catch (Exception ex) { Log.Debug("The ratings cache could not be saved: " + ex.Message); }
                }
                i += chunk.Count;
                if (remaining != null && remaining <= KeepRemaining)
                {
                    Log.Info($"MDBList: {remaining} requests left today; the other ratings are looked up tomorrow.");
                    _pausedUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                    break;
                }
            }
            Log.Debug($"MDBList ratings: {found} of {ids.Count} titles found in {Requests - before} requests, {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        }

        // One batch: the items, the HTTP status, and the requests left today. Items null when it failed.
        private static async Task<(List<MdbItem>? Items, int Status, int? Remaining)> PostAsync(List<string> ids, string key, IJsonSerializer json, CancellationToken ct)
        {
            var body = "{\"ids\":[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]}";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, Api + "imdb/any?apikey=" + Uri.EscapeDataString(key)))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    req.Headers.TryAddWithoutValidation("User-Agent", "ArrCompanion");
                    Interlocked.Increment(ref _requests);
                    using (var res = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        int status = (int)res.StatusCode;
                        int? remaining = Header(res, "X-RateLimit-Remaining");
                        if (status == 429)
                        {
                            // Wait as long as MDBList says; the daily limit until it resets.
                            var wait = res.Headers.RetryAfter?.Delta ?? (Header(res, "Retry-After") is int s ? TimeSpan.FromSeconds(s) : TimeSpan.FromHours(1));
                            var reset = Header(res, "X-RateLimit-Reset");
                            var until = DateTime.UtcNow + wait;
                            if (reset != null) { var r = DateTimeOffset.FromUnixTimeSeconds(reset.Value).UtcDateTime; if (r > until) until = r; }
                            _pausedUntilUtc = until;
                            Log.Info($"MDBList: rate limit reached; ratings are looked up again after {until.ToLocalTime():g}.");
                            return (null, status, 0);
                        }
                        if (!res.IsSuccessStatusCode)
                        {
                            Log.Debug($"MDBList ratings: HTTP {status}");
                            return (null, status, remaining);
                        }
                        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return (json.DeserializeFromString<List<MdbItem>>(text) ?? new List<MdbItem>(), status, remaining);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Never the URL: it carries the key.
                Log.Debug("MDBList ratings: " + ex.Message);
                return (null, 0, null);
            }
        }

        private static int? Header(HttpResponseMessage res, string name) =>
            res.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var n) ? (int)Math.Min(n, int.MaxValue) : (int?)null;

        private static Entry? ToEntry(MdbItem item, DateTime now)
        {
            var id = Id(item.ids?.imdb);
            if (id.Length == 0) return null;
            var e = new Entry { Imdb = id, CheckedUtc = now, Found = true };
            if (item.score > 0) e.Score = (int)Math.Round(item.score.Value);
            foreach (var r in item.ratings ?? new List<MdbRating>())
            {
                if (r.value == null || r.value <= 0) continue;
                switch ((r.source ?? "").ToLowerInvariant())
                {
                    case "imdb": e.ImdbRating = Math.Round(r.value.Value, 1); break;
                    case "tomatoes": e.Tomatoes = (int)Math.Round(r.value.Value); break;
                    case "popcorn": e.Popcorn = (int)Math.Round(r.value.Value); break;
                    case "metacritic": e.Metacritic = (int)Math.Round(r.value.Value); break;
                    // Out of 5, as Letterboxd shows it (its score is out of 100).
                    case "letterboxd": e.Letterboxd = Math.Round(r.score > 0 ? r.score.Value / 20 : r.value.Value > 5 ? r.value.Value / 2 : r.value.Value, 1); break;
                }
            }
            return e;
        }
    }
}
