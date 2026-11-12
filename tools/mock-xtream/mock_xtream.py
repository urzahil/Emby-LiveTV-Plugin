#!/usr/bin/env python3
"""Minimal fake Xtream Codes provider for manual testing of the Emby.Xtream plugin.

Python 3 standard library only.  Usage:

    python3 mock_xtream.py [--port 8080] [--host 0.0.0.0] [--user test] [--password test] [--rate 250000]

Endpoints (credentials default to test/test):
    /player_api.php?username=&password=[&action=get_live_categories|get_live_streams|get_simple_data_table|get_short_epg]
    /xmltv.php?username=&password=
    /live/<user>/<pass>/<id>.ts | .m3u8       (also /<user>/<pass>/<id>)
    /streaming/timeshift.php?username=&password=&stream=<id>&start=YYYY-MM-DD:HH-MM&duration=<minutes>
    /timeshift/<user>/<pass>/<minutes>/<YYYY-MM-DD:HH-MM>/<id>.ts

Streams serve a looping MPEG-TS placeholder from ``sample.ts`` next to this script. Create one, for example:

    ffmpeg -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine=frequency=440 \\
           -t 10 -c:v libx264 -preset veryfast -b:v 600k -c:a aac -f mpegts sample.ts

Without sample.ts the stream endpoints answer 404 with a hint; everything else still works.
The provider's "server timezone" is Europe/Berlin, to exercise the plugin's timezone conversion.
"""
import argparse
import base64
import json
import os
import sys
import time
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse
from xml.sax.saxutils import escape

try:
    from zoneinfo import ZoneInfo
    SERVER_TZ_NAME = "Europe/Berlin"
    SERVER_TZ = ZoneInfo(SERVER_TZ_NAME)
except Exception:  # tzdata missing: fall back to a fixed +01:00
    SERVER_TZ_NAME = "Europe/Berlin"
    SERVER_TZ = timezone(timedelta(hours=1))

HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "sample.ts")

CATEGORIES = [
    {"category_id": "1", "category_name": "News", "parent_id": 0},
    {"category_id": "2", "category_name": "Sports", "parent_id": 0},
    {"category_id": "3", "category_name": "Movies", "parent_id": 0},
    {"category_id": "4", "category_name": "Kids", "parent_id": 0},
]

# stream_id, name, category, tv_archive days (0 = none)
CHANNELS = [
    (101, "Mock News 24", "1", 3),
    (102, "Mock Sport 1", "2", 3),
    (103, "Mock Sport 2", "2", 0),
    (104, "Mock Movies", "3", 7),
    (105, "Mock Kids", "4", 0),
]

SHOWS = {
    "1": ["Morning News", "Business Report", "World Today", "Weather & Traffic", "Evening News"],
    "2": ["Football Live", "Sports Centre", "Tennis Open", "Highlights", "Motorsport"],
    "3": ["Action Movie", "Comedy Night", "Classic Film", "Thriller", "Movie Premiere"],
    "4": ["Cartoon Hour", "Kids Quiz", "Animal Friends", "Story Time", "Puzzle Club"],
}

ARGS = None


def epg_id(stream_id):
    return "mock%d.example" % stream_id


def programmes(stream_id, category, now):
    """One-hour programmes from 4 days ago until 3 days ahead, aligned to full hours (UTC)."""
    start = now.replace(minute=0, second=0, microsecond=0) - timedelta(days=4)
    titles = SHOWS[category]
    for i in range(7 * 24):
        s = start + timedelta(hours=i)
        e = s + timedelta(hours=1)
        title = titles[(i + stream_id) % len(titles)]
        yield s, e, "%s (%02d:00)" % (title, s.hour), "Description of %s on channel %d." % (title, stream_id)


def unix(dt):
    return int(dt.timestamp())


def b64(text):
    return base64.b64encode(text.encode("utf-8")).decode("ascii")


def server_local(dt):
    return dt.astimezone(SERVER_TZ).strftime("%Y-%m-%d %H:%M:%S")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "MockXtream/1.0"

    def log_message(self, fmt, *args):
        # never print passwords
        line = fmt % args
        for secret in (ARGS.password,):
            if secret:
                line = line.replace(secret, "***")
        sys.stderr.write("%s - %s\n" % (self.address_string(), line))

    # ---- helpers ----------------------------------------------------------------
    def send_bytes(self, status, body, content_type):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def send_json(self, obj, status=200):
        self.send_bytes(status, json.dumps(obj).encode("utf-8"), "application/json")

    def authed(self, user, password):
        return user == ARGS.user and password == ARGS.password

    # ---- routing ----------------------------------------------------------------
    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        parts = [unquote(p) for p in url.path.split("/") if p]
        path = url.path.lower()

        if path == "/player_api.php":
            return self.player_api(q)
        if path == "/xmltv.php":
            return self.xmltv(q)
        if path == "/get.php":
            return self.send_bytes(404, b"get.php is not implemented by the mock; use player_api.php\n", "text/plain")
        if path == "/streaming/timeshift.php":
            if not self.authed(q.get("username"), q.get("password")):
                return self.send_bytes(403, b"forbidden\n", "text/plain")
            minutes = int(q.get("duration", "60") or 60)
            return self.stream(minutes * 60)
        if len(parts) == 6 and parts[0] == "timeshift":
            if not self.authed(parts[1], parts[2]):
                return self.send_bytes(403, b"forbidden\n", "text/plain")
            return self.stream(int(parts[3]) * 60)
        if len(parts) == 4 and parts[0] == "live":
            return self.live(parts[1], parts[2], parts[3])
        if len(parts) == 3:  # legacy /user/pass/id
            return self.live(parts[0], parts[1], parts[2])
        self.send_bytes(404, b"not found\n", "text/plain")

    # ---- API --------------------------------------------------------------------
    def player_api(self, q):
        if not self.authed(q.get("username"), q.get("password")):
            return self.send_json({"user_info": {"auth": 0}})

        action = q.get("action")
        now = datetime.now(timezone.utc)
        if not action:
            return self.send_json({
                "user_info": {
                    "username": ARGS.user, "password": ARGS.password, "message": "Mock provider",
                    "auth": 1, "status": "Active", "exp_date": str(unix(now + timedelta(days=365))),
                    "is_trial": "0", "active_cons": "0", "created_at": str(unix(now - timedelta(days=30))),
                    "max_connections": "2", "allowed_output_formats": ["m3u8", "ts"],
                },
                "server_info": {
                    "url": "localhost", "port": str(ARGS.port), "https_port": "443", "server_protocol": "http",
                    "rtmp_port": "1935", "timezone": SERVER_TZ_NAME,
                    "timestamp_now": unix(now), "time_now": server_local(now),
                },
            })
        if action == "get_live_categories":
            return self.send_json(CATEGORIES)
        if action == "get_live_streams":
            cat = q.get("category_id")
            out = []
            for n, (sid, name, category, days) in enumerate(CHANNELS, 1):
                if cat and cat != category:
                    continue
                out.append({
                    "num": n, "name": name, "stream_type": "live",
                    # mix numbers and strings on purpose, like real panels do
                    "stream_id": sid if n % 2 else str(sid),
                    "stream_icon": "", "epg_channel_id": epg_id(sid), "added": str(unix(now - timedelta(days=90))),
                    "category_id": category if n % 2 else int(category), "custom_sid": "",
                    "tv_archive": 1 if days else 0, "direct_source": "", "tv_archive_duration": days,
                })
            return self.send_json(out)
        if action in ("get_simple_data_table", "get_short_epg"):
            return self.epg(action, q, now)
        self.send_json({"error": "unknown action"}, 400)

    def epg(self, action, q, now):
        try:
            sid = int(q.get("stream_id", "0"))
        except ValueError:
            sid = 0
        chan = next((c for c in CHANNELS if c[0] == sid), None)
        if not chan:
            return self.send_json({"epg_listings": []})
        _, _, category, days = chan
        listings = []
        for s, e, title, desc in programmes(sid, category, now):
            if action == "get_short_epg" and e <= now:
                continue
            archived = bool(days) and e <= now and s >= now - timedelta(days=days)
            listings.append({
                "id": str(unix(s)), "epg_id": "1", "title": b64(title), "lang": "en",
                "start": server_local(s), "end": server_local(e), "description": b64(desc),
                "channel_id": epg_id(sid), "start_timestamp": str(unix(s)), "stop_timestamp": str(unix(e)),
                "now_playing": 1 if s <= now < e else 0, "has_archive": 1 if archived else 0,
            })
        if action == "get_short_epg":
            listings = listings[: int(q.get("limit", "4") or 4)]
        self.send_json({"epg_listings": listings})

    def xmltv(self, q):
        if not self.authed(q.get("username"), q.get("password")):
            return self.send_bytes(403, b"forbidden\n", "text/plain")
        now = datetime.now(timezone.utc)
        out = ['<?xml version="1.0" encoding="UTF-8"?>', '<tv generator-info-name="mock-xtream">']
        for sid, name, category, _ in CHANNELS:
            out.append('<channel id="%s"><display-name>%s</display-name></channel>' % (epg_id(sid), escape(name)))
        fmt = "%Y%m%d%H%M%S +0000"
        for sid, name, category, _ in CHANNELS:
            for s, e, title, desc in programmes(sid, category, now):
                out.append(
                    '<programme start="%s" stop="%s" channel="%s"><title lang="en">%s</title>'
                    '<desc lang="en">%s</desc><category lang="en">%s</category></programme>'
                    % (s.strftime(fmt), e.strftime(fmt), epg_id(sid), escape(title), escape(desc),
                       escape(next(c["category_name"] for c in CATEGORIES if c["category_id"] == category))))
        out.append("</tv>")
        self.send_bytes(200, "\n".join(out).encode("utf-8"), "application/xml")

    # ---- streams ----------------------------------------------------------------
    def live(self, user, password, last):
        if not self.authed(user, password):
            return self.send_bytes(403, b"forbidden\n", "text/plain")
        stem, _, ext = last.rpartition(".")
        if not stem:
            stem, ext = last, "ts"
        if ext == "m3u8":
            body = ("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\n"
                    "/live/%s/%s/%s.ts\n#EXT-X-ENDLIST\n" % (user, password, stem)).encode("utf-8")
            return self.send_bytes(200, body, "application/vnd.apple.mpegurl")
        return self.stream(None)

    def stream(self, max_seconds):
        """Loop sample.ts, paced to --rate bytes/second; stop after max_seconds when given."""
        if not os.path.isfile(SAMPLE):
            msg = ("No sample.ts next to mock_xtream.py. Create one with ffmpeg, e.g.:\n"
                   "ffmpeg -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine=frequency=440 "
                   "-t 10 -c:v libx264 -preset veryfast -b:v 600k -c:a aac -f mpegts sample.ts\n")
            return self.send_bytes(404, msg.encode("utf-8"), "text/plain")
        with open(SAMPLE, "rb") as f:
            data = f.read()
        if not data:
            return self.send_bytes(404, b"sample.ts is empty\n", "text/plain")

        self.send_response(200)
        self.send_header("Content-Type", "video/mp2t")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        if self.command == "HEAD":
            return

        chunk = 188 * 56
        started = time.time()
        sent = 0
        try:
            while True:
                for i in range(0, len(data), chunk):
                    block = data[i:i + chunk]
                    self.wfile.write(block)
                    self.wfile.flush()
                    sent += len(block)
                    # pace so clients do not buffer an unbounded amount
                    ahead = sent / float(ARGS.rate) - (time.time() - started)
                    if ahead > 0:
                        time.sleep(min(ahead, 1.0))
                    if max_seconds is not None and time.time() - started >= max_seconds:
                        return
        except (BrokenPipeError, ConnectionResetError):
            return


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--host", default="0.0.0.0")
    p.add_argument("--port", type=int, default=8080)
    p.add_argument("--user", default="test")
    p.add_argument("--password", default="test")
    p.add_argument("--rate", type=int, default=250000, help="stream pacing in bytes/second (default 250000)")
    ARGS = p.parse_args()

    server = ThreadingHTTPServer((ARGS.host, ARGS.port), Handler)
    server.daemon_threads = True
    print("Mock Xtream provider on http://%s:%d  user=%s password=%s  sample.ts: %s"
          % (ARGS.host, ARGS.port, ARGS.user, ARGS.password, "found" if os.path.isfile(SAMPLE) else "MISSING"))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
