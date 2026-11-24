using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Emby.Xtream.Xtream
{
    /// <summary>Short stable hex digest of a string, for cache keys and file names (not for security).</summary>
    internal static class ShortHash
    {
        public static string Of(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Identifies the set of imported channels of a tuner: the same count of different channels must not match.</summary>
        public static string ChannelSignature(string url, IEnumerable<string> epgIds)
        {
            var ids = epgIds.ToList();
            ids.Sort(StringComparer.Ordinal);
            return ids.Count.ToString(CultureInfo.InvariantCulture) + ":" + Of((url ?? string.Empty) + "\n" + string.Join("\n", ids));
        }
    }
}
