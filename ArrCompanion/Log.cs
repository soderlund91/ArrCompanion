using MediaBrowser.Model.Logging;

namespace ArrCompanion
{
    /// <summary>The plugin's lines in Emby's log, under "ArrCompanion".</summary>
    internal static class Log
    {
        private static ILogger? _logger;

        internal static void Initialize(ILogManager logManager) => _logger ??= logManager.GetLogger("ArrCompanion");

        internal static void Info(string message) => _logger?.Info(message);
        internal static void Warn(string message) => _logger?.Warn(message);
        internal static void Debug(string message) => _logger?.Debug(message);
    }
}
