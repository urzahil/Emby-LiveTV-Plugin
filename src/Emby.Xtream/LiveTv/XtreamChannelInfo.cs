using MediaBrowser.Controller.LiveTv;

namespace Emby.Xtream.LiveTv
{
    public class XtreamChannelInfo : ChannelInfo
    {
        public string StreamId { get; set; }
        public string EpgChannelId { get; set; }
        public string CategoryId { get; set; }
        public string CategoryName { get; set; }
        public bool TvArchive { get; set; }
        public int TvArchiveDays { get; set; }
    }
}
