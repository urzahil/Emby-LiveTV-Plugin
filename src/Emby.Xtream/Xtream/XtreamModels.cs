using System;
using System.Collections.Generic;

namespace Emby.Xtream.Xtream
{
    public sealed class XtreamUserInfo
    {
        public bool Auth { get; set; }
        public string Status { get; set; }
        /// <summary>Unix seconds (UTC); null when unlimited / not provided.</summary>
        public long? ExpDate { get; set; }
        public int MaxConnections { get; set; }
        public int ActiveConnections { get; set; }
        public bool IsTrial { get; set; }
        public List<string> AllowedOutputFormats { get; set; } = new List<string>();
    }

    public sealed class XtreamServerInfo
    {
        public string Url { get; set; }
        public string Port { get; set; }
        public string HttpsPort { get; set; }
        public string ServerProtocol { get; set; }
        /// <summary>IANA (or Windows) timezone name of the provider, e.g. "Europe/Berlin".</summary>
        public string Timezone { get; set; }
        public long TimestampNow { get; set; }
        /// <summary>Provider local time as "yyyy-MM-dd HH:mm:ss".</summary>
        public string TimeNow { get; set; }
    }

    public sealed class XtreamAccount
    {
        public XtreamUserInfo UserInfo { get; set; } = new XtreamUserInfo();
        public XtreamServerInfo ServerInfo { get; set; } = new XtreamServerInfo();
    }

    public sealed class XtreamCategory
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ParentId { get; set; }
    }

    public sealed class XtreamLiveStream
    {
        public int Num { get; set; }
        public string Name { get; set; }
        public string StreamId { get; set; }
        public string StreamIcon { get; set; }
        public string EpgChannelId { get; set; }
        public long Added { get; set; }
        public string CategoryId { get; set; }
        public string DirectSource { get; set; }
        public bool TvArchive { get; set; }
        public int TvArchiveDuration { get; set; }
    }

    public sealed class XtreamEpgEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Lang { get; set; }
        public string ChannelId { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime StopUtc { get; set; }
        public bool HasArchive { get; set; }
        public bool NowPlaying { get; set; }
    }

    public sealed class XtreamException : Exception
    {
        public XtreamException(string message) : base(message) { }
        public XtreamException(string message, Exception inner) : base(message, inner) { }
    }
}
