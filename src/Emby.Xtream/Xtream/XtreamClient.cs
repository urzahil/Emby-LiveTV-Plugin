using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream.Json;

namespace Emby.Xtream.Xtream
{
    /// <summary>Calls the Xtream Codes player API through an injected <see cref="IXtreamHttp"/> and parses tolerantly.</summary>
    public sealed class XtreamClient
    {
        private readonly IXtreamHttp _http;
        private readonly XtreamUrlBuilder _urls;

        public XtreamClient(IXtreamHttp http, XtreamCredentials credentials)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            _urls = new XtreamUrlBuilder(credentials);
        }

        public XtreamUrlBuilder Urls => _urls;

        // ---- account ------------------------------------------------------------

        public async Task<XtreamAccount> GetAccountAsync(CancellationToken ct)
        {
            object root = await GetJsonAsync(_urls.PlayerApi(), ct).ConfigureAwait(false);
            return ParseAccount(root);
        }

        public static XtreamAccount ParseAccount(object root)
        {
            var account = new XtreamAccount();
            object ui = JsonLite.Get(root, "user_info");
            if (ui is Dictionary<string, object>)
            {
                var u = account.UserInfo;
                u.Auth = JsonLite.GetBool(ui, "auth");
                u.Status = JsonLite.GetString(ui, "status");
                string exp = JsonLite.GetString(ui, "exp_date");
                long expVal;
                if (!string.IsNullOrWhiteSpace(exp) && long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out expVal) && expVal > 0)
                    u.ExpDate = expVal;
                u.MaxConnections = JsonLite.GetInt(ui, "max_connections");
                u.ActiveConnections = JsonLite.GetInt(ui, "active_cons");
                u.IsTrial = JsonLite.GetBool(ui, "is_trial");
                foreach (var f in JsonLite.AsArrayLoose(JsonLite.Get(ui, "allowed_output_formats")))
                {
                    string s = JsonLite.ToStringValue(f);
                    if (!string.IsNullOrEmpty(s)) u.AllowedOutputFormats.Add(s);
                }
            }
            object si = JsonLite.Get(root, "server_info");
            if (si is Dictionary<string, object>)
            {
                var s = account.ServerInfo;
                s.Url = JsonLite.GetString(si, "url");
                s.Port = JsonLite.GetString(si, "port");
                s.HttpsPort = JsonLite.GetString(si, "https_port");
                s.ServerProtocol = JsonLite.GetString(si, "server_protocol");
                s.Timezone = JsonLite.GetString(si, "timezone");
                s.TimestampNow = JsonLite.GetLong(si, "timestamp_now");
                s.TimeNow = JsonLite.GetString(si, "time_now");
            }
            return account;
        }

        /// <summary>Returns a human readable problem description, or null when the account is usable.</summary>
        public static string GetAccountProblem(XtreamAccount account, DateTime utcNow)
        {
            if (account == null || account.UserInfo == null) return "The server did not return account information. Is this an Xtream Codes server?";
            var u = account.UserInfo;
            if (!u.Auth) return "Authentication failed: check the username and password.";
            if (!string.IsNullOrWhiteSpace(u.Status) && !string.Equals(u.Status.Trim(), "Active", StringComparison.OrdinalIgnoreCase))
                return "Account status is '" + u.Status.Trim() + "' (expected 'Active').";
            if (u.ExpDate.HasValue && XtreamClock.FromUnix(u.ExpDate.Value) < utcNow)
                return "The subscription expired on " + XtreamClock.FromUnix(u.ExpDate.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".";
            return null;
        }

        // ---- catalogue ----------------------------------------------------------

        public async Task<List<XtreamCategory>> GetLiveCategoriesAsync(CancellationToken ct)
        {
            object root = await GetJsonAsync(_urls.LiveCategories(), ct).ConfigureAwait(false);
            return ParseCategories(root);
        }

        public static List<XtreamCategory> ParseCategories(object root)
        {
            var list = new List<XtreamCategory>();
            foreach (var item in JsonLite.AsArrayLoose(root))
            {
                string id = JsonLite.GetString(item, "category_id");
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new XtreamCategory
                {
                    Id = id,
                    Name = JsonLite.GetString(item, "category_name") ?? id,
                    ParentId = JsonLite.GetString(item, "parent_id")
                });
            }
            return list;
        }

        public async Task<List<XtreamLiveStream>> GetLiveStreamsAsync(CancellationToken ct)
        {
            object root = await GetJsonAsync(_urls.LiveStreams(), ct).ConfigureAwait(false);
            return ParseLiveStreams(root);
        }

        public static List<XtreamLiveStream> ParseLiveStreams(object root)
        {
            var list = new List<XtreamLiveStream>();
            foreach (var item in JsonLite.AsArrayLoose(root))
            {
                string streamId = JsonLite.GetString(item, "stream_id");
                if (string.IsNullOrEmpty(streamId)) continue;
                list.Add(new XtreamLiveStream
                {
                    Num = JsonLite.GetInt(item, "num"),
                    Name = JsonLite.GetString(item, "name") ?? ("Channel " + streamId),
                    StreamId = streamId,
                    StreamIcon = NullIfEmpty(JsonLite.GetString(item, "stream_icon")),
                    EpgChannelId = NullIfEmpty(JsonLite.GetString(item, "epg_channel_id")),
                    Added = JsonLite.GetLong(item, "added"),
                    CategoryId = NullIfEmpty(JsonLite.GetString(item, "category_id")),
                    DirectSource = NullIfEmpty(JsonLite.GetString(item, "direct_source")),
                    TvArchive = JsonLite.GetBool(item, "tv_archive"),
                    TvArchiveDuration = JsonLite.GetInt(item, "tv_archive_duration")
                });
            }
            return list;
        }

        // ---- EPG ----------------------------------------------------------------

        public async Task<List<XtreamEpgEntry>> GetSimpleDataTableAsync(string streamId, XtreamServerInfo serverInfo, CancellationToken ct)
        {
            object root = await GetJsonAsync(_urls.SimpleDataTable(streamId), ct).ConfigureAwait(false);
            return ParseEpgListings(root, serverInfo);
        }

        public async Task<List<XtreamEpgEntry>> GetShortEpgAsync(string streamId, int limit, XtreamServerInfo serverInfo, CancellationToken ct)
        {
            object root = await GetJsonAsync(_urls.ShortEpg(streamId, limit), ct).ConfigureAwait(false);
            return ParseEpgListings(root, serverInfo);
        }

        public static List<XtreamEpgEntry> ParseEpgListings(object root, XtreamServerInfo serverInfo)
        {
            var list = new List<XtreamEpgEntry>();
            XtreamClock clock = null;
            foreach (var item in JsonLite.AsArrayLoose(JsonLite.Get(root, "epg_listings")))
            {
                DateTime start, stop;
                long st = JsonLite.GetLong(item, "start_timestamp");
                long sp = JsonLite.GetLong(item, "stop_timestamp");
                if (st > 0 && sp > 0)
                {
                    start = XtreamClock.FromUnix(st);
                    stop = XtreamClock.FromUnix(sp);
                }
                else
                {
                    DateTime s, e;
                    if (!TryParseLocal(JsonLite.GetString(item, "start"), out s) || !TryParseLocal(JsonLite.GetString(item, "end"), out e)) continue;
                    if (clock == null) clock = XtreamClock.FromServerInfo(serverInfo);
                    start = clock.ToUtc(s);
                    stop = clock.ToUtc(e);
                }
                if (stop <= start) continue;
                list.Add(new XtreamEpgEntry
                {
                    Id = JsonLite.GetString(item, "id"),
                    Title = DecodeMaybeBase64(JsonLite.GetString(item, "title")),
                    Description = DecodeMaybeBase64(JsonLite.GetString(item, "description")),
                    Lang = JsonLite.GetString(item, "lang"),
                    ChannelId = JsonLite.GetString(item, "channel_id"),
                    StartUtc = start,
                    StopUtc = stop,
                    HasArchive = JsonLite.GetBool(item, "has_archive"),
                    NowPlaying = JsonLite.GetBool(item, "now_playing")
                });
            }
            list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return list;
        }

        private static bool TryParseLocal(string s, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        /// <summary>
        /// EPG title/description are normally base64(UTF-8) but some panels send plain text.
        /// Falls back to the original string when decoding fails, is not valid UTF-8, or yields control characters.
        /// </summary>
        public static string DecodeMaybeBase64(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string trimmed = value.Trim();
            if (trimmed.Length < 4 || trimmed.Length % 4 != 0) return value;
            foreach (char c in trimmed)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=' || c == '-' || c == '_';
                if (!ok) return value;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(trimmed.Replace('-', '+').Replace('_', '/'));
                string decoded = new UTF8Encoding(false, true).GetString(bytes);
                foreach (char c in decoded)
                {
                    if (c < 0x20 && c != '\n' && c != '\r' && c != '\t') return value;
                    if (c == '�') return value;
                }
                return decoded;
            }
            catch (FormatException) { return value; }
            catch (ArgumentException) { return value; }
        }

        // ---- XMLTV --------------------------------------------------------------

        /// <summary>Opens the xmltv.php response stream. The caller disposes it.</summary>
        public Task<Stream> OpenXmltvAsync(CancellationToken ct) => _http.GetStreamAsync(_urls.Xmltv(), ct);

        // ---- plumbing -----------------------------------------------------------

        private async Task<object> GetJsonAsync(string url, CancellationToken ct)
        {
            Stream stream;
            try
            {
                stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new XtreamException("Request failed (" + XtreamUrlBuilder.Sanitize(url) + "): " + XtreamUrlBuilder.SanitizeText(ex.Message), ex);
            }
            using (stream)
            {
                try
                {
                    return JsonLite.Parse(stream);
                }
                catch (JsonLiteException ex)
                {
                    throw new XtreamException("The server returned invalid JSON for " + XtreamUrlBuilder.Sanitize(url) + ": " + XtreamUrlBuilder.SanitizeText(ex.Message), ex);
                }
            }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
