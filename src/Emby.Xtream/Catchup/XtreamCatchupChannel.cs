using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.LiveTv;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Emby has no catch-up concept in its Live TV API, so provider archives are exposed as a browsable Channel:
    /// tuner > category > channel > day > programme. Leaf items carry no media sources; the timeshift URL (which contains
    /// credentials) is built at play time by <see cref="GetChannelItemMediaInfo"/>.
    /// </summary>
    public class XtreamCatchupChannel : IChannel, IRequiresMediaInfoCallback
    {
        private sealed class Catalog
        {
            public DateTime LoadedUtc;
            public List<XtreamCategory> Categories;
            public List<XtreamLiveStream> Streams;
            public XtreamClock Clock;
        }

        private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan EpgTtl = TimeSpan.FromMinutes(30);

        private readonly ILiveTvManager _liveTv;
        private readonly IHttpClient _http;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly EpgTableCache _epg = new EpgTableCache(4);
        private readonly Dictionary<string, Catalog> _catalogs = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _catalogGate = new SemaphoreSlim(1, 1);

        public XtreamCatchupChannel(ILiveTvManager liveTvManager, IHttpClient httpClient, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _liveTv = liveTvManager;
            _http = httpClient;
            _json = jsonSerializer;
            _logger = logManager.GetLogger("XtreamCatchup");
        }

        public string Name => "IPTV Catch-up";

        public string Description => "Watch past programmes from your Xtream Codes provider's TV archive.";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Emby.Xtream.thumb.png");
            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Png,
                Stream = stream
            });
        }

        // ---- browsing -------------------------------------------------------------

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var empty = new ChannelItemResult { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };

            if (!XtreamTunerSupport.PluginConfig.EnableCatchupChannel) return empty;

            var tuners = GetCatchupTuners();
            if (tuners.Count == 0) return empty;

            var id = CatchupItemId.Parse(query.FolderId);
            if (id == null) return empty;

            List<ChannelItemInfo> items;
            try
            {
                items = await BuildItems(id, tuners, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Error("Catch-up listing failed: {0}", XtreamUrlBuilder.SanitizeText(ex.Message));
                return empty;
            }

            int total = items.Count;
            int start = Math.Max(0, query.StartIndex ?? 0);
            IEnumerable<ChannelItemInfo> page = items.Skip(start);
            if (query.Limit.HasValue) page = page.Take(query.Limit.Value);
            return new ChannelItemResult { Items = page.ToList(), TotalRecordCount = total };
        }

        private async Task<List<ChannelItemInfo>> BuildItems(CatchupItemId id, List<TunerHostInfo> tuners, CancellationToken ct)
        {
            if (id.Kind == CatchupNodeKind.Root)
            {
                if (tuners.Count == 1) return await BuildCategories(tuners[0], ct).ConfigureAwait(false);
                return tuners.Select(t => Folder(CatchupItemId.ForTuner(t.Id).ToString(), t.FriendlyName ?? t.Url, null)).ToList();
            }

            var tuner = tuners.FirstOrDefault(t => string.Equals(t.Id, id.TunerId, StringComparison.OrdinalIgnoreCase));
            if (tuner == null) return new List<ChannelItemInfo>();

            switch (id.Kind)
            {
                case CatchupNodeKind.Tuner:
                    return await BuildCategories(tuner, ct).ConfigureAwait(false);
                case CatchupNodeKind.Category:
                    return await BuildChannels(tuner, id, ct).ConfigureAwait(false);
                case CatchupNodeKind.Channel:
                    return await BuildDays(tuner, id, ct).ConfigureAwait(false);
                case CatchupNodeKind.Day:
                    return await BuildProgrammes(tuner, id, ct).ConfigureAwait(false);
                default:
                    return new List<ChannelItemInfo>();
            }
        }

        private async Task<List<ChannelItemInfo>> BuildCategories(TunerHostInfo tuner, CancellationToken ct)
        {
            var catalog = await GetCatalog(tuner, ct).ConfigureAwait(false);
            var options = XtreamTunerSupport.GetOptions(_json, tuner);
            var archived = ArchiveStreams(catalog, options);

            var names = catalog.Categories.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
            return archived
                .GroupBy(s => s.CategoryId ?? string.Empty)
                .Select(g => new
                {
                    Id = g.Key,
                    Name = g.Key.Length > 0 && names.ContainsKey(g.Key) ? names[g.Key] : "Other",
                    Count = g.Count()
                })
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => Folder(CatchupItemId.ForCategory(tuner.Id, c.Id).ToString(), c.Name, c.Count.ToString(CultureInfo.InvariantCulture) + " channels with catch-up"))
                .ToList();
        }

        private async Task<List<ChannelItemInfo>> BuildChannels(TunerHostInfo tuner, CatchupItemId id, CancellationToken ct)
        {
            var catalog = await GetCatalog(tuner, ct).ConfigureAwait(false);
            var options = XtreamTunerSupport.GetOptions(_json, tuner);
            int maxDays = MaxDays(options);

            return ArchiveStreams(catalog, options)
                .Where(s => string.Equals(s.CategoryId ?? string.Empty, id.CategoryId ?? string.Empty, StringComparison.Ordinal))
                .OrderBy(s => s.Num).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s =>
                {
                    int days = CatchupPlanner.EffectiveDays(s.TvArchiveDuration, maxDays);
                    var item = Folder(CatchupItemId.ForChannel(tuner.Id, id.CategoryId, s.StreamId).ToString(), s.Name,
                        days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days") + " of catch-up");
                    item.ImageUrl = s.StreamIcon;
                    return item;
                }).ToList();
        }

        private async Task<List<ChannelItemInfo>> BuildDays(TunerHostInfo tuner, CatchupItemId id, CancellationToken ct)
        {
            var catalog = await GetCatalog(tuner, ct).ConfigureAwait(false);
            var stream = catalog.Streams.FirstOrDefault(s => s.StreamId == id.StreamId);
            if (stream == null) return new List<ChannelItemInfo>();

            var options = XtreamTunerSupport.GetOptions(_json, tuner);
            int days = CatchupPlanner.EffectiveDays(stream.TvArchiveDuration, MaxDays(options));
            DateTime utcNow = DateTime.UtcNow;
            DateTime today = catalog.Clock.ToServer(utcNow).Date;

            return CatchupPlanner.DayFolders(catalog.Clock, utcNow, days)
                .Select(d =>
                {
                    var item = Folder(CatchupItemId.ForDay(tuner.Id, id.CategoryId, id.StreamId, CatchupPlanner.DayId(d)).ToString(),
                        CatchupPlanner.DayLabel(d, today), null);
                    item.ImageUrl = stream.StreamIcon;
                    return item;
                }).ToList();
        }

        private async Task<List<ChannelItemInfo>> BuildProgrammes(TunerHostInfo tuner, CatchupItemId id, CancellationToken ct)
        {
            DateTime day;
            if (!CatchupPlanner.TryParseDayId(id.Day, out day)) return new List<ChannelItemInfo>();

            var catalog = await GetCatalog(tuner, ct).ConfigureAwait(false);
            var stream = catalog.Streams.FirstOrDefault(s => s.StreamId == id.StreamId);
            if (stream == null) return new List<ChannelItemInfo>();

            var options = XtreamTunerSupport.GetOptions(_json, tuner);
            int days = CatchupPlanner.EffectiveDays(stream.TvArchiveDuration, MaxDays(options));
            var client = XtreamTunerSupport.CreateClient(_http, tuner, options);

            var entries = await _epg.GetAsync(tuner.Id + "|" + id.StreamId, EpgTtl,
                () => client.GetSimpleDataTableAsync(id.StreamId, null, ct), ct).ConfigureAwait(false);

            var programmes = CatchupPlanner.ArchivedProgrammes(entries, catalog.Clock, day, DateTime.UtcNow, days);
            var result = new List<ChannelItemInfo>(programmes.Count);
            foreach (var p in programmes)
            {
                long startTs = XtreamClock.ToUnix(p.StartUtc), stopTs = XtreamClock.ToUnix(p.StopUtc);
                string title = string.IsNullOrWhiteSpace(p.Title) ? "Programme" : p.Title.Trim();
                string clockTime = catalog.Clock.ToServer(p.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
                result.Add(new ChannelItemInfo
                {
                    Id = CatchupItemId.ForProgramme(tuner.Id, id.CategoryId, id.StreamId, id.Day, startTs, stopTs, title).ToString(),
                    Name = clockTime + " " + title,
                    Overview = p.Description,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Clip,
                    PremiereDate = new DateTimeOffset(DateTime.SpecifyKind(p.StartUtc, DateTimeKind.Utc)),
                    RunTimeTicks = (p.StopUtc - p.StartUtc).Ticks,
                    ImageUrl = stream.StreamIcon,
                    IsLiveStream = false
                });
            }
            return result;
        }

        // ---- playback -------------------------------------------------------------

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var item = CatchupItemId.Parse(id);
            if (item == null || item.Kind != CatchupNodeKind.Programme) return new List<MediaSourceInfo>();

            var tuner = GetCatchupTuners().FirstOrDefault(t => string.Equals(t.Id, item.TunerId, StringComparison.OrdinalIgnoreCase));
            if (tuner == null) return new List<MediaSourceInfo>();

            var options = XtreamTunerSupport.GetOptions(_json, tuner);
            var catalog = await GetCatalog(tuner, cancellationToken).ConfigureAwait(false);

            DateTime startUtc = XtreamClock.FromUnix(item.StartTs);
            int minutes = CatchupPlanner.DurationMinutes(item.StartTs, item.StopTs);
            var urls = new XtreamUrlBuilder(XtreamTunerSupport.GetCredentials(tuner, options));
            string url = urls.Timeshift(item.StreamId, catalog.Clock.ToServer(startUtc), minutes, options.CatchupUrlStyle);

            _logger.Info("Catch-up playback requested: {0}", XtreamUrlBuilder.Sanitize(url));

            var source = new MediaSourceInfo
            {
                Path = url,
                Protocol = MediaProtocol.Http,
                Id = url.GetMD5().ToString("N"),
                Container = "ts",
                IsInfiniteStream = false,
                IsRemote = true,
                RunTimeTicks = TimeSpan.FromSeconds(Math.Max(1, item.StopTs - item.StartTs)).Ticks,
                SupportsDirectStream = true,
                SupportsDirectPlay = false,
                SupportsTranscoding = true,
                RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = XtreamTunerSupport.GetUserAgent(tuner)
                },
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Index = -1 },
                    new MediaStream { Type = MediaStreamType.Audio, Index = -1 }
                }
            };
            return new List<MediaSourceInfo> { source };
        }

        // ---- helpers --------------------------------------------------------------

        private List<TunerHostInfo> GetCatchupTuners()
        {
            var all = _liveTv.GetTunerHostInfos(XtreamDefaults.TunerType) ?? new List<TunerHostInfo>();
            return all.Where(t => XtreamTunerSupport.GetOptions(_json, t).EnableCatchup).ToList();
        }

        private static int MaxDays(XtreamTunerOptions options) =>
            options.CatchupMaxDays > 0 ? options.CatchupMaxDays : XtreamTunerSupport.PluginConfig.CatchupMaxDays;

        private static List<XtreamLiveStream> ArchiveStreams(Catalog catalog, XtreamTunerOptions options)
        {
            HashSet<string> allowed = options.Categories != null && options.Categories.Length > 0
                ? new HashSet<string>(options.Categories, StringComparer.Ordinal) : null;
            return catalog.Streams.Where(s => s.TvArchive && (allowed == null || (s.CategoryId != null && allowed.Contains(s.CategoryId)))).ToList();
        }

        private async Task<Catalog> GetCatalog(TunerHostInfo tuner, CancellationToken ct)
        {
            await _catalogGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Catalog cached;
                if (_catalogs.TryGetValue(tuner.Id, out cached) && DateTime.UtcNow - cached.LoadedUtc < CatalogTtl) return cached;

                var options = XtreamTunerSupport.GetOptions(_json, tuner);
                var client = XtreamTunerSupport.CreateClient(_http, tuner, options);

                var account = await client.GetAccountAsync(ct).ConfigureAwait(false);
                var categories = await client.GetLiveCategoriesAsync(ct).ConfigureAwait(false);
                var streams = await client.GetLiveStreamsAsync(ct).ConfigureAwait(false);

                var catalog = new Catalog
                {
                    LoadedUtc = DateTime.UtcNow,
                    Categories = categories,
                    Streams = streams,
                    Clock = XtreamClock.FromServerInfo(account.ServerInfo)
                };
                _catalogs[tuner.Id] = catalog;
                return catalog;
            }
            finally
            {
                _catalogGate.Release();
            }
        }

        private static ChannelItemInfo Folder(string id, string name, string overview) => new ChannelItemInfo
        {
            Id = id,
            Name = name,
            Overview = overview,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container
        };
    }
}
