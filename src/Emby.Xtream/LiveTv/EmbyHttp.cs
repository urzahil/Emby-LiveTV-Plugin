using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Net;

namespace Emby.Xtream.LiveTv
{
    /// <summary>IXtreamHttp on top of Emby's IHttpClient.</summary>
    public sealed class EmbyHttp : IXtreamHttp
    {
        private readonly IHttpClient _http;
        private readonly string _userAgent;
        private readonly int _timeoutMs;

        public EmbyHttp(IHttpClient http, string userAgent, int timeoutSeconds)
        {
            _http = http;
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? XtreamDefaults.UserAgent : userAgent;
            _timeoutMs = Math.Max(5, timeoutSeconds) * 1000;
        }

        public async Task<Stream> GetStreamAsync(string url, CancellationToken cancellationToken)
        {
            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                UserAgent = _userAgent,
                TimeoutMs = _timeoutMs,
                EnableHttpCompression = true,
                DecompressionMethod = CompressionMethod.Gzip,
                BufferContent = false,
                // Emby would otherwise write the full URL (incl. password) to its log.
                LogRequest = false,
                LogErrors = false,
                ThrowOnErrorResponse = true
            };
            return await _http.Get(options).ConfigureAwait(false);
        }
    }
}
