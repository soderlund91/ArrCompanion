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
            return (r.TopN > 0 ? $"Top {r.TopN} {what} of {where}" : $"All {what} on {where}") + (r.MinYear > 0 ? $" from {r.MinYear}" : "");
        }

        private static MissingList.Place? Matches(ArrRule rule, Catalog.Row row)
        {
            if (!string.IsNullOrEmpty(rule.MediaType) && rule.MediaType != row.Type) return null;
            if (rule.MinYear > 0 && (row.Year == null || row.Year < rule.MinYear)) return null;
            var places = row.Places.Where(p => string.IsNullOrWhiteSpace(rule.List) || string.Equals(p.List, rule.List.Trim(), StringComparison.OrdinalIgnoreCase));
            if (rule.TopN > 0) places = places.Where(p => p.Rank != null && p.Rank <= rule.TopN);
            return places.OrderBy(p => p.Rank ?? int.MaxValue).FirstOrDefault();
        }

        /// <summary>What the rules pick from the catalog. A title two rules pick goes to the first.</summary>
        internal static Outcome Picks(PluginConfiguration config, Catalog.Result catalog, IEnumerable<ArrRule> rules)
        {
            var outcome = new Outcome();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unreadable = catalog.Errors.Count > 0;
            foreach (var rule in rules)
            {
                var label = RuleLabel(rule);
                var matching = catalog.Rows
                    .Where(r => r.Status == Catalog.Status.Missing && !r.AddedBefore && !taken.Contains(r.Key))
                    .Select(r => (Row: r, Place: Matches(rule, r)))
                    .Where(x => x.Place != null)
                    .OrderBy(x => x.Place!.Rank ?? int.MaxValue).ThenBy(x => x.Row.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                int count = 0;
                foreach (var (row, place) in matching)
                {
                    if (rule.MaxPerRun > 0 && count >= rule.MaxPerRun) break;
                    var instance = config.InstanceFor(ArrKind.For(row.Type), rule.InstanceId);
                    if (instance == null) continue;
                    if (!instance.HasDefaults)
                    {
                        var note = $"{label}: pick a root folder and a quality profile for {instance.Label} under Settings.";
                        if (!outcome.Notes.Contains(note)) outcome.Notes.Add(note);
                        continue;
                    }
                    taken.Add(row.Key);
                    count++;
                    outcome.Picks.Add(new Pick
                    {
                        RuleId = rule.Id, RuleName = label, Key = row.Key, Title = row.Title, Year = row.Year, Type = row.Type,
                        List = place!.List, Rank = place.Rank, InstanceId = instance.Id, InstanceName = instance.Label, Row = row
                    });
                }
            }
            if (unreadable) outcome.Notes.AddRange(catalog.Errors);
            return outcome;
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
