using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Turns a channel logo URL into the picture a catch-up item should carry (see <see cref="LogoPadder"/>): the URL itself
    /// when the logo's shape is fine, a padded PNG on disk when it is too wide or tall, or null when it cannot be made safe.
    /// Each logo is downloaded once per server run; padded files are kept under the cache directory.
    /// </summary>
    public sealed class CatchupLogoCache
    {
        private const int MaxLogoBytes = 5 * 1024 * 1024;

        private readonly string _directory;
        private readonly Dictionary<string, string> _resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public CatchupLogoCache(string directory)
        {
            _directory = directory;
        }

        /// <summary>Returns the picture for a logo, or null for none. Download problems fall back to the URL itself.</summary>
        public async Task<string> ResolveAsync(string logoUrl, IXtreamHttp http, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(logoUrl)) return null;
            logoUrl = logoUrl.Trim();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string known;
                if (_resolved.TryGetValue(logoUrl, out known)) return known;

                string path = Path.Combine(_directory, Hash(logoUrl) + ".png");
                string result;
                if (File.Exists(path))
                {
                    result = path;
                }
                else
                {
                    byte[] data = await DownloadAsync(logoUrl, http, ct).ConfigureAwait(false);
                    result = data == null ? logoUrl : Decide(logoUrl, data, path);
                    if (data == null)
                    {
                        // transient failure: keep the URL but try again on a later refresh
                        return result;
                    }
                }
                _resolved[logoUrl] = result;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string Decide(string logoUrl, byte[] data, string path)
        {
            byte[] padded;
            switch (LogoPadder.Process(data, out padded))
            {
                case LogoPadder.Verdict.UsePadded:
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string temp = path + ".tmp";
                    File.WriteAllBytes(temp, padded);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                    return path;
                case LogoPadder.Verdict.Drop:
                    return null;
                default:
                    return logoUrl;
            }
        }

        private static async Task<byte[]> DownloadAsync(string url, IXtreamHttp http, CancellationToken ct)
        {
            try
            {
                using (var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[81920];
                    int n;
                    while ((n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        buffer.Write(chunk, 0, n);
                        if (buffer.Length > MaxLogoBytes) return null;
                    }
                    return buffer.ToArray();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
