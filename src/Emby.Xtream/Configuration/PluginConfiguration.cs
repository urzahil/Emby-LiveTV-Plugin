using MediaBrowser.Model.Plugins;

namespace Emby.Xtream.Configuration
{
    /// <summary>Global (plugin-wide) settings. Per-provider settings live in the tuner's ProviderOptions.</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>How long a downloaded XMLTV guide / API EPG table is reused.</summary>
        public int EpgCacheHours { get; set; } = 6;

        /// <summary>Upper limit of days offered in the catch-up channel (also limited by the provider's tv_archive_duration).</summary>
        public int CatchupMaxDays { get; set; } = 7;

        /// <summary>Show the "IPTV Catch-up" channel at all.</summary>
        public bool EnableCatchupChannel { get; set; } = true;

        /// <summary>Timeout for requests to the provider.</summary>
        public int HttpTimeoutSeconds { get; set; } = 60;

        /// <summary>How many days ahead of now guide data is imported.</summary>
        public int GuideDays { get; set; } = 3;
    }
}
