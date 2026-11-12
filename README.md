# Emby Xtream IPTV plugin

An [Emby Server](https://emby.media) plugin that adds an **Xtream Codes** IPTV provider as a native Live TV
source. It appears under *Live TV > TV Sources > Add* next to HDHomeRun and M3U, supplies programme guide data for
its channels, and exposes the provider's **catch-up / TV archive** as a browsable Emby channel.

* Live channels from `player_api.php` (optionally limited to chosen categories), with logos, numbers and category tags
* Guide data straight from the provider (`xmltv.php`, or per-channel API calls) - no separate guide provider needed
* Catch-up: browse *channel > day > programme* and play past programmes through the provider's timeshift URLs
* Single DLL, no extra dependencies

## Requirements

* Emby Server **4.8** or newer (built against the 4.8.11 SDK)
* An Xtream Codes account: server URL, username and password

## Install

1. Download `Emby.Xtream.dll` (from the CI artifact of this repository, or [build it](#building-from-source)).
2. Copy it into the `plugins` folder of your Emby data directory (for example `/var/lib/emby/plugins/` on Linux,
   `C:\Users\<you>\AppData\Roaming\Emby-Server\programdata\plugins\` on Windows).
3. Restart Emby Server. *Xtream IPTV* now shows up under *Settings > Plugins*.

## Add a TV source

1. Open *Settings > Live TV* and choose **Add** under *TV Sources*, then pick **Xtream Codes (IPTV)**.
2. Enter the **Server URL** (`http://provider.example:8080`), **Username** and **Password**.
   You can also paste the full M3U link your provider gave you (`.../get.php?username=...&password=...`);
   it is split into URL, username and password for you.
3. Press **Test connection**. You see the account status, expiry date and allowed connections, and the provider's
   categories are loaded into the list below.
4. Optionally tick the categories to import (none ticked = everything).
5. Choose the stream format (MPEG-TS is the most compatible), the simultaneous connection limit
   (most accounts allow 1 or 2) and, if your provider is picky, a custom User-Agent
   (default `VLC/3.0.21 LibVLC/3.0.21`).
6. Save. Emby validates the account and imports the channels.

Credentials are stored in Emby's Live TV configuration (admin only), like Emby's own M3U tuner. The plugin never writes
passwords to the log; URLs are sanitised before logging.

## Guide data

Tick **Import guide data from the provider** (on by default). Two sources are available:

* **XMLTV file** (recommended): `xmltv.php` is downloaded once per cache period (default 6 hours), streamed to disk
  and parsed only for the channels you imported.
* **Provider API**: `get_simple_data_table` per channel, cached per channel; slower for large channel lists.

The provider's `epg_channel_id` is also exposed as the tuner channel id, so you can map an external guide provider
(for example your own XMLTV) onto these channels in Emby if the provider's guide is poor. Global options such as the
cache duration and the number of guide days are on the plugin's settings page.

## Catch-up

Emby's Live TV API has no catch-up concept, so past programmes cannot be played from the guide grid. Instead the
plugin adds a channel named **IPTV Catch-up** (find it under *Channels*; it is a normal Emby channel):

```
IPTV Catch-up > [source] > category > channel > day > programme
```

Only channels the provider flags with a TV archive are listed, going back as many days as the provider keeps
(capped by the *Maximum catch-up days* setting, 7 by default). Choosing a programme plays it through the provider's
timeshift URL. The start time is converted to the provider's server timezone, as these URLs require.

Enable or disable catch-up per source on the source's setup page, and the whole channel on the plugin settings page.
If your provider does not answer the default `streaming/timeshift.php` URL, try the alternative
`timeshift/user/pass/...` style on the source page.

## Limitations

* **No catch-up from the guide grid.** Emby has no API to make a past guide entry playable; use the *IPTV Catch-up*
  channel instead.
* Only live TV is supported (no VOD or series sections of the Xtream API).
* Catch-up depends on the provider: archive length, programme flags and timeshift URL style vary. Programmes are
  offered when they have finished and are flagged `has_archive` (or, if the provider never sets the flag, simply when
  they have finished).
* Live and catch-up streams count against the provider's connection limit.
* The setup page's *Test connection* sends the credentials to your own Emby server as query parameters of an
  admin-only request, so they may appear in Emby's HTTP request log.

## Building from source

Requires the .NET 8 SDK.

```
dotnet build Emby.Xtream.sln -c Release
dotnet test Emby.Xtream.sln -c Release
```

The plugin is `src/Emby.Xtream/bin/Release/netstandard2.0/Emby.Xtream.dll`. The Emby SDK is referenced from NuGet
(`mediabrowser.server.core` 4.8.11) at compile time only, so that DLL is the only file you need to deploy.

The code is split into a pure, unit-tested `Xtream/` layer (API client, tolerant JSON parser, URL builder, XMLTV
parser, time zone handling) and thin Emby-facing classes (`XtreamTunerHost`, `XtreamCatchupChannel`, `XtreamService`).
See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Testing against a mock provider

`tools/mock-xtream/mock_xtream.py` (Python 3, standard library only) fakes an Xtream provider with five channels,
categories, an EPG and a catch-up archive:

```
cd tools/mock-xtream
# optional: a short looping test stream
ffmpeg -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine=frequency=440 \
       -t 10 -c:v libx264 -preset veryfast -b:v 600k -c:a aac -f mpegts sample.ts
python3 mock_xtream.py --port 8080
```

Then add a source in Emby with `http://localhost:8080`, username `test`, password `test`. Without `sample.ts`
the stream endpoints return 404 with a hint, but channels, guide and catch-up listings still work.

## License

See [LICENSE](LICENSE).
