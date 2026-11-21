using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Catchup;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Xtream.Api
{
    [Route("/Xtream/Catchup/{Token}", "GET", IsHidden = true, Summary = "Catch-up stream for Emby's own ffmpeg (loopback only)")]
    public class GetCatchupStream
    {
        public string Token { get; set; }
    }

    /// <summary>
    /// Serves a catch-up programme to Emby's own ffmpeg/ffprobe, starting at the first video headers
    /// (<see cref="TsHeaderGate"/>). Not authenticated, because ffmpeg has no Emby token; instead it answers only
    /// requests from the server itself, and only for a random token that <see cref="CatchupStreamRegistry"/> issued.
    /// </summary>
    public class CatchupStreamService : IService, IRequiresRequest
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClient _http;
        private readonly ILogger _logger;

        public CatchupStreamService(IHttpClient httpClient, ILogManager logManager)
        {
            _http = httpClient;
            _logger = logManager.GetLogger("XtreamCatchup");
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetCatchupStream request)
        {
            IPAddress remote = Request?.RemoteIp;
            if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            CatchupStreamRegistry.Entry entry;
            // the same answer for both cases, so the endpoint does not reveal which tokens exist
            if (remote == null || !IPAddress.IsLoopback(remote) || !CatchupStreamRegistry.TryResolve(request.Token, DateTime.UtcNow, out entry))
                throw new ResourceNotFoundException();

            var options = new HttpRequestOptions
            {
                Url = entry.Url,
                CancellationToken = Request.CancellationToken,
                UserAgent = string.IsNullOrWhiteSpace(entry.UserAgent) ? XtreamDefaults.UserAgent : entry.UserAgent,
                TimeoutMs = (int)ConnectTimeout.TotalMilliseconds,
                EnableHttpCompression = false,
                BufferContent = false,
                // the URL holds the provider password
                LogRequest = false,
                LogErrors = false,
                ThrowOnErrorResponse = true
            };

            System.IO.Stream provider;
            try
            {
                provider = await _http.Get(options).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                string message = XtreamUrlBuilder.SanitizeText(ex.Message);
                _logger.Error("Catch-up stream could not be opened: {0}", message);
                throw new InvalidOperationException("Catch-up stream could not be opened: " + message);
            }

            return new TsResponseStream(provider, new TsHeaderGate(), gate =>
                _logger.Info("Catch-up stream starts after skipping {0} KB ({1})", gate.SkippedBytes / 1024, gate.OpenReason));
        }

        private sealed class TsResponseStream : GatedStream, IHasHeaders
        {
            public TsResponseStream(System.IO.Stream inner, TsHeaderGate gate, Action<TsHeaderGate> onOpen) : base(inner, gate, onOpen) { }

            public IDictionary<string, string> Headers { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "video/mp2t" };
        }
    }
}
