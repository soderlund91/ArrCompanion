using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// The server's taste, and how well a missing title matches it ("Match 82%").
    ///
    /// The profile is what everyone on the server watched in the last 180 days, read from Emby's
    /// user data (Played and LastPlayedDate of every user), as Home Screen Companion's "Your Next
    /// Watch" reads it: one GetAllUserData per user, matched to the library's movies and shows by
    /// user-data key (a show through its episodes; a film's versions are one film). Each user's
    /// watched title weighs 0.5 + 0.5·e^(−days ago / 60), a show more the more episodes were
    /// watched (up to 2×), a favourite 1.5×; one rated below 5 does not count. The profile adds up
    /// those weights per genre, original language, country and person (top-billed actors and
    /// directors), as shares of all the watching.
    ///
    /// A missing title's match, 0–100, is on a fixed scale (not relative to the other titles, so a
    /// rule's "at least N%" keeps its meaning):
    ///   genres    40% — each of its genres by how much of the watching carries it, and (as Next
    ///                   Watch does) by how much more than the library carries it (lift, up to 2×);
    ///   language  25% — its original language's share of all the watching (40% or more = full);
    ///   country   10% — its best country's share of all the watching (40% or more = full);
    ///   people    25% — people in it who are in the watched titles (two strong ones = full).
    /// A part the title has no data for is left out and the others count for it. No genres: no
    /// match. The profile is kept in taste.json and built again in the background.
    /// </summary>
    internal static class Taste
    {
        internal const int WindowDays = 180;
        private const int MaxActors = 5, MaxCrew = 2, MaxDetailsAsked = 300;
        private const double GenreW = 0.40, LanguageW = 0.25, CountryW = 0.10, PeopleW = 0.25, MaxGenreLift = 2.0;
        // A language or country with this share of all the watching counts fully. Against all of it, not
        // the most watched one: titles whose language is not known (no TMDb key) do not inflate the rest.
        private const double FullShare = 0.4;
        internal static readonly TimeSpan Stale = TimeSpan.FromHours(6);

        private static ILibraryManager? _library;
        private static IUserManager? _users;
        private static IUserDataManager? _userData;
        private static readonly object Gate = new object();
        private static Profile? _profile;
        private static bool _loaded;
        private static int _building;

        internal static void Initialize(ILibraryManager library, IUserManager users, IUserDataManager userData)
        {
            _library ??= library;
            _users ??= users;
            _userData ??= userData;
        }

        internal static bool Building => Volatile.Read(ref _building) == 1;

        public sealed class Profile
        {
            public DateTime BuiltUtc { get; set; }
            public int Users { get; set; }          // users with something watched in the window
            public int Titles { get; set; }         // movies and shows watched in the window
            public long BuildMs { get; set; }
            public Dictionary<string, double> Genres { get; set; } = new Dictionary<string, double>();      // share of the watching
            public Dictionary<string, double> GenreLift { get; set; } = new Dictionary<string, double>();   // share / the library's share
            public Dictionary<string, double> Languages { get; set; } = new Dictionary<string, double>();
            public Dictionary<string, double> Countries { get; set; } = new Dictionary<string, double>();
            public Dictionary<string, double> People { get; set; } = new Dictionary<string, double>();      // weight / a watched title's mean weight
        }

        private static string FilePath => Path.Combine(Plugin.Instance.DataFolderPath, "taste.json");

        /// <summary>The profile kept, or null before the first one is built (or when nothing was watched).</summary>
        internal static Profile? Current(IJsonSerializer json)
        {
            lock (Gate)
            {
                if (_loaded) return _profile;
                _loaded = true;
                try { if (File.Exists(FilePath)) _profile = json.DeserializeFromFile<Profile>(FilePath); }
                catch (Exception ex) { Log.Debug("The taste profile could not be read: " + ex.Message); }
                if (_profile != null) Fix(_profile);
                return _profile;
            }
        }

        private static void Fix(Profile p)
        {
            var c = StringComparer.OrdinalIgnoreCase;
            p.Genres = new Dictionary<string, double>(p.Genres ?? new Dictionary<string, double>(), c);
            p.GenreLift = new Dictionary<string, double>(p.GenreLift ?? new Dictionary<string, double>(), c);
            p.Languages = new Dictionary<string, double>(p.Languages ?? new Dictionary<string, double>(), c);
            p.Countries = new Dictionary<string, double>(p.Countries ?? new Dictionary<string, double>(), c);
            p.People = new Dictionary<string, double>(p.People ?? new Dictionary<string, double>(), c);
        }

        /// <summary>Builds the profile again in the background when it is older than maxAge; one build at a time.</summary>
        internal static bool RebuildInBackground(IJsonSerializer json, TimeSpan maxAge, Action<List<MissingList.Title>>? lookUp = null)
        {
            if (_library == null || _users == null || _userData == null) return false;
            var current = Current(json);
            if (current != null && DateTime.UtcNow - current.BuiltUtc < maxAge) return Building;
            if (Interlocked.CompareExchange(ref _building, 1, 0) != 0) return true;
            Task.Run(() =>
            {
                try { Build(json, lookUp); }
                catch (Exception ex) { Log.Warn("The taste profile could not be built: " + ex.Message); }
                finally { Volatile.Write(ref _building, 0); }
            });
            return true;
        }

        /// <summary>Builds the profile now when it is older than maxAge (the rules' run waits for it).</summary>
        internal static async Task EnsureAsync(IJsonSerializer json, TimeSpan maxAge, CancellationToken ct)
        {
            RebuildInBackground(json, maxAge);
            while (Building && !ct.IsCancellationRequested) await Task.Delay(500, ct).ConfigureAwait(false);
        }

        // ---- Building it ----

        private sealed class Group
        {
            public BaseItem Rep = null!;
            public BaseItem? Episode;     // a show: one of its episodes, for its audio language
            public bool IsSeries;
            public double Weight;
        }

        private struct Seen
        {
            public int Plays;
            public DateTimeOffset Last;
            public bool Favourite;
            public double? Rating;
        }

        private static void Build(IJsonSerializer json, Action<List<MissingList.Title>>? lookUp)
        {
            var sw = Stopwatch.StartNew();
            var titles = _library!.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Movie", "Series" }, Recursive = true, IsVirtualItem = false })
                .Where(i => i is Movie || i is Series).ToList();

            // One group per film / show; a film's versions (separate items) share their IMDb id.
            var groups = new List<Group>();
            var byFilm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seriesGroup = new Dictionary<long, int>();
            foreach (var item in titles.OrderBy(i => i.InternalId))
            {
                var film = FilmKey(item);
                if (!byFilm.TryGetValue(film, out var g))
                {
                    g = groups.Count;
                    byFilm[film] = g;
                    groups.Add(new Group { Rep = item, IsSeries = item is Series });
                }
                var key = KeyOf(item);
                if (key.Length > 0 && !byKey.ContainsKey(key)) byKey[key] = g;
                if (item is Series) seriesGroup[item.InternalId] = g;
            }
            // A show is watched through its episodes.
            if (seriesGroup.Count > 0)
            {
                var episodes = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    SeriesIds = seriesGroup.Keys.ToArray(),
                    Recursive = true,
                    IsVirtualItem = false
                });
                foreach (var ep in episodes.OfType<Episode>())
                {
                    if (!seriesGroup.TryGetValue(ep.SeriesId, out var g)) continue;
                    var key = KeyOf(ep);
                    if (key.Length > 0 && !byKey.ContainsKey(key)) byKey[key] = g;
                    groups[g].Episode ??= ep;
                }
            }
            long tLibrary = sw.ElapsedMilliseconds;

            // Everyone's watching in the window, per user and title.
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-WindowDays);
            int users = 0, records = 0;
            foreach (var user in _users!.GetUserList(new UserQuery { IsDisabled = false }))
            {
                var seen = new Dictionary<int, Seen>();
                foreach (var r in _userData!.GetAllUserData(user.InternalId) ?? new List<UserItemData>())
                {
                    // Watched = Emby's Played flag with a date in the window (PlayCount also goes up on a play stopped at once).
                    if (!r.Played || !r.LastPlayedDate.HasValue || r.LastPlayedDate.Value < cutoff) continue;
                    if (r.Key == null || !byKey.TryGetValue(r.Key, out var g)) continue;
                    records++;
                    seen.TryGetValue(g, out var s);
                    s.Plays++;
                    if (r.LastPlayedDate.Value > s.Last) s.Last = r.LastPlayedDate.Value;
                    if (r.IsFavorite) s.Favourite = true;
                    if (r.Rating.HasValue) s.Rating = Math.Max(s.Rating ?? 0, r.Rating.Value);
                    seen[g] = s;
                }
                if (seen.Count > 0) users++;
                foreach (var kv in seen)
                {
                    var s = kv.Value;
                    if (s.Rating.HasValue && s.Rating.Value > 0 && s.Rating.Value < 5) continue;   // rated low: not a taste
                    double age = Math.Max(0, (now - s.Last).TotalDays);
                    double w = 0.5 + 0.5 * Math.Exp(-age / 60.0);
                    if (groups[kv.Key].IsSeries) w *= Math.Min(2.0, 1.0 + Math.Log(1 + s.Plays) / 3.0);
                    if (s.Favourite) w *= 1.5;
                    groups[kv.Key].Weight += w;
                }
            }
            long tWatch = sw.ElapsedMilliseconds - tLibrary;

            var watched = groups.Where(g => g.Weight > 0).ToList();
            var profile = new Profile { BuiltUtc = DateTime.UtcNow, Users = users, Titles = watched.Count };
            Fix(profile);
            if (watched.Count > 0)
            {
                // The library's share of each genre, for the lift.
                var libraryGenres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in groups)
                    foreach (var genre in Languages.Genres(g.Rep.Genres).Distinct(StringComparer.OrdinalIgnoreCase))
                        libraryGenres[genre] = (libraryGenres.TryGetValue(genre, out var n) ? n : 0) + 1;

                var people = People(watched);
                var keys = watched.ToDictionary(g => g, g => TitleKeyOf(g.Rep));
                var known = Posters.Known(keys.Values.Where(k => k.Length > 0), json);
                double total = watched.Sum(g => g.Weight), mean = total / watched.Count;
                int fromDetails = 0, fromAudio = 0;
                var ask = new List<(MissingList.Title Title, double Weight)>();
                foreach (var g in watched)
                {
                    known.TryGetValue(keys[g], out var d);
                    var genres = Languages.Genres(g.Rep.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (genres.Count == 0 && d != null) genres = d.Genres;
                    var countries = (g.Rep.ProductionLocations ?? Array.Empty<string>()).Select(Languages.Country).Where(c => c.Length > 0).ToList();
                    if (d != null && d.Countries.Count > 0) countries = d.Countries;
                    var language = d?.Language ?? "";
                    if (language.Length > 0) fromDetails++;
                    else if ((language = AudioLanguage(g.IsSeries ? g.Episode : g.Rep)).Length > 0) fromAudio++;
                    if (d == null && keys[g].Length > 0) ask.Add((ToTitle(g.Rep, keys[g]), g.Weight));

                    double share = g.Weight / total;
                    foreach (var x in genres) Add(profile.Genres, x, share);
                    foreach (var x in countries.Distinct(StringComparer.OrdinalIgnoreCase)) Add(profile.Countries, x, share);
                    if (language.Length > 0) Add(profile.Languages, language, share);
                    if (people.TryGetValue(g.Rep.InternalId, out var names))
                        foreach (var x in names) Add(profile.People, x, g.Weight / mean);
                }
                foreach (var kv in profile.Genres)
                    if (libraryGenres.TryGetValue(kv.Key, out var n) && n > 0) profile.GenreLift[kv.Key] = kv.Value / (n / groups.Count);
                // People seen once in one title tell little and make the file big.
                profile.People = profile.People.Where(kv => kv.Value >= 0.75).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                // The watched titles' original languages, from the same lookup as the missing titles'
                // (the next build uses them; till then the audio language stands in).
                if (ask.Count > 0 && lookUp != null)
                    lookUp(ask.OrderByDescending(a => a.Weight).Take(MaxDetailsAsked).Select(a => a.Title).ToList());
                Log.Debug($"Taste: {fromDetails} watched titles' language from the lookup, {fromAudio} from the audio, {ask.Count} not looked up yet");
            }
            profile.BuildMs = sw.ElapsedMilliseconds;
            Log.Info($"Taste profile: {users} users watched {watched.Count} of {groups.Count} titles in the last {WindowDays} days ({records} records); "
                + $"{profile.Genres.Count} genres, {profile.Languages.Count} languages, {profile.Countries.Count} countries, {profile.People.Count} people; "
                + $"top: {string.Join(", ", profile.Genres.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key))} · "
                + $"{string.Join(", ", profile.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => Languages.Name(kv.Key)))}; "
                + $"library {tLibrary} ms, watch data {tWatch} ms, total {profile.BuildMs} ms");

            lock (Gate)
            {
                _profile = profile;
                _loaded = true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    json.SerializeToFile(profile, FilePath);
                }
                catch (Exception ex) { Log.Debug("The taste profile could not be saved: " + ex.Message); }
            }
        }

        private static void Add(Dictionary<string, double> d, string key, double v) => d[key] = (d.TryGetValue(key, out var had) ? had : 0) + v;

        // Top-billed actors and directors of the watched titles, read in batches (as Next Watch does).
        private static Dictionary<long, List<string>> People(List<Group> watched)
        {
            var result = new Dictionary<long, List<string>>();
            void Put(long id, string? name, int max, PersonType type)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!result.TryGetValue(id, out var l)) result[id] = l = new List<string>();
                int had = l.Count(n => n.StartsWith(type == PersonType.Actor ? "a:" : "d:", StringComparison.Ordinal));
                var v = (type == PersonType.Actor ? "a:" : "d:") + name!.Trim();
                if (had < max && !l.Contains(v)) l.Add(v);
            }
            var ids = watched.Select(g => g.Rep.InternalId).ToArray();
            bool batchWorks = true;
            for (int i = 0; i < ids.Length && batchWorks; i += 400)
            {
                var chunk = ids.Skip(i).Take(400).ToArray();
                var set = new HashSet<long>(chunk);
                List<PersonInfo> a, c;
                try
                {
                    a = _library!.GetItemPeople(new InternalPeopleQuery { ItemIds = chunk, PersonTypes = new[] { PersonType.Actor }, MaxListOrder = MaxActors - 1 }) ?? new List<PersonInfo>();
                    c = _library.GetItemPeople(new InternalPeopleQuery { ItemIds = chunk, PersonTypes = new[] { PersonType.Director } }) ?? new List<PersonInfo>();
                }
                catch { batchWorks = false; break; }
                // PersonInfo.ItemId must name the title for the batch read to be usable.
                if ((a.Count > 0 && !set.Contains(a[0].ItemId)) || (c.Count > 0 && !set.Contains(c[0].ItemId))) { batchWorks = false; break; }
                foreach (var p in a) Put(p.ItemId, p.Name, MaxActors, PersonType.Actor);
                foreach (var p in c) Put(p.ItemId, p.Name, MaxCrew, PersonType.Director);
            }
            if (!batchWorks)
            {
                result.Clear();
                foreach (var g in watched)
                {
                    List<PersonInfo> list;
                    try { list = _library!.GetItemPeople(g.Rep) ?? new List<PersonInfo>(); } catch { continue; }
                    foreach (var p in list.Where(p => p.Type == PersonType.Actor)) Put(g.Rep.InternalId, p.Name, MaxActors, PersonType.Actor);
                    foreach (var p in list.Where(p => p.Type == PersonType.Director)) Put(g.Rep.InternalId, p.Name, MaxCrew, PersonType.Director);
                }
            }
            return result.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v.Substring(2)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        // The first audio stream's language (the default one first), as ISO 639-1.
        private static string AudioLanguage(BaseItem? item)
        {
            if (item == null) return "";
            try
            {
                var audio = (item.GetMediaStreams() ?? new List<MediaStream>()).Where(s => s.Type == MediaStreamType.Audio).ToList();
                var pick = audio.FirstOrDefault(s => s.IsDefault && !string.IsNullOrWhiteSpace(s.Language)) ?? audio.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Language));
                return Languages.Code(pick?.Language);
            }
            catch { return ""; }
        }

        private static string TitleKeyOf(BaseItem item)
        {
            string type = item is Series ? MediaType.Show : MediaType.Movie;
            var imdb = item.GetProviderId("Imdb");
            int.TryParse(item.GetProviderId("Tmdb"), out var tmdb);
            int.TryParse(item.GetProviderId("Tvdb"), out var tvdb);
            if (string.IsNullOrWhiteSpace(imdb) && tmdb <= 0 && (type != MediaType.Show || tvdb <= 0)) return "";
            return MissingList.TitleKey(type, imdb, tmdb, tvdb, item.Name, item.ProductionYear);
        }

        private static MissingList.Title ToTitle(BaseItem item, string key)
        {
            int.TryParse(item.GetProviderId("Tmdb"), out var tmdb);
            int.TryParse(item.GetProviderId("Tvdb"), out var tvdb);
            bool show = item is Series;
            return new MissingList.Title
            {
                Key = key, Type = show ? MediaType.Show : MediaType.Movie, Name = item.Name ?? "", Year = item.ProductionYear,
                Imdb = (item.GetProviderId("Imdb") ?? "").Trim(), Tmdb = tmdb, Tvdb = show ? tvdb : 0
            };
        }

        private static string KeyOf(BaseItem item)
        {
            var key = item.UserDataKey;
            if (string.IsNullOrEmpty(key))
            {
                try { key = item.GetUserDataKey(null); } catch { }
            }
            return key ?? "";
        }

        private static string FilmKey(BaseItem item)
        {
            var imdb = item.GetProviderId("Imdb");
            if (!string.IsNullOrEmpty(imdb)) return "imdb:" + imdb;
            var tmdb = item.GetProviderId("Tmdb");
            if (!string.IsNullOrEmpty(tmdb)) return (item is Series ? "tmdbtv:" : "tmdb:") + tmdb;
            if (!string.IsNullOrEmpty(item.PresentationUniqueKey)) return "puk:" + item.PresentationUniqueKey;
            return "id:" + item.InternalId;
        }

        // ---- Scoring a missing title ----

        internal sealed class Match
        {
            public int Score;
            public string Reason = "";
        }

        /// <summary>How well a title matches the profile; null when either has too little to tell.</summary>
        internal static Match? Score(Profile? p, Posters.Entry? d)
        {
            if (p == null || d == null || p.Titles == 0 || p.Genres.Count == 0 || d.Genres.Count == 0) return null;

            // Genres: share against the most watched genre, and lift over the library.
            double maxGenre = p.Genres.Values.Max();
            var genreAff = d.Genres.Distinct(StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                double share = p.Genres.TryGetValue(g, out var s) ? s : 0;
                double lift = p.GenreLift.TryGetValue(g, out var l) ? l : 0;
                return (Name: g, A: share <= 0 ? 0 : 0.6 * share / maxGenre + 0.4 * Math.Min(1, lift / MaxGenreLift));
            }).ToList();
            double genre = Math.Min(1, genreAff.Sum(x => x.A) / Math.Sqrt(genreAff.Count));

            double parts = GenreW, sum = GenreW * genre;
            double language = -1, country = -1, people = -1;
            if (d.Language.Length > 0 && p.Languages.Count > 0)
            {
                language = p.Languages.TryGetValue(d.Language, out var s) ? Math.Min(1, s / FullShare) : 0;
                parts += LanguageW; sum += LanguageW * language;
            }
            if (d.Countries.Count > 0 && p.Countries.Count > 0)
            {
                country = d.Countries.Select(c => p.Countries.TryGetValue(c, out var s) ? Math.Min(1, s / FullShare) : 0).Max();
                parts += CountryW; sum += CountryW * country;
            }
            var known = new List<(string Name, double A)>();
            if (d.People.Count > 0 && p.Titles >= 3)
            {
                // A person in two titles of average weight counts fully; two such people fill the part.
                known = d.People.Select(n => (Name: n, A: p.People.TryGetValue(n, out var w) ? Math.Min(1, w / 2) : 0)).Where(x => x.A > 0).OrderByDescending(x => x.A).ToList();
                people = Math.Min(1, known.Sum(x => x.A) / 2);
                parts += PeopleW; sum += PeopleW * people;
            }

            var reason = new List<string>();
            var genres = genreAff.Where(x => x.A >= 0.3).OrderByDescending(x => x.A).Take(2).Select(x => x.Name).ToList();
            if (genres.Count > 0) reason.Add(string.Join(", ", genres));
            if (language >= 0.3) reason.Add(Languages.Name(d.Language));
            if (known.Count > 0 && known[0].A >= 0.5) reason.Add(known[0].Name);
            return new Match { Score = (int)Math.Round(100 * sum / parts), Reason = string.Join(" · ", reason) };
        }
    }
}
