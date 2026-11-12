using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;
using Emby.Xtream.Xtream.XmlTv;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.LiveTv
{
    /// <summary>
    /// Per-tuner cache of the provider's XMLTV guide. The XML is streamed to disk (CachePath/xtream/) and parsed once per TTL
    /// into memory, filtered to the imported channels and a time window. Concurrent refreshes are serialised.
    /// </summary>
    public sealed class GuideCache
    {
        private sealed class Entry
        {
            public DateTime LoadedUtc;
            public string Signature;
            public Dictionary<string, List<XmlTvProgram>> Programs;
        }

        private readonly string _cacheDirectory;
        private readonly ILogger _logger;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SemaphoreSlim> _locks = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public GuideCache(string cacheRoot, ILogger logger)
        {
            _cacheDirectory = Path.Combine(cacheRoot, "xtream");
            _logger = logger;
        }

        private SemaphoreSlim GetLock(string tunerId)
        {
            lock (_gate)
            {
                SemaphoreSlim s;
                if (!_locks.TryGetValue(tunerId, out s)) { s = new SemaphoreSlim(1, 1); _locks[tunerId] = s; }
                return s;
            }
        }

        private string FilePath(string tunerId) => Path.Combine(_cacheDirectory, "xmltv_" + Sanitize(tunerId) + ".xml");

        private static string Sanitize(string s)
        {
            var chars = (s ?? "x").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }

        /// <summary>
        /// Returns programmes per epg channel id. <paramref name="signature"/> identifies the set of imported channels;
        /// a change forces a re-parse (the downloaded file is reused when still fresh).
        /// </summary>
        public async Task<Dictionary<string, List<XmlTvProgram>>> GetAsync(
            string tunerId, XtreamClient client, ISet<string> channelIds, string signature, TimeSpan ttl, int guideDays, CancellationToken ct)
        {
            var gate = GetLock(tunerId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Entry entry;
                lock (_gate) _entries.TryGetValue(tunerId, out entry);

                DateTime now = DateTime.UtcNow;
                if (entry != null && now - entry.LoadedUtc < ttl && entry.Signature == signature) return entry.Programs;

                string path = FilePath(tunerId);
                bool fileFresh = false;
                try { fileFresh = File.Exists(path) && now - File.GetLastWriteTimeUtc(path) < ttl; } catch (IOException) { }

                if (!fileFresh)
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    string tmp = path + ".tmp";
                    _logger.Info("Downloading XMLTV guide for tuner {0}", tunerId);
                    using (var source = await client.OpenXmltvAsync(ct).ConfigureAwait(false))
                    using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }

                var programs = await Task.Run(() =>
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                    {
                        return XmlTvParser.Parse(fs, channelIds, now.AddDays(-1), now.AddDays(Math.Max(1, guideDays) + 1));
                    }
                }, ct).ConfigureAwait(false);

                _logger.Info("Parsed XMLTV guide for tuner {0}: {1} channels with programmes", tunerId, programs.Count);

                lock (_gate)
                {
                    _entries[tunerId] = new Entry { LoadedUtc = now, Signature = signature, Programs = programs };
                }
                return programs;
            }
            finally
            {
                gate.Release();
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
            try
            {
                if (Directory.Exists(_cacheDirectory))
                {
                    foreach (var f in Directory.GetFiles(_cacheDirectory, "xmltv_*")) { try { File.Delete(f); } catch (IOException) { } }
                }
            }
            catch (Exception ex) { _logger.Debug("Could not clear guide cache: {0}", ex.Message); }
        }
    }
}
