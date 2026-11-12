using System;
using System.Collections.Generic;

namespace Emby.Xtream.Xtream
{
    /// <summary>Server base URL plus username/password for an Xtream Codes provider.</summary>
    public sealed class XtreamCredentials
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public XtreamCredentials() { }

        public XtreamCredentials(string baseUrl, string username, string password)
        {
            BaseUrl = NormalizeBaseUrl(baseUrl);
            Username = username;
            Password = password;
        }

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

        /// <summary>
        /// Trims, adds a missing scheme (http), strips any path/query/fragment and trailing slashes, keeping scheme://host[:port].
        /// A path prefix is preserved only if it is not one of the known panel endpoints (for panels hosted below a sub-path).
        /// </summary>
        public static string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;

            int q = url.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) url = url.Substring(0, q);

            // If a panel endpoint was pasted, cut the path at it.
            string[] endpoints = { "/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php", "/live/", "/streaming/", "/timeshift/" };
            foreach (var ep in endpoints)
            {
                int idx = url.IndexOf(ep, url.IndexOf("://", StringComparison.Ordinal) + 3, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) { url = url.Substring(0, idx); break; }
            }
            return url.TrimEnd('/');
        }

        /// <summary>
        /// Parses a pasted M3U URL such as http://host:8080/get.php?username=U&amp;password=P&amp;type=m3u_plus
        /// (also accepts player_api.php / xmltv.php URLs). Returns null if it does not carry credentials.
        /// </summary>
        public static XtreamCredentials ParseM3uUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            int q = url.IndexOf('?');
            if (q < 0) return null;
            var query = ParseQuery(url.Substring(q + 1));
            string user, pass;
            if (!query.TryGetValue("username", out user) || string.IsNullOrEmpty(user)) return null;
            if (!query.TryGetValue("password", out pass) || pass == null) return null;
            string baseUrl = NormalizeBaseUrl(url.Substring(0, q));
            if (string.IsNullOrEmpty(baseUrl)) return null;
            return new XtreamCredentials { BaseUrl = baseUrl, Username = user, Password = pass };
        }

        internal static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? string.Empty : part.Substring(eq + 1);
                result[SafeUnescape(k)] = SafeUnescape(v);
            }
            return result;
        }

        private static string SafeUnescape(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (UriFormatException) { return s; }
        }
    }
}
