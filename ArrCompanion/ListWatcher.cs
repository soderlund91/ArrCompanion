using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArrCompanion
{
    /// <summary>
    /// Runs the rules a few minutes after the missing titles file changes (Home Screen Companion
    /// writes it after its sync and after library changes), when "Run rules when the list
    /// changes" is on and a rule is.
    /// </summary>
    public class ListWatcher : IServerEntryPoint
    {
        // The file is written again soon after a sync when the library changes too: one run after both.
        private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(3);

        private static ListWatcher? _current;
        private readonly IJsonSerializer _json;
        private readonly object _lock = new object();
        private FileSystemWatcher? _watcher;
        private Timer? _startTimer;
        private Timer? _timer;

        public ListWatcher(IApplicationHost appHost, IJsonSerializer json, ILogManager logManager)
        {
            _json = json;
            Log.Initialize(logManager);
            MissingList.Initialize(appHost);
        }

        public void Run()
        {
            _current = this;
            // Home Screen Companion may load after this plugin: its folder is looked up a little later.
            _startTimer = new Timer(_ => Start(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }

        internal static void Restart() => _current?.Start();

        private void Start()
        {
            lock (_lock)
            {
                _watcher?.Dispose();
                _watcher = null;
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.RunRulesOnListChange || !(config.Rules ?? new System.Collections.Generic.List<ArrRule>()).Any(r => r.Enabled)) return;
                var path = MissingList.FilePath(config);
                var folder = path.Length > 0 ? Path.GetDirectoryName(path) : null;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    Log.Debug("Not watching the missing titles file: its folder does not exist yet (the scheduled task still runs the rules)");
                    return;
                }
                try
                {
                    // Written beside the file and moved over it: a rename or a create, not only a change.
                    _watcher = new FileSystemWatcher(folder, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    _watcher.Changed += OnChanged;
                    _watcher.Created += OnChanged;
                    _watcher.Renamed += OnChanged;
                    _watcher.EnableRaisingEvents = true;
                    Log.Debug("Watching " + path);
                }
                catch (Exception ex)
                {
                    Log.Warn("The missing titles file cannot be watched: " + ex.Message);
                }
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            lock (_lock)
            {
                if (_timer == null) _timer = new Timer(_ => RunRules(), null, Debounce, Timeout.InfiniteTimeSpan);
                else _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private async void RunRules()
        {
            try
            {
                var outcome = await Rules.RunAsync(Plugin.Instance.Configuration, _json, false, null, CancellationToken.None).ConfigureAwait(false);
                Log.Info("Rules (the list changed): " + outcome.Message);
                foreach (var note in outcome.Notes) Log.Info("Rules: " + note);
            }
            catch (Exception ex)
            {
                Log.Warn("The rules failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _watcher?.Dispose();
                _watcher = null;
                _startTimer?.Dispose();
                _startTimer = null;
                _timer?.Dispose();
                _timer = null;
            }
            if (_current == this) _current = null;
        }
    }
}
