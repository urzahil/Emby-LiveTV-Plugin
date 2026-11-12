using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Xtream
{
    /// <summary>Builds Xtream Codes URLs and sanitises URLs for logging.</summary>
    public sealed class XtreamUrlBuilder
    {
        private readonly string _baseUrl;
        private readonly string _user;
        private readonly string _pass;

        public XtreamUrlBuilder(XtreamCredentials credentials)
        {
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            _baseUrl = (credentials.BaseUrl ?? string.Empty).TrimEnd('/');
            _user = credentials.Username ?? string.Empty;
            _pass = credentials.Password ?? string.Empty;
        }

        private static string E(string s) => Uri.EscapeDataString(s ?? string.Empty);

        private string Api(string action, string extra)
        {
            var sb = new StringBuilder();
            sb.Append(_baseUrl).Append("/player_api.php?username=").Append(E(_user)).Append("&password=").Append(E(_pass));
            if (!string.IsNullOrEmpty(action)) sb.Append("&action=").Append(action);
            if (!string.IsNullOrEmpty(extra)) sb.Append('&').Append(extra);
            return sb.ToString();
        }

        public string PlayerApi() => Api(null, null);
        public string LiveCategories() => Api("get_live_categories", null);

        public string LiveStreams(string categoryId = null) =>
            Api("get_live_streams", string.IsNullOrEmpty(categoryId) ? null : "category_id=" + E(categoryId));

        public string ShortEpg(string streamId, int limit) =>
            Api("get_short_epg", "stream_id=" + E(streamId) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture));

        public string SimpleDataTable(string streamId) => Api("get_simple_data_table", "stream_id=" + E(streamId));

        public string Xmltv() =>
            _baseUrl + "/xmltv.php?username=" + E(_user) + "&password=" + E(_pass);

        /// <summary>Live stream URL. extension is "ts" or "m3u8".</summary>
        public string Live(string streamId, string extension = "ts")
        {
            string ext = string.IsNullOrEmpty(extension) ? "ts" : extension.TrimStart('.');
            return _baseUrl + "/live/" + E(_user) + "/" + E(_pass) + "/" + E(streamId) + "." + ext;
        }

        /// <summary>
        /// Catch-up URL. <paramref name="startInServerTime"/> must already be expressed in the provider's timezone.
        /// style "query" => /streaming/timeshift.php?...; "path" => /timeshift/U/P/minutes/start/id.ts
        /// </summary>
        public string Timeshift(string streamId, DateTime startInServerTime, int durationMinutes, string style = "query")
        {
            if (durationMinutes < 1) durationMinutes = 1;
            string start = startInServerTime.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture);
            string mins = durationMinutes.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(style, "path", StringComparison.OrdinalIgnoreCase))
            {
                return _baseUrl + "/timeshift/" + E(_user) + "/" + E(_pass) + "/" + mins + "/" + start + "/" + E(streamId) + ".ts";
            }
            return _baseUrl + "/streaming/timeshift.php?username=" + E(_user) + "&password=" + E(_pass)
                   + "&stream=" + E(streamId) + "&start=" + start + "&duration=" + mins;
        }

        private static readonly Regex QuerySecrets = new Regex(@"(?<k>password|username|pass|token)=[^&\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PathSecrets = new Regex(@"/(?<k>live|timeshift|movie|series)/[^/\s""'<>?]+/[^/\s""'<>?]+/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Masks credentials in free text (e.g. exception messages that may embed a request URL).</summary>
        public static string SanitizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = QuerySecrets.Replace(text, "${k}=***");
            return PathSecrets.Replace(text, "/${k}/***/***/");
        }

        // ---- log sanitising ------------------------------------------------------

        /// <summary>
        /// Removes secrets from a URL so it can be logged: password/username query values become "***" and the
        /// credential path segments of /live/U/P/id, /timeshift/U/P/... and /movie|series/U/P/... are masked.
        /// </summary>
        public static string Sanitize(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string result = url;

            int q = result.IndexOf('?');
            if (q >= 0)
            {
                string head = result.Substring(0, q);
                string[] parts = result.Substring(q + 1).Split('&');
                for (int i = 0; i < parts.Length; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string key = parts[i].Substring(0, eq);
                    if (string.Equals(key, "password", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "username", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "pass", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "token", StringComparison.OrdinalIgnoreCase))
                    {
                        parts[i] = key + "=***";
                    }
                }
                result = head + "?" + string.Join("&", parts);
            }

            // path style: /{live|timeshift|movie|series}/USER/PASS/...
            int schemeEnd = result.IndexOf("://", StringComparison.Ordinal);
            int pathStart = result.IndexOf('/', schemeEnd < 0 ? 0 : schemeEnd + 3);
            if (pathStart >= 0)
            {
                int qm = result.IndexOf('?');
                string path = qm >= 0 ? result.Substring(pathStart, qm - pathStart) : result.Substring(pathStart);
                string tail = qm >= 0 ? result.Substring(qm) : string.Empty;
                var segs = path.Split('/');
                // segs[0] is "" (leading slash)
                if (segs.Length >= 4)
                {
                    string kind = segs[1].ToLowerInvariant();
                    if (kind == "live" || kind == "timeshift" || kind == "movie" || kind == "series")
                    {
                        segs[2] = "***";
                        segs[3] = "***";
                        path = string.Join("/", segs);
                    }
                }
                result = result.Substring(0, pathStart) + path + tail;
            }
            return result;
        }
    }
}
