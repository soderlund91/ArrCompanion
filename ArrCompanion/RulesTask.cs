using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArrCompanion
{
    /// <summary>Runs the rules on a schedule (every 6 hours unless changed under Scheduled Tasks).</summary>
    public class RulesTask : IScheduledTask
    {
        private readonly IJsonSerializer _json;

        public RulesTask(IJsonSerializer json, ILogManager logManager)
        {
            _json = json;
            Log.Initialize(logManager);
        }

        public string Key => "ArrCompanionRules";
        public string Name => "Run rules";
        public string Description => "Adds the missing titles the rules pick to Sonarr and Radarr.";
        public string Category => "Arr Companion";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(6).Ticks } };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            var outcome = await Rules.RunAsync(Plugin.Instance.Configuration, _json, false, null, cancellationToken).ConfigureAwait(false);
            Log.Info("Rules: " + outcome.Message);
            foreach (var note in outcome.Notes) Log.Info("Rules: " + note);
            progress.Report(100);
        }
    }
}
