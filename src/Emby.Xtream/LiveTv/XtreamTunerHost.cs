using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.XmlTv;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Xtream.LiveTv
{
    /// <summary>Xtream Codes provider as an Emby Live TV tuner. Thin: protocol and parsing logic lives in the Xtream/ layer.</summary>
    public class XtreamTunerHost : BaseTunerHost, ITunerHost, IHasProgramCaches
    {
        private sealed class ChannelIndex
        {
            public int Count;
            public string OptionsKey;
            public Dictionary<string, string> EpgIdByStreamId;
            public HashSet<string> EpgIds;
            public string Signature;
        }

        private readonly IHttpClient _httpClient;
        private readonly GuideCache _guideCache;
        private readonly EpgTableCache _apiEpg = new EpgTableCache(4);
        private readonly Dictionary<string, ChannelIndex> _indexes = new Dictionary<string, ChannelIndex>(StringComparer.OrdinalIgnoreCase);
        private readonly object _indexGate = new object();

        public XtreamTunerHost(IServerApplicationHost applicationHost, IHttpClient httpClient)
            : base(applicationHost)
        {
            _httpClient = httpClient;
            _guideCache = new GuideCache(Config.ApplicationPaths.CachePath, Logger);
        }

        public override string Name => "Xtream Codes (IPTV)";

        public override string Type => XtreamDefaults.TunerType;

        public override string SetupUrl => BasePlugin.GetPluginPageUrl("xtreamtuner");

        // channel ids become "{tuner.Id}_{stream_id}"
        protected override bool UseTunerHostIdAsPrefix => true;

        public void ClearProgramCaches()
        {
            _guideCache.Clear();
            _apiEpg.Clear();
        }

        public override TunerHostInfo GetDefaultConfiguration()
        {
            var info = base.GetDefaultConfiguration();
            info.TunerCount = 1;
            info.AllowMappingByNumber = false;
            info.SetupUrl = SetupUrl;
            return info;
        }

        public override Task OnSaved(TunerHostInfo tuner, bool isNew, CancellationToken cancellationToken)
        {
            if (isNew) tuner.DataVersion = 1;
            if (string.IsNullOrEmpty(tuner.SetupUrl)) tuner.SetupUrl = SetupUrl;
            InvalidateIndex(tuner);
            return base.OnSaved(tuner, isNew, cancellationToken);
        }

        public override bool SupportsGuideData(TunerHostInfo tuner) =>
            XtreamTunerSupport.GetOptions(JsonSerializer, tuner).ImportGuideData;

        public override async Task ValdidateOptions(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var options = XtreamTunerSupport.GetOptions(JsonSerializer, tuner);
            var creds = XtreamTunerSupport.GetCredentials(tuner, options);
            if (!creds.IsComplete)
            {
                const string missing = "Server URL, username and password are required.";
                Logger.Error("Xtream tuner validation failed: {0}", missing);
                throw new ArgumentException(missing);
            }

            var client = XtreamTunerSupport.CreateClient(_httpClient, tuner, options);
            XtreamAccount account;
            try
            {
                account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (XtreamException ex)
            {
                Logger.Error("Xtream tuner validation failed for {0}: {1}", XtreamUrlBuilder.Sanitize(creds.BaseUrl), ex.Message);
                throw;
            }

            string problem = XtreamClient.GetAccountProblem(account, DateTime.UtcNow);
            if (problem != null)
            {
                Logger.Error("Xtream tuner validation failed for {0}: {1}", XtreamUrlBuilder.Sanitize(creds.BaseUrl), problem);
                throw new InvalidOperationException(problem);
            }
        }

        // ---- channels -------------------------------------------------------------

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            InvalidateIndex(tuner);

            var options = XtreamTunerSupport.GetOptions(JsonSerializer, tuner);
            var creds = XtreamTunerSupport.GetCredentials(tuner, options);
            if (!creds.IsComplete) throw new ArgumentException("Server URL, username and password are required.");

            var client = XtreamTunerSupport.CreateClient(_httpClient, tuner, options);
            string ext = XtreamTunerSupport.StreamExtension(options);

            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var c in await client.GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false))
                    categories[c.Id] = c.Name;
            }
            catch (XtreamException ex)
            {
                // categories are cosmetic; keep going without them
                Logger.Warn("Could not load Xtream categories: {0}", ex.Message);
            }

            var streams = await client.GetLiveStreamsAsync(cancellationToken).ConfigureAwait(false);

            HashSet<string> allowed = options.Categories != null && options.Categories.Length > 0
                ? new HashSet<string>(options.Categories, StringComparer.Ordinal)
                : null;

            var result = new List<ChannelInfo>(streams.Count);
            int ordinal = 0;
            foreach (var s in streams)
            {
                ordinal++;
                if (allowed != null && (s.CategoryId == null || !allowed.Contains(s.CategoryId))) continue;

                string categoryName;
                categories.TryGetValue(s.CategoryId ?? string.Empty, out categoryName);

                result.Add(new XtreamChannelInfo
                {
                    Id = CreateEmbyChannelId(tuner, s.StreamId),
                    Name = s.Name,
                    Number = (s.Num > 0 ? s.Num : ordinal).ToString(CultureInfo.InvariantCulture),
                    ImageUrl = s.StreamIcon,
                    ChannelType = ChannelType.TV,
                    Path = client.Urls.Live(s.StreamId, ext),
                    TunerChannelId = s.EpgChannelId,
                    Tags = categoryName != null ? new[] { categoryName } : new string[0],
                    StreamId = s.StreamId,
                    EpgChannelId = s.EpgChannelId,
                    CategoryId = s.CategoryId,
                    CategoryName = categoryName,
                    TvArchive = s.TvArchive,
                    TvArchiveDays = s.TvArchiveDuration
                });
            }

            Logger.Info("Xtream tuner {0}: {1} channels imported ({2} offered by provider)", tuner.FriendlyName ?? tuner.Id, result.Count, streams.Count);
            return result;
        }

        // The base class caches channels as JSON and would read them back as plain ChannelInfo.
        protected override async Task<List<ChannelInfo>> DeserializeChannelsFromFileAsync(string file)
        {
            var list = await JsonSerializer.DeserializeFromFileAsync<List<XtreamChannelInfo>>(file).ConfigureAwait(false);
            return list == null ? null : list.Cast<ChannelInfo>().ToList();
        }

        // ---- streams --------------------------------------------------------------

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, BaseItem dbChannel, ChannelInfo tunerChannel, CancellationToken cancellationToken)
        {
            var options = XtreamTunerSupport.GetOptions(JsonSerializer, tuner);
            string streamId = GetTunerChannelIdFromEmbyChannelId(tuner, tunerChannel.Id);
            string ext = XtreamTunerSupport.StreamExtension(options);

            // Rebuild from the current credentials rather than trusting the cached Path.
            var urls = new XtreamUrlBuilder(XtreamTunerSupport.GetCredentials(tuner, options));
            string url = urls.Live(streamId, ext);

            string userAgent = XtreamTunerSupport.GetUserAgent(tuner);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = userAgent
            };
            string referrer = string.IsNullOrWhiteSpace(tuner.Referrer) ? null : tuner.Referrer;
            if (referrer != null)
            {
                headers["Referer"] = referrer;
                headers["Referrer"] = referrer;
            }

            var source = new MediaSourceInfo
            {
                Path = url,
                Protocol = MediaProtocol.Http,
                Id = url.GetMD5().ToString("N"),
                Container = ext == "ts" ? "ts" : null,
                IsInfiniteStream = true,
                IsRemote = true,
                RequiresOpening = true,
                RequiresClosing = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                RequiredHttpHeaders = headers,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Index = -1, IsInterlaced = true },
                    new MediaStream { Type = MediaStreamType.Audio, Index = -1 }
                }
            };
            source.InferTotalBitrate();

            return Task.FromResult(new List<MediaSourceInfo> { source });
        }

        // ---- guide ----------------------------------------------------------------

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(TunerHostInfo tuner, string tunerChannelId, DateTimeOffset startDateUtc, DateTimeOffset endDateUtc, CancellationToken cancellationToken)
        {
            var options = XtreamTunerSupport.GetOptions(JsonSerializer, tuner);
            if (!options.ImportGuideData) return new List<ProgramInfo>();

            var plugin = XtreamTunerSupport.PluginConfig;
            var ttl = TimeSpan.FromHours(Math.Max(1, plugin.EpgCacheHours));
            var client = XtreamTunerSupport.CreateClient(_httpClient, tuner, options);

            if (string.Equals(options.GuideSource, "api", StringComparison.OrdinalIgnoreCase))
            {
                string key = tuner.Id + "|" + tunerChannelId;
                var entries = await _apiEpg.GetAsync(key, ttl,
                    () => client.GetSimpleDataTableAsync(tunerChannelId, null, cancellationToken), cancellationToken).ConfigureAwait(false);

                return entries
                    .Where(e => e.StopUtc > startDateUtc.UtcDateTime && e.StartUtc < endDateUtc.UtcDateTime)
                    .Select(e => new ProgramInfo
                    {
                        Name = e.Title,
                        Overview = e.Description,
                        StartDate = new DateTimeOffset(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc)),
                        EndDate = new DateTimeOffset(DateTime.SpecifyKind(e.StopUtc, DateTimeKind.Utc))
                    }).ToList();
            }

            var index = await GetIndexAsync(tuner, options, cancellationToken).ConfigureAwait(false);
            string epgId;
            if (!index.EpgIdByStreamId.TryGetValue(tunerChannelId, out epgId) || string.IsNullOrEmpty(epgId))
                return new List<ProgramInfo>();

            var all = await _guideCache.GetAsync(tuner.Id, client, index.EpgIds, index.Signature, ttl, plugin.GuideDays, cancellationToken).ConfigureAwait(false);

            List<XmlTvProgram> programs;
            if (!all.TryGetValue(epgId, out programs)) return new List<ProgramInfo>();

            DateTime from = startDateUtc.UtcDateTime, to = endDateUtc.UtcDateTime;
            return programs.Where(p => p.EndUtc > from && p.StartUtc < to).Select(ToProgramInfo).ToList();
        }

        internal static ProgramInfo ToProgramInfo(XmlTvProgram p)
        {
            var info = new ProgramInfo
            {
                Name = p.Title ?? "Unknown",
                EpisodeTitle = p.SubTitle,
                Overview = p.Description,
                StartDate = new DateTimeOffset(DateTime.SpecifyKind(p.StartUtc, DateTimeKind.Utc)),
                EndDate = new DateTimeOffset(DateTime.SpecifyKind(p.EndUtc, DateTimeKind.Utc)),
                Genres = p.Categories.ToList(),
                ImageUrl = p.IconUrl,
                IsSeries = p.HasEpisodeNumber,
                ProductionYear = p.Year,
                SeasonNumber = p.SeasonNumber,
                EpisodeNumber = p.EpisodeNumber
            };

            foreach (var c in p.Categories)
            {
                if (Has(c, "movie") || Has(c, "film")) info.IsMovie = true;
                if (Has(c, "news")) info.IsNews = true;
                if (Has(c, "sport")) info.IsSports = true;
                if (Has(c, "kid") || Has(c, "child") || Has(c, "cartoon") || Has(c, "animat")) info.IsKids = true;
            }
            return info;
        }

        private static bool Has(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- channel index (epg id lookup) ---------------------------------------

        private void InvalidateIndex(TunerHostInfo tuner)
        {
            if (string.IsNullOrEmpty(tuner.Id)) return;
            lock (_indexGate) _indexes.Remove(tuner.Id);
        }

        private async Task<ChannelIndex> GetIndexAsync(TunerHostInfo tuner, XtreamTunerOptions options, CancellationToken ct)
        {
            string optionsKey = tuner.ProviderOptions ?? string.Empty;
            lock (_indexGate)
            {
                ChannelIndex existing;
                if (_indexes.TryGetValue(tuner.Id, out existing) && existing.OptionsKey == optionsKey) return existing;
            }

            var channels = await GetChannels(tuner, ct).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in channels)
            {
                string streamId = GetTunerChannelIdFromEmbyChannelId(tuner, c.Id);
                if (string.IsNullOrEmpty(c.TunerChannelId)) continue;
                map[streamId] = c.TunerChannelId;
                ids.Add(c.TunerChannelId);
            }

            var index = new ChannelIndex
            {
                Count = channels.Count,
                OptionsKey = optionsKey,
                EpgIdByStreamId = map,
                EpgIds = ids,
                Signature = channels.Count.ToString(CultureInfo.InvariantCulture) + ":" + ids.Count.ToString(CultureInfo.InvariantCulture)
            };
            lock (_indexGate) _indexes[tuner.Id] = index;
            return index;
        }
    }
}
