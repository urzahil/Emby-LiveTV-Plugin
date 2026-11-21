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
            /// <summary>Provider byte offset the served stream starts at (TsStartFinder); null until the first request found it.</summary>
            public long? StartOffset;
            /// <summary>Length of the served stream when the provider supports byte ranges; null when it cannot seek.</summary>
            public long? SeekableLength;
        }

        /// <summary>Long enough for a paused multi-hour programme; each request extends it.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

        private static readonly Dictionary<string, Entry> ByToken = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> TokenByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IDisposable> ActiveByToken = new Dictionary<string, IDisposable>(StringComparer.Ordinal);

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
                entry = new Entry
                {
                    Url = found.Url, UserAgent = found.UserAgent, LastUsedUtc = utcNow,
                    StartOffset = found.StartOffset, SeekableLength = found.SeekableLength
                };
                return true;
            }
        }

        /// <summary>Remembers where the served stream starts and how long it is, so byte ranges can be mapped later.</summary>
        public static void SetLayout(string token, long startOffset, long? seekableLength)
        {
            lock (ByToken)
            {
                Entry found;
                if (!ByToken.TryGetValue(token, out found)) return;
                found.StartOffset = startOffset;
                found.SeekableLength = seekableLength;
            }
        }

        /// <summary>
        /// Closes the provider connection a previous request for this token still holds, and remembers <paramref name="next"/>.
        /// ffmpeg opens the connection for a seek before it closes the old one, and a provider that allows one stream at a
        /// time answers the new one with 503 for as long as the old one stays open.
        /// </summary>
        public static void ReplaceActive(string token, IDisposable next)
        {
            IDisposable previous;
            lock (ByToken)
            {
                ActiveByToken.TryGetValue(token, out previous);
                if (next == null) ActiveByToken.Remove(token);
                else ActiveByToken[token] = next;
            }
            if (previous != null && !ReferenceEquals(previous, next))
            {
                try { previous.Dispose(); } catch (Exception) { /* already closed */ }
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
                ActiveByToken.Remove(token);
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
