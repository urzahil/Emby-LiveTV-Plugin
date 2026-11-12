using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.LiveTv;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Xtream.Api
{
    [Route("/Xtream/Test", "GET", Summary = "Tests an Xtream Codes server and credentials")]
    [Authenticated(Roles = "Admin")]
    public class TestXtream : IReturn<TestXtreamResult>
    {
        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string UserAgent { get; set; }
    }

    [Route("/Xtream/Categories", "GET", Summary = "Lists the live categories of an Xtream Codes server")]
    [Authenticated(Roles = "Admin")]
    public class GetXtreamCategories : IReturn<List<XtreamCategoryDto>>
    {
        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string UserAgent { get; set; }
    }

    public class TestXtreamResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Status { get; set; }
        public string ExpiryDate { get; set; }
        public int MaxConnections { get; set; }
        public int ActiveConnections { get; set; }
        public string ServerTimezone { get; set; }
        public string[] AllowedOutputFormats { get; set; }
        /// <summary>Normalised server URL (a pasted get.php link is split into URL + credentials).</summary>
        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class XtreamCategoryDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>Helper endpoints for the tuner setup page. Admin only.</summary>
    public class XtreamService : IService
    {
        private readonly IHttpClient _http;
        private readonly ILogger _logger;

        public XtreamService(IHttpClient httpClient, ILogManager logManager)
        {
            _http = httpClient;
            _logger = logManager.GetLogger("XtreamService");
        }

        public async Task<object> Get(TestXtream request)
        {
            XtreamCredentials creds = ResolveCredentials(request.Url, request.Username, request.Password);
            var result = new TestXtreamResult { Url = creds.BaseUrl, Username = creds.Username, Password = creds.Password };

            if (!creds.IsComplete)
            {
                result.Message = "Enter the server URL, username and password.";
                return result;
            }

            try
            {
                var client = CreateClient(creds, request.UserAgent);
                var account = await client.GetAccountAsync(CancellationToken.None).ConfigureAwait(false);
                string problem = XtreamClient.GetAccountProblem(account, DateTime.UtcNow);

                result.Status = account.UserInfo.Status;
                result.MaxConnections = account.UserInfo.MaxConnections;
                result.ActiveConnections = account.UserInfo.ActiveConnections;
                result.ServerTimezone = account.ServerInfo.Timezone;
                result.AllowedOutputFormats = account.UserInfo.AllowedOutputFormats.ToArray();
                result.ExpiryDate = account.UserInfo.ExpDate.HasValue
                    ? XtreamClock.FromUnix(account.UserInfo.ExpDate.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;

                if (problem != null)
                {
                    result.Message = problem;
                    _logger.Info("Xtream test failed for {0}: {1}", XtreamUrlBuilder.Sanitize(creds.BaseUrl), problem);
                    return result;
                }

                result.Success = true;
                result.Message = "Connected.";
            }
            catch (Exception ex)
            {
                string msg = XtreamUrlBuilder.SanitizeText(ex.Message);
                _logger.Info("Xtream test failed for {0}: {1}", XtreamUrlBuilder.Sanitize(creds.BaseUrl), msg);
                result.Message = msg;
            }
            return result;
        }

        public async Task<object> Get(GetXtreamCategories request)
        {
            XtreamCredentials creds = ResolveCredentials(request.Url, request.Username, request.Password);
            if (!creds.IsComplete) return new List<XtreamCategoryDto>();

            try
            {
                var client = CreateClient(creds, request.UserAgent);
                var categories = await client.GetLiveCategoriesAsync(CancellationToken.None).ConfigureAwait(false);
                return categories
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(c => new XtreamCategoryDto { Id = c.Id, Name = c.Name })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Info("Xtream categories request failed for {0}: {1}", XtreamUrlBuilder.Sanitize(creds.BaseUrl), XtreamUrlBuilder.SanitizeText(ex.Message));
                return new List<XtreamCategoryDto>();
            }
        }

        private static XtreamCredentials ResolveCredentials(string url, string username, string password)
        {
            // a pasted get.php link carries the credentials itself
            var parsed = XtreamCredentials.ParseM3uUrl(url);
            if (parsed != null && string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password)) return parsed;
            return new XtreamCredentials(url, username, password);
        }

        private XtreamClient CreateClient(XtreamCredentials creds, string userAgent)
        {
            return new XtreamClient(new EmbyHttp(_http, userAgent, XtreamTunerSupport.PluginConfig.HttpTimeoutSeconds), creds);
        }
    }
}
