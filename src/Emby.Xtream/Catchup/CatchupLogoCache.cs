using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.Json;

namespace Emby.Xtream.Catchup
{
    /// <summary>
    /// Turns a channel logo URL into the picture a catch-up item should carry (see <see cref="LogoPadder"/>): the URL itself
    /// when the logo's shape is fine, a padded PNG on disk when it is too wide or tall, or null when it cannot be made safe.
    /// <para>Each logo is downloaded once: the verdict for every URL is kept in a small manifest (verdicts.json) next to the
    /// padded files, so a restart does not fetch all logos again. Downloads run in parallel (a few at a time), a URL asked
    /// for by several callers is fetched once, and one that failed is not retried for a while.</para>
    /// </summary>
    public sealed class CatchupLogoCache
    {
        private const int MaxLogoBytes = 5 * 1024 * 1024;
        public const int MaxParallelDownloads = 6;
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

        private const string Keep = "keep", Drop = "drop", Padded = "padded";

        private readonly string _directory;
        private readonly string _manifestPath;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _downloads = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _verdicts = new Dictionary<string, string>(StringComparer.Ordinal); // hash -> verdict
        private readonly Dictionary<string, Task<string>> _inFlight = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _failedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private bool _manifestLoaded;

        public CatchupLogoCache(string directory, Func<DateTime> utcNow = null)
        {
            _directory = directory;
            _manifestPath = Path.Combine(directory, "verdicts.json");
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Returns the picture for a logo, or null for none. Download problems fall back to the URL itself.</summary>
        public async Task<string> ResolveAsync(string logoUrl, IXtreamHttp http, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(logoUrl)) return null;
            logoUrl = logoUrl.Trim();

            for (;;)
            {
                Task<string> task;
                lock (_lock)
                {
                    string known;
                    if (TryKnown(logoUrl, out known)) return known;
                    DateTime failed;
                    if (_failedAt.TryGetValue(logoUrl, out failed))
                    {
                        if (_utcNow() - failed < RetryAfterFailure) return logoUrl;
                        _failedAt.Remove(logoUrl);
                    }
                    if (!_inFlight.TryGetValue(logoUrl, out task))
                    {
                        // off this thread: LoadAsync takes _lock itself when it ends, and may end at once
                        task = Task.Run(() => LoadAsync(logoUrl, http, ct));
                        _inFlight[logoUrl] = task;
                    }
                }

                try
                {
                    return await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // the caller that started the shared download was cancelled, not this one: go again
                }
            }
        }

        // caller holds _lock
        private bool TryKnown(string logoUrl, out string result)
        {
            result = null;
            EnsureManifest();
            string verdict;
            if (!_verdicts.TryGetValue(ShortHash.Of(logoUrl), out verdict)) return false;
            switch (verdict)
            {
                case Keep: result = logoUrl; return true;
                case Drop: result = null; return true;
                case Padded:
                    string path = PaddedPath(logoUrl);
                    if (File.Exists(path)) { result = path; return true; }
                    return false; // the file is gone: decide again
                default: return false;
            }
        }

        private async Task<string> LoadAsync(string logoUrl, IXtreamHttp http, CancellationToken ct)
        {
            try
            {
                await _downloads.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!await LogoUrlPolicy.IsAllowedAsync(logoUrl, ct).ConfigureAwait(false))
                    {
                        Record(logoUrl, Drop);
                        return null;
                    }

                    byte[] data = await DownloadAsync(logoUrl, http, ct).ConfigureAwait(false);
                    if (data == null)
                    {
                        // transient failure: keep the URL, try again on a later refresh
                        lock (_lock) _failedAt[logoUrl] = _utcNow();
                        return logoUrl;
                    }

                    byte[] padded;
                    switch (LogoPadder.Process(data, out padded))
                    {
                        case LogoPadder.Verdict.UsePadded:
                            string path = PaddedPath(logoUrl);
                            Directory.CreateDirectory(_directory);
                            string temp = path + ".tmp";
                            File.WriteAllBytes(temp, padded);
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(temp, path);
                            Record(logoUrl, Padded);
                            return path;
                        case LogoPadder.Verdict.Drop:
                            Record(logoUrl, Drop);
                            return null;
                        default:
                            Record(logoUrl, Keep);
                            return logoUrl;
                    }
                }
                finally
                {
                    _downloads.Release();
                }
            }
            finally
            {
                lock (_lock) _inFlight.Remove(logoUrl);
            }
        }

        private string PaddedPath(string logoUrl) => Path.Combine(_directory, ShortHash.Of(logoUrl) + ".png");

        // ---- manifest -------------------------------------------------------------

        private void Record(string logoUrl, string verdict)
        {
            lock (_lock)
            {
                EnsureManifest();
                _verdicts[ShortHash.Of(logoUrl)] = verdict;
                SaveManifest();
            }
        }

        // caller holds _lock
        private void EnsureManifest()
        {
            if (_manifestLoaded) return;
            _manifestLoaded = true;
            try
            {
                if (!File.Exists(_manifestPath)) return;
                var root = JsonLite.Parse(File.ReadAllText(_manifestPath, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return;
                foreach (var kv in root)
                {
                    string v = kv.Value as string;
                    if (v == Keep || v == Drop || v == Padded) _verdicts[kv.Key] = v;
                }
            }
            catch (Exception)
            {
                // a damaged manifest only means the logos are looked at again
                _verdicts.Clear();
            }
        }

        // caller holds _lock; the keys are hex and the values fixed words, so no escaping is needed
        private void SaveManifest()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (var kv in _verdicts)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":\"").Append(kv.Value).Append('"');
                }
                sb.Append('}');
                string temp = _manifestPath + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
                File.Move(temp, _manifestPath);
            }
            catch (Exception)
            {
                // the cache still works from memory
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
    }
}
