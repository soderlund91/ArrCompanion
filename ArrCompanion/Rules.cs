using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>
    /// The rules: which missing titles they pick (a preview, or a run that adds them). A title a
    /// rule picks is one that is missing, not ignored, never added by the plugin before, and on
    /// the rule's list within its top places.
    /// </summary>
    internal static class Rules
    {
        private static readonly SemaphoreSlim RunGate = new SemaphoreSlim(1, 1);

        public sealed class Pick
        {
            public string RuleId { get; set; } = "";
            public string RuleName { get; set; } = "";
            public string Key { get; set; } = "";
            public string Title { get; set; } = "";
            public int? Year { get; set; }
            public string Type { get; set; } = "";
            public string List { get; set; } = "";
            public int? Rank { get; set; }
            public string InstanceId { get; set; } = "";
            public string InstanceName { get; set; } = "";
            public string LanguageName { get; set; } = "";
            public int? Match { get; set; }
            // After a run:
            public bool Done { get; set; }
            public bool Success { get; set; }
            public bool AlreadyIn { get; set; }
            public string Message { get; set; } = "";
            internal Catalog.Row Row = null!;
        }

        public sealed class Outcome
        {
            public bool Ran { get; set; }
            public string Message { get; set; } = "";
            public List<Pick> Picks { get; set; } = new List<Pick>();
            public List<string> Notes { get; set; } = new List<string>();   // why a rule picked nothing it could add
        }

        internal static string RuleLabel(ArrRule r) => string.IsNullOrWhiteSpace(r.Name) ? Describe(r) : r.Name.Trim();

        internal static string Describe(ArrRule r)
        {
            var what = r.MediaType == MediaType.Movie ? "movies" : r.MediaType == MediaType.Show ? "shows" : "titles";
            var where = string.IsNullOrWhiteSpace(r.List) ? "any list" : r.List.Trim();
            return (r.TopN > 0 ? $"Top {r.TopN} {what} of {where}" : $"All {what} on {where}") + (r.MinYear > 0 ? $" from {r.MinYear}" : "")
                + ((r.Languages?.Count ?? 0) > 0 ? " in " + Names(r.Languages!) : "")
                + ((r.ExcludeLanguages?.Count ?? 0) > 0 ? " not in " + Names(r.ExcludeLanguages!) : "")
                + (r.MinMatch > 0 ? $", match at least {r.MinMatch}%" : "");
        }

        private static string Names(List<string> codes) => string.Join(", ", codes.Select(Languages.Name));

        private static bool Has(List<string>? codes, string code) =>
            code.Length > 0 && (codes ?? new List<string>()).Any(c => string.Equals(Languages.Code(c), code, StringComparison.OrdinalIgnoreCase));

        // The conditions on what the lookup gives: language and match. A title not looked up yet, or
        // whose language is not known, is not picked by a rule that asks for one.
        private static bool DetailsAllow(ArrRule rule, Catalog.Row row)
        {
            if (!rule.UsesDetails) return true;
            if (!row.HasDetails) return false;
            if ((rule.Languages?.Count ?? 0) > 0 && !Has(rule.Languages, row.Language)) return false;
            if ((rule.ExcludeLanguages?.Count ?? 0) > 0 && (row.Language.Length == 0 || Has(rule.ExcludeLanguages, row.Language))) return false;
            if (rule.MinMatch > 0 && (row.Match == null || row.Match < rule.MinMatch)) return false;
            return true;
        }

        private static MissingList.Place? Matches(ArrRule rule, Catalog.Row row)
        {
            if (!string.IsNullOrEmpty(rule.MediaType) && rule.MediaType != row.Type) return null;
            if (rule.MinYear > 0 && (row.Year == null || row.Year < rule.MinYear)) return null;
            if (!DetailsAllow(rule, row)) return null;
            var places = row.Places.Where(p => string.IsNullOrWhiteSpace(rule.List) || string.Equals(p.List, rule.List.Trim(), StringComparison.OrdinalIgnoreCase));
            if (rule.TopN > 0) places = places.Where(p => p.Rank != null && p.Rank <= rule.TopN);
            return places.OrderBy(p => p.Rank ?? int.MaxValue).FirstOrDefault();
        }

        /// <summary>Why a rule picks nothing: how many titles each step left out, first step first.</summary>
        private static string WhyNothing(ArrRule rule, List<Catalog.Row> rows, HashSet<string> taken, bool preview)
        {
            if (rows.Count == 0) return "the missing titles file has no titles.";
            var counts = new List<(int N, string What)>();
            var left = rows;
            void Step(Func<Catalog.Row, bool> drop, string what)
            {
                var n = left.Count(drop);
                if (n > 0) counts.Add((n, what));
                left = left.Where(r => !drop(r)).ToList();
            }
            Step(r => r.Status == Catalog.Status.InArr, "already in Sonarr / Radarr");
            if (!preview) Step(r => r.Status == Catalog.Status.NoServer, "have no Sonarr / Radarr set up for their type");
            Step(r => r.Status == Catalog.Status.Ignored, "are hidden");
            Step(r => r.AddedBefore, "were added once before");
            Step(r => taken.Contains(r.Key), "are picked by an earlier rule");
            Step(r => !string.IsNullOrEmpty(rule.MediaType) && rule.MediaType != r.Type, rule.MediaType == MediaType.Show ? "are movies" : "are shows");
            Step(r => rule.MinYear > 0 && (r.Year == null || r.Year < rule.MinYear), $"are from before {rule.MinYear} (or have no year)");
            if (rule.UsesDetails)
            {
                Step(r => !r.HasDetails, "have no language or match looked up yet");
                bool asksLanguage = (rule.Languages?.Count ?? 0) > 0 || (rule.ExcludeLanguages?.Count ?? 0) > 0;
                if (asksLanguage) Step(r => r.Language.Length == 0, "have no known language");
                if ((rule.Languages?.Count ?? 0) > 0) Step(r => !Has(rule.Languages, r.Language), "are not in " + Names(rule.Languages!));
                if ((rule.ExcludeLanguages?.Count ?? 0) > 0) Step(r => Has(rule.ExcludeLanguages, r.Language), "are in " + Names(rule.ExcludeLanguages!));
                if (rule.MinMatch > 0)
                {
                    Step(r => r.Match == null, "have no match score yet");
                    Step(r => r.Match < rule.MinMatch, $"match less than {rule.MinMatch}%");
                }
            }
            Step(r => r.Places.Count == 0, "are on no list in the file");
            if (!string.IsNullOrWhiteSpace(rule.List))
                Step(r => !r.Places.Any(p => string.Equals(p.List, rule.List.Trim(), StringComparison.OrdinalIgnoreCase)), $"are not on {rule.List.Trim()}");
            if (rule.TopN > 0)
            {
                bool OnList(MissingList.Place p) => string.IsNullOrWhiteSpace(rule.List) || string.Equals(p.List, rule.List.Trim(), StringComparison.OrdinalIgnoreCase);
                Step(r => !r.Places.Where(OnList).Any(p => p.Rank != null), "have no known place on the list");
                Step(r => Matches(rule, r) == null, $"are not in the top {rule.TopN}");
            }
            var parts = counts.Select(c => $"{c.N} {c.What}").ToList();
            return $"of {rows.Count} titles, " + (parts.Count > 0 ? string.Join(", ", parts) : "none was left out by the rule") + ".";
        }

        /// <summary>
        /// What the rules pick from the catalog. A title two rules pick goes to the first. A
        /// <paramref name="preview"/> also shows the titles that cannot be added yet (no server set
        /// up, or no root folder and profile), so a rule can be tried before Sonarr / Radarr are.
        /// </summary>
        internal static Outcome Picks(PluginConfiguration config, Catalog.Result catalog, IEnumerable<ArrRule> rules, bool preview = false)
        {
            var outcome = new Outcome();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unreadable = catalog.Errors.Count > 0;
            foreach (var rule in rules)
            {
                var label = RuleLabel(rule);
                var matching = catalog.Rows
                    .Where(r => (r.Status == Catalog.Status.Missing || (preview && r.Status == Catalog.Status.NoServer)) && !r.AddedBefore && !taken.Contains(r.Key))
                    .Select(r => (Row: r, Place: Matches(rule, r)))
                    .Where(x => x.Place != null)
                    .OrderBy(x => x.Place!.Rank ?? int.MaxValue).ThenBy(x => x.Row.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                int count = 0;
                foreach (var (row, place) in matching)
                {
                    if (rule.MaxPerRun > 0 && count >= rule.MaxPerRun) break;
                    var kind = ArrKind.For(row.Type);
                    var instance = config.InstanceFor(kind, rule.InstanceId);
                    string? missing = instance == null ? $"Set up {ArrKind.Label(kind)} under Settings: until then these are not added."
                        : !instance.HasDefaults ? $"Pick a root folder and a quality profile for {instance.Label} under Settings: until then these are not added."
                        : null;
                    if (missing != null)
                    {
                        if (!outcome.Notes.Contains(missing)) outcome.Notes.Add(missing);
                        if (!preview) continue;
                    }
                    taken.Add(row.Key);
                    count++;
                    outcome.Picks.Add(new Pick
                    {
                        RuleId = rule.Id, RuleName = label, Key = row.Key, Title = row.Title, Year = row.Year, Type = row.Type,
                        List = place!.List, Rank = place.Rank, InstanceId = instance?.Id ?? "", InstanceName = instance?.Label ?? ArrKind.Label(kind), Row = row,
                        LanguageName = row.LanguageName, Match = row.Match
                    });
                }
                if (count == 0)
                {
                    var why = WhyNothing(rule, catalog.Rows, taken, preview);
                    outcome.Notes.Add($"{label}: {why}");
                    Log.Debug($"Rule \"{label}\" picks nothing: {why}");
                }
            }
            if (unreadable) outcome.Notes.AddRange(catalog.Errors);
            return outcome;
        }

        private static async Task DetailsAsync(PluginConfiguration config, IJsonSerializer json, List<ArrRule> rules, CancellationToken ct)
        {
            var apiKey = Posters.ApiKey(config);
            var titles = Posters.Unknown(MissingList.Read(config, json).Titles, apiKey, json);
            if (titles.Count > 0)
            {
                // One lookup at a time: a background one already running is waited for, then the rest asked.
                while (Posters.Enriching && !ct.IsCancellationRequested) await Task.Delay(500, ct).ConfigureAwait(false);
                await Posters.PathsAsync(Posters.Unknown(titles, apiKey, json), apiKey, json, ct).ConfigureAwait(false);
            }
            if (rules.Any(r => r.MinMatch > 0)) await Taste.EnsureAsync(json, Taste.Stale, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the enabled rules (or the one asked for): with <paramref name="dryRun"/> only says
        /// what they would add. One run at a time.
        /// </summary>
        internal static async Task<Outcome> RunAsync(PluginConfiguration config, IJsonSerializer json, bool dryRun, string? ruleId, CancellationToken ct)
        {
            var rules = (config.Rules ?? new List<ArrRule>())
                .Where(r => string.IsNullOrEmpty(ruleId) ? r.Enabled : r.Id == ruleId).ToList();
            if (rules.Count == 0) return new Outcome { Message = string.IsNullOrEmpty(ruleId) ? "No rules are on." : "That rule no longer exists." };

            if (!dryRun && !await RunGate.WaitAsync(0, ct).ConfigureAwait(false))
                return new Outcome { Message = "The rules are already running." };
            try
            {
                // A rule on language or match: the titles' details and the taste profile first (a preview uses what is kept).
                if (!dryRun && rules.Any(r => r.UsesDetails)) await DetailsAsync(config, json, rules, ct).ConfigureAwait(false);
                var catalog = await Catalog.BuildAsync(config, json, ct).ConfigureAwait(false);
                if (!catalog.File.Found) return new Outcome { Message = catalog.File.Problem ?? "No missing titles file." };
                var outcome = Picks(config, catalog, rules);
                if (dryRun || outcome.Picks.Count == 0)
                {
                    outcome.Message = outcome.Picks.Count == 0 ? "Nothing to add." : $"Would add {outcome.Picks.Count}.";
                    return outcome;
                }
                // Errors on a server's list: its titles may already be there, so nothing is added to it.
                if (catalog.Errors.Count > 0)
                {
                    outcome.Message = "Not run: " + string.Join(" ", catalog.Errors);
                    return outcome;
                }

                var client = new ArrClient(json);
                var entries = new List<History.Entry>();
                foreach (var pick in outcome.Picks)
                {
                    ct.ThrowIfCancellationRequested();
                    var instance = config.InstanceFor(ArrKind.For(pick.Type), pick.InstanceId)!;
                    var res = await client.AddAsync(instance, Catalog.ToTitle(pick.Row), ArrClient.AddOptions.From(instance), ct).ConfigureAwait(false);
                    pick.Done = true;
                    pick.Success = res.Success;
                    pick.AlreadyIn = res.AlreadyIn;
                    pick.Message = res.Message;
                    entries.Add(new History.Entry
                    {
                        AtUtc = DateTime.UtcNow, Key = pick.Key, Title = pick.Title, Year = pick.Year, Type = pick.Type,
                        InstanceId = instance.Id, InstanceName = instance.Label, By = pick.RuleName, List = pick.List, Rank = pick.Rank,
                        Success = res.Success, AlreadyIn = res.AlreadyIn, Message = res.Message
                    });
                    var place = pick.Rank != null ? $" (#{pick.Rank} on {pick.List})" : $" ({pick.List})";
                    if (res.Success) Log.Info($"Rule \"{pick.RuleName}\" added {pick.Title}{place} to {instance.Label}");
                    else if (!res.AlreadyIn) Log.Warn($"Rule \"{pick.RuleName}\" could not add {pick.Title}: {res.Message}");
                }
                History.Add(entries, json);
                outcome.Ran = true;
                int ok = outcome.Picks.Count(p => p.Success), failed = outcome.Picks.Count(p => !p.Success && !p.AlreadyIn);
                outcome.Message = $"Added {ok}" + (failed > 0 ? $", {failed} failed" : "") + ".";
                return outcome;
            }
            finally
            {
                if (!dryRun) RunGate.Release();
            }
        }
    }
}
