using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    // ---- Requests ----

    [Route("/ArrCompanion/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class TestRequest : IReturn<TestResponse>
    {
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public class TestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Version { get; set; } = "";
    }

    // The settings page asks with the URL and key it shows (they may not be saved yet).
    [Route("/ArrCompanion/Options", "POST")]
    [Authenticated(Roles = "Admin")]
    public class OptionsRequest : IReturn<OptionsResponse>
    {
        public string InstanceId { get; set; } = "";
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public class OptionsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public List<FolderOption> RootFolders { get; set; } = new List<FolderOption>();
        public List<ProfileOption> QualityProfiles { get; set; } = new List<ProfileOption>();
    }

    public class FolderOption { public string Path { get; set; } = ""; public long? FreeSpace { get; set; } }
    public class ProfileOption { public int Id { get; set; } public string Name { get; set; } = ""; }

    [Route("/ArrCompanion/Missing", "GET")]
    [Authenticated(Roles = "Admin")]
    public class MissingRequest : IReturn<Catalog.Result> { }

    [Route("/ArrCompanion/Add", "POST")]
    [Authenticated(Roles = "Admin")]
    public class AddRequest : IReturn<AddResponse>
    {
        public List<string> Keys { get; set; } = new List<string>();
        // What the add dialog picked, per kind; null = the server's defaults.
        public AddChoice? Radarr { get; set; }
        public AddChoice? Sonarr { get; set; }
    }

    public class AddChoice
    {
        public string InstanceId { get; set; } = "";
        public string RootFolderPath { get; set; } = "";
        public int QualityProfileId { get; set; }
        public bool Monitored { get; set; } = true;
        public bool Search { get; set; } = true;
        public string MinimumAvailability { get; set; } = "";
        public string SeriesMonitor { get; set; } = "";
    }

    public class AddResponse
    {
        public List<AddItemResult> Results { get; set; } = new List<AddItemResult>();
    }

    public class AddItemResult
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Success { get; set; }
        public bool AlreadyIn { get; set; }
        public string Message { get; set; } = "";
    }

    [Route("/ArrCompanion/Ignore", "POST")]
    [Authenticated(Roles = "Admin")]
    public class IgnoreRequest : IReturnVoid
    {
        public List<string> Keys { get; set; } = new List<string>();
        public bool Ignore { get; set; } = true;
    }

    [Route("/ArrCompanion/Rules/Run", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RunRulesRequest : IReturn<Rules.Outcome>
    {
        public bool DryRun { get; set; } = true;
        public string RuleId { get; set; } = "";
        // A rule being edited: its preview before it is saved.
        public ArrRule? Rule { get; set; }
    }

    [Route("/ArrCompanion/History", "GET")]
    [Authenticated(Roles = "Admin")]
    public class HistoryRequest : IReturn<List<History.Entry>>
    {
        public int Count { get; set; } = 200;
    }

    // ---- The service ----

    public class ArrCompanionService : IService
    {
        private readonly IJsonSerializer _json;

        public ArrCompanionService(IJsonSerializer json, ILogManager logManager)
        {
            _json = json;
            Log.Initialize(logManager);
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public async Task<object> Post(TestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.ApiKey))
                return new TestResponse { Message = "The URL and the API key are both needed." };
            try
            {
                var version = await new ArrClient(_json).TestAsync(request.Url, request.ApiKey, CancellationToken.None).ConfigureAwait(false);
                return new TestResponse { Success = true, Version = version, Message = "Connected" + (version.Length > 0 ? ", version " + version : "") + "." };
            }
            catch (Exception ex)
            {
                return new TestResponse { Message = "Could not connect: " + ex.Message };
            }
        }

        public async Task<object> Post(OptionsRequest request)
        {
            var instance = (Config.Instances ?? new List<ArrInstance>()).FirstOrDefault(i => i.Id == request.InstanceId);
            if (!string.IsNullOrWhiteSpace(request.Url) && !string.IsNullOrWhiteSpace(request.ApiKey))
                instance = new ArrInstance { Url = request.Url, ApiKey = request.ApiKey, Kind = instance?.Kind ?? ArrKind.Radarr };
            if (instance == null || !instance.IsSetUp)
                return new OptionsResponse { Message = "Fill in the URL and the API key first." };
            try
            {
                var (folders, profiles) = await new ArrClient(_json).OptionsAsync(instance, CancellationToken.None).ConfigureAwait(false);
                return new OptionsResponse
                {
                    Success = true,
                    RootFolders = folders.Where(f => !string.IsNullOrEmpty(f.path)).Select(f => new FolderOption { Path = f.path!, FreeSpace = f.freeSpace }).ToList(),
                    QualityProfiles = profiles.Select(p => new ProfileOption { Id = p.id, Name = p.name ?? "" }).ToList()
                };
            }
            catch (Exception ex)
            {
                return new OptionsResponse { Message = "Could not read the root folders and profiles: " + ex.Message };
            }
        }

        public async Task<object> Get(MissingRequest request) =>
            await Catalog.BuildAsync(Config, _json, CancellationToken.None).ConfigureAwait(false);

        public async Task<object> Post(AddRequest request)
        {
            var config = Config;
            var response = new AddResponse();
            var keys = new HashSet<string>(request.Keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (keys.Count == 0) return response;

            // The titles as the file has them, so their ids come from it and not from the page.
            var catalog = await Catalog.BuildAsync(config, _json, CancellationToken.None).ConfigureAwait(false);
            var client = new ArrClient(_json);
            var entries = new List<History.Entry>();
            foreach (var row in catalog.Rows.Where(r => keys.Contains(r.Key)))
            {
                var kind = ArrKind.For(row.Type);
                var choice = kind == ArrKind.Sonarr ? request.Sonarr : request.Radarr;
                var instance = config.InstanceFor(kind, choice?.InstanceId);
                var item = new AddItemResult { Key = row.Key, Title = row.Title };
                response.Results.Add(item);
                if (instance == null)
                {
                    item.Message = $"No {ArrKind.Label(kind)} is set up.";
                    continue;
                }
                var options = ArrClient.AddOptions.From(instance);
                if (choice != null)
                {
                    if (!string.IsNullOrWhiteSpace(choice.RootFolderPath)) options.RootFolderPath = choice.RootFolderPath.Trim();
                    if (choice.QualityProfileId > 0) options.QualityProfileId = choice.QualityProfileId;
                    options.Monitored = choice.Monitored;
                    options.Search = choice.Search;
                    if (!string.IsNullOrWhiteSpace(choice.MinimumAvailability)) options.MinimumAvailability = choice.MinimumAvailability;
                    if (!string.IsNullOrWhiteSpace(choice.SeriesMonitor)) options.SeriesMonitor = choice.SeriesMonitor;
                }
                var res = await client.AddAsync(instance, Catalog.ToTitle(row), options, CancellationToken.None).ConfigureAwait(false);
                item.Success = res.Success;
                item.AlreadyIn = res.AlreadyIn;
                item.Message = res.Message;
                var best = row.Places.Where(p => p.Rank != null).OrderBy(p => p.Rank).FirstOrDefault() ?? row.Places.FirstOrDefault();
                entries.Add(new History.Entry
                {
                    AtUtc = DateTime.UtcNow, Key = row.Key, Title = row.Title, Year = row.Year, Type = row.Type,
                    InstanceId = instance.Id, InstanceName = instance.Label, By = "manual", List = best?.List ?? "", Rank = best?.Rank,
                    Success = res.Success, AlreadyIn = res.AlreadyIn, Message = res.Message
                });
                if (res.Success) Log.Info($"Added {row.Title} to {instance.Label}");
            }
            foreach (var key in keys.Where(k => response.Results.All(r => !string.Equals(r.Key, k, StringComparison.OrdinalIgnoreCase))))
                response.Results.Add(new AddItemResult { Key = key, Message = "No longer on the missing list." });
            if (entries.Count > 0) History.Add(entries, _json);
            return response;
        }

        public void Post(IgnoreRequest request)
        {
            var config = Config;
            var set = new HashSet<string>(config.Ignored ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var k in request.Keys ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (request.Ignore) set.Add(k.Trim());
                else set.Remove(k.Trim());
            }
            config.Ignored = set.ToList();
            Plugin.Instance.SaveConfiguration();
        }

        public async Task<object> Post(RunRulesRequest request)
        {
            var config = Config;
            if (request.Rule != null)
            {
                // Previewed on its own, as the page has it now.
                var rule = request.Rule;
                var catalog = await Catalog.BuildAsync(config, _json, CancellationToken.None).ConfigureAwait(false);
                if (!catalog.File.Found) return new Rules.Outcome { Message = catalog.File.Problem ?? "No missing titles file." };
                var outcome = Rules.Picks(config, catalog, new[] { rule });
                outcome.Message = outcome.Picks.Count == 0 ? "Nothing to add." : $"Would add {outcome.Picks.Count}.";
                return outcome;
            }
            return await Rules.RunAsync(config, _json, request.DryRun, string.IsNullOrEmpty(request.RuleId) ? null : request.RuleId, CancellationToken.None).ConfigureAwait(false);
        }

        public object Get(HistoryRequest request) =>
            History.Recent(request.Count <= 0 ? 200 : Math.Min(request.Count, 1000), _json);
    }
}
