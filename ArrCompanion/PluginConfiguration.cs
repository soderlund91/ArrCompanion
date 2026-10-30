using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArrCompanion
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Sonarr and Radarr servers. The page shows one of each for now; everything that picks a
        // server goes through ArrInstance.Id, so more of each kind only needs the page.
        public List<ArrInstance> Instances { get; set; } = new List<ArrInstance>();

        // The missing titles file (missing_titles.json). Empty = the one Home Screen Companion keeps.
        public string MissingListPath { get; set; } = "";

        // For the posters. Empty = Home Screen Companion's TMDb key.
        public string TmdbApiKey { get; set; } = "";

        public List<ArrRule> Rules { get; set; } = new List<ArrRule>();
        // Rules run a few minutes after the file changes, not only on the scheduled task.
        public bool RunRulesOnListChange { get; set; } = true;

        // Titles never shown as missing nor added by a rule (TitleKey).
        public List<string> Ignored { get; set; } = new List<string>();

        /// <summary>Ids for new instances and rules, and a rule's server that no longer exists.</summary>
        internal void Normalize()
        {
            Instances ??= new List<ArrInstance>();
            Rules ??= new List<ArrRule>();
            Ignored ??= new List<string>();
            foreach (var i in Instances)
            {
                if (string.IsNullOrWhiteSpace(i.Id)) i.Id = Guid.NewGuid().ToString("N");
                i.Kind = i.Kind == ArrKind.Sonarr ? ArrKind.Sonarr : ArrKind.Radarr;
            }
            foreach (var r in Rules)
            {
                if (string.IsNullOrWhiteSpace(r.Id)) r.Id = Guid.NewGuid().ToString("N");
                if (!string.IsNullOrEmpty(r.InstanceId) && Instances.All(i => i.Id != r.InstanceId)) r.InstanceId = "";
            }
            Ignored = Ignored.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The server for a kind: the one asked for, else the first of that kind that is set up.</summary>
        internal ArrInstance? InstanceFor(string kind, string? instanceId = null)
        {
            var ofKind = (Instances ?? new List<ArrInstance>()).Where(i => i.Kind == kind && i.IsSetUp).ToList();
            return ofKind.FirstOrDefault(i => !string.IsNullOrEmpty(instanceId) && i.Id == instanceId) ?? ofKind.FirstOrDefault();
        }
    }

    public static class ArrKind
    {
        public const string Sonarr = "sonarr";
        public const string Radarr = "radarr";

        public static string For(string mediaType) => mediaType == MediaType.Show ? Sonarr : Radarr;
        public static string Label(string kind) => kind == Sonarr ? "Sonarr" : "Radarr";
    }

    public static class MediaType
    {
        public const string Movie = "movie";
        public const string Show = "show";
    }

    public class ArrInstance
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = ArrKind.Radarr;
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";

        // What an add uses unless the add dialog says otherwise (a rule always uses these).
        public string RootFolderPath { get; set; } = "";
        public int QualityProfileId { get; set; }
        public bool Monitored { get; set; } = true;
        public bool SearchOnAdd { get; set; } = true;
        public string MinimumAvailability { get; set; } = "released";   // Radarr: announced | inCinemas | released
        public string SeriesMonitor { get; set; } = "all";               // Sonarr: all | future | missing | existing | firstSeason | latestSeason | pilot | none
        public bool SeasonFolder { get; set; } = true;                   // Sonarr

        internal string BaseUrl => (Url ?? "").Trim().TrimEnd('/');
        internal string Key => (ApiKey ?? "").Trim();
        internal bool IsSetUp => BaseUrl.Length > 0 && Key.Length > 0;
        internal bool HasDefaults => (RootFolderPath ?? "").Trim().Length > 0 && QualityProfileId > 0;
        internal string Label => string.IsNullOrWhiteSpace(Name) ? ArrKind.Label(Kind) : Name.Trim();
    }

    /// <summary>
    /// Adds the titles that are missing from the library and high on a list: "the top 3 of
    /// Trending", "every movie on Oscars 2026 from 2020 on".
    /// </summary>
    public class ArrRule
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string List { get; set; } = "";          // the list's name in the file; "" = any list
        public string MediaType { get; set; } = "";     // movie | show | "" = both
        public int TopN { get; set; } = 3;              // places 1..TopN on the list; 0 = anywhere on it
        public int MinYear { get; set; }                // 0 = any year
        public int MaxPerRun { get; set; }              // 0 = no limit
        public string InstanceId { get; set; } = "";    // "" = the first server of the kind
    }
}
