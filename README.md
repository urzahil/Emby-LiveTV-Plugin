# Emby Xtream IPTV plugin

[![build](https://github.com/urzahil/Emby-LiveTV-Plugin/actions/workflows/build.yml/badge.svg)](https://github.com/urzahil/Emby-LiveTV-Plugin/actions/workflows/build.yml)
![Emby 4.8+](https://img.shields.io/badge/Emby-4.8%2B-52b54b)
![version](https://img.shields.io/badge/version-1.0.0-blue)
![license GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-lightgrey)

Watch IPTV in Emby. This plugin adds an **Xtream Codes** provider as a native
**Live TV source**, fills the **guide** from the provider, and lets you watch
**catch-up / TV archive** programmes.

- Appears under *Live TV → TV Sources → Add* next to HDHomeRun and M3U
- Channels, logos, numbers and category tags from the Xtream API, with an optional category filter
- Guide data straight from the provider (`xmltv.php` or per-channel API) — no separate guide provider needed
- Catch-up: browse *channel → day → programme* and play past programmes via the provider's timeshift URLs
- One DLL, no extra dependencies; passwords are never written to the log

## Requirements

- Emby Server **4.8** or newer (built against the 4.8.11 SDK; Emby Premiere is **not** required for live viewing)
- An Xtream Codes account: server URL, username and password

## Install

1. Download `Emby.Xtream.dll` from the [latest build artifact](https://github.com/urzahil/Emby-LiveTV-Plugin/actions/workflows/build.yml) (or [build it](#building-from-source)).
2. Copy it into the `plugins` folder of your Emby data directory:
   - Linux: `/var/lib/emby/plugins/`
   - Docker: `<config volume>/plugins/`
   - Windows: `%APPDATA%\Emby-Server\programdata\plugins\`
   - macOS: `~/.config/emby-server/plugins/`
3. Restart Emby Server. *Xtream IPTV* now shows under *Settings → Plugins*.

## Add a TV source

1. *Settings → Live TV → TV Sources → Add* → **Xtream Codes (IPTV)**.
2. Enter the **Server URL** (`http://provider.example:8080`), **Username** and **Password** —
   or just paste the M3U link your provider gave you (`…/get.php?username=…&password=…`); it is split for you.
3. Click **Test connection**: you get the account status, expiry date and connection limit, and the provider's
   categories are loaded. Tick the categories you want (none ticked = everything).
4. Pick the stream format (**MPEG-TS** is the most compatible, **HLS** if your provider only serves `.m3u8`),
   the simultaneous connection limit (what your account allows, usually 1–2) and, if needed, a custom User-Agent.
5. **Save**. Emby validates the account, imports the channels and refreshes the guide.

Channels show up in *Live TV* like any other tuner's; a guide refresh (*Live TV → Refresh Guide*) re-reads the
channel list from the provider.

## Guide data

*Import guide data from the provider* is on by default. Two sources are available on the source's page:

| Source | How it works | Use when |
|---|---|---|
| **XMLTV** (default) | Downloads `xmltv.php` once per cache period, streams it to disk and parses only your imported channels | Almost always |
| **Provider API** | Calls `get_simple_data_table` per channel, cached per channel | The provider's XMLTV file is missing or broken |

The provider's `epg_channel_id` is exposed as the tuner channel id, so you can still map your own XMLTV guide
provider onto these channels in Emby if the provider's listings are poor.

## Catch-up

Emby's Live TV API has no catch-up concept, so past programmes cannot be started from the guide grid. Instead the
plugin adds a channel called **IPTV Catch-up** under *Channels* (it also shows up on the home screen like any channel):

```
IPTV Catch-up › [source] › category › channel › day › programme
```

Only channels the provider flags with an archive are listed, going back as far as the provider keeps them (capped by
*Maximum catch-up days*). Picking a programme plays it through the provider's timeshift URL, with the start time
converted to the provider's server timezone as those URLs require.

Catch-up can be turned off per source (source page) or globally (plugin settings). If a provider does not answer the
default `streaming/timeshift.php` URL, switch the source to the alternative `timeshift/user/pass/…` style.

## Plugin settings

*Settings → Plugins → Xtream IPTV*

| Setting | Default | Meaning |
|---|---|---|
| Guide cache (hours) | 6 | How long downloaded guide data is reused before fetching again |
| Guide days | 3 | How many days ahead to keep from the provider's guide |
| Enable catch-up channel | on | Shows or hides the *IPTV Catch-up* channel |
| Maximum catch-up days | 7 | Upper limit on how far back catch-up folders go |
| HTTP timeout (seconds) | 60 | Timeout for provider requests |

## Limitations

- No catch-up from the guide grid (Emby has no API for it) — use the *IPTV Catch-up* channel.
- Live TV only; the VOD and series parts of the Xtream API are not imported.
- Catch-up quality depends on the provider: archive length, `has_archive` flags and timeshift URL styles vary.
- Live and catch-up streams both count against your provider's connection limit.
- Recording needs Emby Premiere, as with every Emby tuner.

## Troubleshooting

- **"Error saving TV provider"** — the account check failed. The Emby log (`embyserver.txt`) has the reason
  (bad credentials, expired or inactive account, unreachable host).
- **Channels play in VLC but not in Emby** — try the other stream format, or set the User-Agent your provider expects
  (the default is `VLC/3.0.21 LibVLC/3.0.21`).
- **Guide is empty** — check that *Import guide data* is on and that the provider's `xmltv.php` is not empty; the
  *Provider API* source is a fallback.
- **Catch-up plays the wrong time** — the provider reports a wrong `timezone` in `server_info`; this is a provider bug
  the plugin cannot correct.

Credentials are stored in Emby's Live TV configuration (admin-only), the same way Emby's own M3U tuner stores its URL.

## Building from source

Requires the .NET 8 SDK.

```bash
dotnet build Emby.Xtream.sln -c Release
```

```bash
dotnet test Emby.Xtream.sln -c Release
```

The plugin is `src/Emby.Xtream/bin/Release/netstandard2.0/Emby.Xtream.dll`; the Emby SDK (`mediabrowser.server.core`
4.8.11) is a compile-time reference only. The code is split into a pure, unit-tested `Xtream/` layer (API client,
tolerant JSON parser, URL builder, XMLTV parser, timezone handling) and thin Emby-facing classes
(`XtreamTunerHost`, `XtreamCatchupChannel`, `XtreamService`). Design notes: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Testing against a mock provider

`tools/mock-xtream/mock_xtream.py` (Python 3, standard library only) fakes a provider with channels, categories,
an EPG and a catch-up archive:

```bash
cd tools/mock-xtream && python3 mock_xtream.py --port 8080
```

Optionally create a short looping test stream next to it first:

```bash
ffmpeg -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine=frequency=440 -t 10 -c:v libx264 -preset veryfast -b:v 600k -c:a aac -f mpegts tools/mock-xtream/sample.ts
```

Then add a source in Emby with `http://localhost:8080`, username `test`, password `test`.

## Contributing

Branches are named `feature/<what-it-adds>` or `fix/<what-it-fixes>`; open a pull request against `main`.
CI builds and tests every push and attaches the compiled DLL as an artifact.

## License

[GPL-3.0](LICENSE)
