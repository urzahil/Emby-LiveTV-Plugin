# Emby Xtream IPTV plugin — architecture

This plugin adds an **Xtream Codes** IPTV provider to Emby Server as a native
Live TV tuner (it appears under *Live TV → TV Sources → Add* like HDHomeRun or
M3U), supplies guide data for those channels, and exposes the provider's
**catch-up / archive** recordings as a browsable Emby *Channel*.

Target: Emby Server **4.8.x** (SDK NuGet `mediabrowser.server.core` 4.8.11,
`netstandard2.0`). Everything below was verified against the 4.8.11 assemblies.

---

## 1. Why these extension points

| Need | Emby extension point | Notes |
|---|---|---|
| Show up on the Live TV page as a TV source | `MediaBrowser.Controller.LiveTv.BaseTunerHost` (implements `ITunerHost`) | Emby discovers every `ITunerHost` in plugin assemblies. `GET /LiveTv/TunerHosts/Types` lists them and the web UI's *Add TV Source* sheet navigates to `SetupUrl`. |
| Guide data (EPG) for those channels without a separate listings provider | `BaseTunerHost.SupportsGuideData` + `GetProgramsInternal` | Same mechanism Emby's own M3U tuner uses for `url-tvg`. |
| Catch-up / archive playback | `MediaBrowser.Controller.Channels.IChannel` + `IRequiresMediaInfoCallback` | Emby's Live TV API has **no catch-up concept at all** (verified: no catchup/timeshift anywhere in the 4.8 SDK or the bundled M3U tuner). Past guide entries are not playable. The only first-class way to offer "play this programme from yesterday" is a Channel plugin: a virtual folder tree whose leaf items are playable URLs. |
| Setup UI for credentials | `IHasWebPages` embedded HTML/JS pages | The generic `livetvtuner.html` hides every field for unknown tuner types, so a custom page is **required**. |
| Helper REST endpoints used by the setup page | `MediaBrowser.Model.Services.IService` | `/Xtream/Test`, `/Xtream/Categories`. |

### Key facts about `BaseTunerHost` (decompiled from 4.8.11)

```csharp
protected BaseTunerHost(IServerApplicationHost applicationHost); // resolves Config, Logger, JsonSerializer, FileSystem, LiveTvManager
public abstract string Name { get; }      // display name in the "Add TV Source" list
public abstract string Type { get; }      // stable id, lower-case, e.g. "xtream"
public virtual string SetupUrl { get; }   // default "livetvsetup/livetvtuner.html?type=..." -> we override
protected virtual bool UseTunerHostIdAsPrefix => true; // channel ids become "{tuner.Id}_{ourId}"
protected abstract Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken ct);
protected abstract Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, BaseItem dbChannel, ChannelInfo tunerChannel, CancellationToken ct);
protected virtual Task<List<ProgramInfo>> GetProgramsInternal(TunerHostInfo tuner, string tunerChannelId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
public virtual bool SupportsGuideData(TunerHostInfo tuner) => false;   // override -> true when option enabled
public virtual Task ValdidateOptions(TunerHostInfo tuner, CancellationToken ct); // called on save; default calls GetChannelsInternal
public virtual TunerHostInfo GetDefaultConfiguration();
protected string CreateEmbyChannelId(TunerHostInfo tuner, string tunerChannelId);
protected string GetTunerChannelIdFromEmbyChannelId(TunerHostInfo tuner, string channelId);
protected T GetProviderOptions<T>(TunerHostInfo info) where T : new();  // JSON in TunerHostInfo.ProviderOptions
protected void SetCustomOptions<T>(TunerHostInfo info, T options);
```

* `RefreshChannels` (called by Emby's guide refresh) → `GetChannelsInternal`, result is cached by the base class to `data/livetv/tuner_{id}_channels`.
* `GetChannelStream` default: takes the first `MediaSourceInfo` from `GetChannelStreamMediaSources` and calls `LiveTvManager.CreateLiveStream(...)`. We do **not** override it.
* `GetProgramsAsync` strips the prefix and calls `GetProgramsInternal` with *our* channel id (the Xtream `stream_id`), then sets `ProgramInfo.ChannelId`/`Id` itself. We must not set those.
* `TunerHostInfo` fields we use: `Id`, `Url` (server base URL), `FriendlyName`, `TunerCount`, `UserAgent`, `ProviderOptions` (JSON string), `SetupUrl` (the UI appends `&id=` to it when editing an existing tuner — set it in `GetDefaultConfiguration` and keep it on save), `ImportGuideData`.

### Web UI contract (from `dashboard-ui` 4.8.11)

* Add: UI reads `LiveTv/TunerHosts/Types` → `{Id, Name, SetupUrl}` and navigates to `SetupUrl`.
* Edit: UI navigates to `tunerHostInfo.SetupUrl + "&id={Id}"` (or `?id=` if no `?`).
* Plugin pages are served at `configurationpage?name={PluginPageInfo.Name}`; use `BasePlugin.GetPluginPageUrl("xtreamtuner")` for `SetupUrl`.
* In the page: `ApiClient.getTunerHostConfiguration(id)`, `ApiClient.getDefaultTunerHostConfiguration('xtream')`, `ApiClient.saveTunerHostConfiguration(info)` (POST `/LiveTv/TunerHosts`), then `appRouter.show(appRouter.getRouteUrl('LiveTVSetup', {SavedTunerHostId, IsNew}))`.
* HTML page root: `<div is="emby-scroller" class="view ..." data-controller="__plugin/xtreamtunerjs" data-title="...">`; the JS is an AMD module: `define(['baseView','globalize','loading','appRouter','formHelper','emby-input','emby-button','emby-checkbox','emby-select'], function (BaseView, ...) { ... return View; })`.
* Strings: `IHasTranslations` with embedded `Emby.Xtream.strings.en-US.json`; `${Key}` placeholders in HTML are translated by Emby.

A decompiled copy of Emby's bundled M3U tuner plugin (the closest reference)
and its setup page is available during development at
`/tmp/claude-0/-home-user/fc78b71b-fb1e-53f7-b8aa-5c97a2139965/scratchpad/decomp/m3u/`
(do not copy it into this repository — it is Emby's proprietary code; use it only
to confirm API usage).

---

## 2. Xtream Codes API (what we consume)

Base URL `http(s)://host[:port]`, credentials `U`/`P`.

| Purpose | Request | Notes |
|---|---|---|
| Auth + server info | `GET /player_api.php?username=U&password=P` | `user_info{auth(0/1), status, exp_date, max_connections, allowed_output_formats[]}`, `server_info{url, port, https_port, server_protocol, timezone, timestamp_now, time_now}` |
| Live categories | `...&action=get_live_categories` | `[{category_id, category_name, parent_id}]` |
| Live channels | `...&action=get_live_streams[&category_id=X]` | `[{num, name, stream_type, stream_id, stream_icon, epg_channel_id, added, category_id, custom_sid, tv_archive, direct_source, tv_archive_duration}]` |
| Short EPG | `...&action=get_short_epg&stream_id=X&limit=N` | `{epg_listings:[...]}` |
| Full/archive EPG for one channel | `...&action=get_simple_data_table&stream_id=X` | `{epg_listings:[{id, epg_id, title(base64), lang, start "YYYY-MM-DD HH:MM:SS", end, description(base64), channel_id, start_timestamp, stop_timestamp, now_playing, has_archive}]}` |
| XMLTV guide (all channels) | `GET /xmltv.php?username=U&password=P` | Standard XMLTV; `<channel id>` matches `epg_channel_id` |
| Live stream | `/live/U/P/{stream_id}.{ts|m3u8}` (also legacy `/U/P/{stream_id}`) | Extension selectable: `ts` (default) or `m3u8` |
| Catch-up stream | `/streaming/timeshift.php?username=U&password=P&stream={stream_id}&start=YYYY-MM-DD:HH-MM&duration={minutes}` ; alternative path style `/timeshift/U/P/{minutes}/{YYYY-MM-DD:HH-MM}/{stream_id}.ts` | **`start` is in the provider's server timezone** (`server_info.timezone`, IANA name), not UTC. `start_timestamp`/`stop_timestamp` in EPG are Unix UTC. |

Gotchas the client must tolerate:
* Numbers arrive as numbers *or* strings (`"stream_id": "123"` vs `123`); booleans as `0/1`, `"0"/"1"`, `true/false`.
* `epg_listings` may be missing, `null`, or an object instead of an array; `title`/`description` are base64 (sometimes already plain text — fall back if base64 decoding fails or yields invalid UTF-8).
* Providers reject unknown User-Agents; default to a VLC-like UA (same approach as Emby's M3U tuner: `VLC/3.0.21 LibVLC/3.0.21`), overridable per tuner.
* Some providers block `player_api.php` when credentials are given as the `get.php` M3U URL — accept a pasted M3U URL (`.../get.php?username=U&password=P&...`) in the setup page and split it into base URL + credentials client-side.

---

## 3. Solution layout

```
Emby.Xtream.sln
src/Emby.Xtream/
  Emby.Xtream.csproj              netstandard2.0; PackageReference mediabrowser.server.core 4.8.11 (PrivateAssets=all, ExcludeAssets=runtime)
  Plugin.cs                       BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IHasTranslations
  Configuration/PluginConfiguration.cs
  Xtream/                         pure, Emby-independent, unit-testable
    XtreamCredentials.cs          BaseUrl, Username, Password (+ parse from M3U/get.php URL)
    XtreamUrlBuilder.cs           player_api, xmltv, live, timeshift URLs
    XtreamModels.cs               ServerInfo, UserInfo, Category, LiveStream, EpgEntry
    XtreamClient.cs               calls API via an injected IXtreamHttp (GET url -> Stream) ; parses with JsonLite
    Json/JsonLite.cs              small tolerant JSON parser (object/array/string/number/bool/null) + helpers GetString/GetInt/GetBool that accept mixed types
    XmlTv/XmlTvParser.cs          streaming XmlReader parser, filtered to a set of channel ids + time window
  LiveTv/
    XtreamTunerHost.cs            : BaseTunerHost, IHasProgramCaches
    XtreamTunerOptions.cs         ProviderOptions JSON: Username, Password, StreamFormat ("ts"|"m3u8"), Categories (string[] ids, empty = all), ImportGuideData, GuideSource ("xmltv"|"api"), EnableCatchup, CatchupMaxDays, CatchupUrlStyle ("query"|"path")
    XtreamChannelInfo.cs          : ChannelInfo + StreamId, EpgChannelId, CategoryId, CategoryName, TvArchive, TvArchiveDays
    GuideCache.cs                 per-tuner cached XMLTV (disk under Config.ApplicationPaths.CachePath/xtream/, TTL = PluginConfiguration.EpgCacheHours)
    EmbyHttp.cs                   IXtreamHttp over MediaBrowser.Common.Net.IHttpClient
  Catchup/
    XtreamCatchupChannel.cs       : IChannel, IRequiresMediaInfoCallback, IHasCacheKey, IHasChannelFeatures
    CatchupItemId.cs              encode/decode folder & item ids
  Api/
    XtreamService.cs              : IService  — GET /Xtream/Test, GET /Xtream/Categories (Admin only)
  Web/
    xtreamtuner.html / xtreamtuner.js      tuner setup page (add + edit)
    config.html / config.js                plugin main config page (global defaults, help)
  strings.en-US.json
  thumb.png
tests/Emby.Xtream.Tests/            net8.0, xunit; references src project; fixtures under TestData/
tools/mock-xtream/mock_xtream.py    stdlib-only Python mock of player_api.php / xmltv.php / live / timeshift for manual testing against a real Emby
.github/workflows/build.yml         dotnet build + test, uploads Emby.Xtream.dll artifact
README.md                           install (copy DLL to plugins/, restart), setup walkthrough, catch-up explanation, limitations
```

Embedded resources must use `LogicalName` = `Emby.Xtream.Web.xtreamtuner.html` etc. (the
`EmbeddedResourcePath` string Emby loads). Keep **no runtime NuGet dependencies**
beyond the Emby SDK — the plugin is distributed as a single `Emby.Xtream.dll`.

---

## 4. Tuner host behaviour

* `Type = "xtream"`, `Name = "Xtream Codes (IPTV)"`, `SetupUrl = BasePlugin.GetPluginPageUrl("xtreamtuner")`.
* `GetDefaultConfiguration()`: `TunerCount = 1` (Xtream `max_connections` is normally 1), `AllowMappingByNumber = false`, `SetupUrl` set.
* `ValdidateOptions`: call `player_api.php`; throw with a clear message when `user_info.auth != 1` or status != "Active" (the UI shows "Error saving TV provider" — also log the reason).
* `GetChannelsInternal`:
  1. `get_live_categories`, `get_live_streams` (one call, no category filter) — filter by `Categories` option if non-empty.
  2. Map to `XtreamChannelInfo`: `Id = CreateEmbyChannelId(tuner, stream_id)`, `Name`, `Number = num` (as string), `ImageUrl = stream_icon`, `ChannelType = TV`, `Path = live URL`, `TunerChannelId = epg_channel_id` (lets users map an external XMLTV provider), `Tags = [category_name]` when present, plus archive fields.
  3. `OnSaved(isNew)`: set `tuner.DataVersion = 1`.
* `GetChannelStreamMediaSources`: one `MediaSourceInfo`, mirroring Emby's M3U tuner:
  `Path = live URL`, `Protocol = Http`, `Id = md5(url)`, `IsInfiniteStream = true`, `IsRemote = true`, `RequiresOpening = true`, `RequiresClosing = true`, `SupportsDirectPlay = false`, `SupportsDirectStream = true`, `SupportsTranscoding = true`, `SupportsProbing = true`, placeholder `MediaStreams` (Video index -1 IsInterlaced true, Audio index -1), `RequiredHttpHeaders = {User-Agent, Referer}`; then `InferTotalBitrate()`. Container: `"ts"` when StreamFormat is ts, leave null for m3u8 (Emby's HLS handling probes it).
* Guide: `SupportsGuideData` = `options.ImportGuideData`. `GetProgramsInternal(tuner, streamId, start, end)`:
  * `GuideSource = "xmltv"` (default): `GuideCache.Get(tuner)` → download `xmltv.php` once per TTL (stream to disk with `IHttpClient.GetTempFile`/`Get`, not into memory), parse with `XmlTvParser` into `Dictionary<epgChannelId, List<ProgramInfo>>` filtered to the tuner's imported `epg_channel_id`s and window `[now-1d, now+GuideDays+1d]`; serve the requested channel/window from memory. Serialize concurrent refreshes with a `SemaphoreSlim`. `ClearProgramCaches()` (`IHasProgramCaches`) drops memory + disk cache.
  * `GuideSource = "api"`: `get_simple_data_table&stream_id=` per channel, cached per channel for the TTL, max 4 concurrent requests.
  * `ProgramInfo` mapping: `Name` (title), `EpisodeTitle` (sub-title), `Overview` (desc), `StartDate/EndDate` (UTC), `Genres` (category elements), `ImageUrl` (icon), `IsSeries` when `episode-num` present, `IsMovie` when category contains "movie"/"film", `IsNews`/`IsSports`/`IsKids` from category keywords, `ProductionYear` from `date` when 4 digits. Do **not** set `Id`/`ChannelId`.

---

## 5. Catch-up channel

`XtreamCatchupChannel` — `Name = "IPTV Catch-up"`, `Description`, `ParentalRating = GeneralAudience`, `GetSupportedChannelImages = [Primary, Thumb]` (serve `thumb.png`).
Visible only when `PluginConfiguration.EnableCatchupChannel` (default true) and at least one xtream tuner has `EnableCatchup`; otherwise `GetChannelItems` returns an empty result. Tuner hosts are read via `ILiveTvManager.GetTunerHostInfos("xtream")`.

Folder tree (`InternalChannelItemQuery.FolderId`, ids built by `CatchupItemId`):

```
root                     -> one folder per tuner  (skipped when exactly one tuner: show its categories directly)
t|{tunerId}              -> categories that contain at least one channel with tv_archive = 1
t|{tunerId}|c|{catId}    -> channels with tv_archive = 1 (name, logo, "N days" in overview)
...|s|{streamId}         -> day folders: today, yesterday, ... back to min(tv_archive_duration, CatchupMaxDays)
...|d|{yyyyMMdd}         -> programmes from get_simple_data_table for that local day where has_archive=1 (or stop < now when provider never sets it) and stop <= now
leaf  ...|p|{startTs}|{stopTs}|{urlsafe-title}  -> ChannelItemInfo{Type=Media, MediaType=Video, ContentType=Clip, Name="HH:mm Title", Overview=description, StartDate, RunTimeTicks, ImageUrl=channel logo, IsLiveStream=false}
```

* Items carry **no** `MediaSources`; `IRequiresMediaInfoCallback.GetChannelItemMediaInfo(id)` builds the timeshift URL at play time (duration = stop-start in minutes, start converted from UTC to `server_info.timezone` via `TimeZoneInfo.FindSystemTimeZoneById` with try/catch → fallback to UTC offset derived from `server_info.time_now` vs `timestamp_now`). `MediaSourceInfo`: `Protocol = Http`, `Container = "ts"`, `IsInfiniteStream = false`, `RunTimeTicks`, `SupportsDirectStream = true`, `SupportsTranscoding = true`, `SupportsProbing = true`, `RequiredHttpHeaders` (UA), `IsRemote = true`.
* `IHasCacheKey.GetCacheKey(userId)` returns a value that changes hourly so Emby refreshes day listings but doesn't hammer the provider; EPG tables are additionally cached in memory for 30 minutes per stream.
* `IHasChannelFeatures.Features`: `MediaTypes=[Video]`, `ContentTypes=[Clip]`, `MaxPageSize=200`, `DefaultSortFields=[Name]`, `AutoRefreshLevels=2`.
* Credentials never appear in item ids; ids only hold tuner id / stream id / timestamps.

---

## 6. Setup page (`xtreamtuner.html/js`)

Fields: Friendly name · Server URL (accepts a full `get.php` M3U URL and splits it) · Username · Password (type=password) · Stream format (TS/HLS) · *Test connection* button → `GET /Xtream/Test?Url&Username&Password` shows status/expiry/max connections and loads categories into a multi-select (`GET /Xtream/Categories?...`) · "Import guide data from provider" checkbox + guide source select · "Enable catch-up" checkbox + max days · Simultaneous connection limit · User-Agent.
Save: start from `getTunerHostConfiguration(id)` / `getDefaultTunerHostConfiguration('xtream')`, fill `Url`, `FriendlyName`, `TunerCount`, `UserAgent`, `ImportGuideData`, set `ProviderOptions = JSON.stringify(options)`, `saveTunerHostConfiguration`.

Main config page (`config.html`): global defaults (`EpgCacheHours`=6, `CatchupMaxDays`=7, `EnableCatchupChannel`=true, `HttpTimeoutSeconds`=60) and a short "how to add a source" explanation. Use the standard `ApiClient.getPluginConfiguration(pluginId)` / `updatePluginConfiguration`.

**Security note:** like Emby's own M3U tuner, credentials are stored in Emby's `livetv.xml` configuration (admin-only). Never log passwords — sanitise URLs in log lines (strip `password=` / path segments) via a `Sanitize(url)` helper used by every log call.

---

## 7. Testing

* Unit tests (xunit, net8.0): `JsonLite` (mixed types, nesting, escapes, invalid input), `XtreamClient` parsing against fixtures in `TestData/` (player_api, categories, streams with mixed types, simple_data_table with base64), `XtreamUrlBuilder` (live/timeshift/both styles, URL encoding, trailing slashes, ports), `XtreamCredentials.ParseM3uUrl`, `XmlTvParser` (filtering, timezones `+0100`, missing fields), `CatchupItemId` round-trips, timezone conversion.
* The Emby-facing classes (`XtreamTunerHost`, `XtreamCatchupChannel`) stay thin and delegate to the pure layer so most logic is covered without an Emby host.
* `tools/mock-xtream/mock_xtream.py` serves a fake provider (channels, categories, EPG, a looping sample TS) for manual verification against a real Emby install: copy `Emby.Xtream.dll` to `<emby data>/plugins/`, restart, add the source with `http://localhost:8080`, user/pass `test`/`test`.
