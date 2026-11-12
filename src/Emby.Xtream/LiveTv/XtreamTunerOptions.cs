namespace Emby.Xtream.LiveTv
{
    /// <summary>Stored as JSON in TunerHostInfo.ProviderOptions.</summary>
    public class XtreamTunerOptions
    {
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>"ts" (default) or "m3u8".</summary>
        public string StreamFormat { get; set; } = "ts";

        /// <summary>Xtream category ids to import; empty = all.</summary>
        public string[] Categories { get; set; } = new string[0];

        public bool ImportGuideData { get; set; } = true;

        /// <summary>"xmltv" (default) or "api".</summary>
        public string GuideSource { get; set; } = "xmltv";

        public bool EnableCatchup { get; set; } = true;

        /// <summary>Per-provider cap on catch-up days; 0 = use the plugin-wide setting.</summary>
        public int CatchupMaxDays { get; set; }

        /// <summary>"query" (/streaming/timeshift.php?...) or "path" (/timeshift/U/P/min/start/id.ts).</summary>
        public string CatchupUrlStyle { get; set; } = "query";
    }
}
