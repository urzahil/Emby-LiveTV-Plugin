using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Maps random tokens to provider timeshift URLs, so the media source Emby stores, logs and hands to ffmpeg is a
    /// local URL without credentials. Tokens live in memory only and expire after <see cref="Lifetime"/> without use.
    /// </summary>
    public static class CatchupStreamRegistry
    {
        public sealed class Entry
        {
            public string Url;
            public string UserAgent;
            public DateTime LastUsedUtc;
        }

        /// <summary>Long enough for a paused multi-hour programme; each request extends it.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

        private static readonly Dictionary<string, Entry> ByToken = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> TokenByUrl = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Returns the token for this URL (the same one while it is in use, so Emby sees a stable media source).</summary>
        public static string Register(string url, string userAgent, DateTime utcNow)
        {
            lock (ByToken)
            {
                Purge(utcNow);
                string token;
                if (TokenByUrl.TryGetValue(url, out token))
                {
                    var existing = ByToken[token];
                    existing.UserAgent = userAgent;
                    existing.LastUsedUtc = utcNow;
                    return token;
                }
                token = NewToken();
                ByToken[token] = new Entry { Url = url, UserAgent = userAgent, LastUsedUtc = utcNow };
                TokenByUrl[url] = token;
                return token;
            }
        }

        public static bool TryResolve(string token, DateTime utcNow, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(token)) return false;
            lock (ByToken)
            {
                Purge(utcNow);
                Entry found;
                if (!ByToken.TryGetValue(token, out found)) return false;
                found.LastUsedUtc = utcNow;
                entry = new Entry { Url = found.Url, UserAgent = found.UserAgent, LastUsedUtc = utcNow };
                return true;
            }
        }

        private static void Purge(DateTime utcNow)
        {
            List<string> expired = null;
            foreach (var kv in ByToken)
                if (utcNow - kv.Value.LastUsedUtc > Lifetime) (expired ?? (expired = new List<string>())).Add(kv.Key);
            if (expired == null) return;
            foreach (var token in expired)
            {
                TokenByUrl.Remove(ByToken[token].Url);
                ByToken.Remove(token);
            }
        }

        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
