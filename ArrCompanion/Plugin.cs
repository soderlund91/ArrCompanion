using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArrCompanion
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Arr Companion";

        // Stable GUID – never change after first deployment
        public override Guid Id => new Guid("b4e2c6a1-58d3-4f0e-9a7c-3d1f62e8b905");

        public override string Description => "Adds the missing titles of Home Screen Companion's lists to Sonarr and Radarr, by hand or by rules.";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; } = null!;

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration is PluginConfiguration incoming)
            {
                // The ignored titles are the Missing tab's own record (Ignore buttons, saved at once);
                // a settings page loaded before them would otherwise undo them.
                incoming.Ignored = Configuration?.Ignored ?? new List<string>();
                incoming.Normalize();
            }
            base.UpdateConfiguration(configuration);
            // Another file path, or rules turned on or off: the watcher follows.
            ListWatcher.Restart();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var names = GetType().Assembly.GetManifestResourceNames();
            var html = names.FirstOrDefault(r => r.EndsWith("configPage.html"));
            var js = names.FirstOrDefault(r => r.EndsWith("configPage.js"));
            if (html == null || js == null) return new List<PluginPageInfo>();
            return new List<PluginPageInfo>
            {
                new PluginPageInfo
                {
                    Name = "ArrCompanionConfig",
                    EmbeddedResourcePath = html,
                    EnableInMainMenu = true,
                    DisplayName = "Arr Companion",
                    MenuIcon = "playlist_add"
                },
                new PluginPageInfo
                {
                    Name = "ArrCompanionJS",
                    EmbeddedResourcePath = js
                }
            };
        }
    }
}
