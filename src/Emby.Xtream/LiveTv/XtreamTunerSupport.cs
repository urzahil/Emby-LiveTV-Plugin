using System;
using Emby.Xtream.Configuration;
using Emby.Xtream.Xtream;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream.LiveTv
{
    /// <summary>Shared helpers for turning a TunerHostInfo into options, credentials and a client.</summary>
    internal static class XtreamTunerSupport
    {
        public static PluginConfiguration PluginConfig => Plugin.Instance != null ? Plugin.Instance.Configuration : new PluginConfiguration();

        public static XtreamTunerOptions GetOptions(IJsonSerializer json, TunerHostInfo tuner)
        {
            if (string.IsNullOrEmpty(tuner.ProviderOptions)) return new XtreamTunerOptions();
            XtreamTunerOptions options = null;
            try { options = json.DeserializeFromString<XtreamTunerOptions>(tuner.ProviderOptions); }
            catch (Exception) { /* corrupt options: fall back to defaults */ }
            return options ?? new XtreamTunerOptions();
        }

        public static XtreamCredentials GetCredentials(TunerHostInfo tuner, XtreamTunerOptions options) =>
            new XtreamCredentials(tuner.Url, options.Username, options.Password);

        public static string GetUserAgent(TunerHostInfo tuner) =>
            string.IsNullOrWhiteSpace(tuner.UserAgent) ? XtreamDefaults.UserAgent : tuner.UserAgent;

        public static XtreamClient CreateClient(IHttpClient http, TunerHostInfo tuner, XtreamTunerOptions options)
        {
            var creds = GetCredentials(tuner, options);
            return new XtreamClient(new EmbyHttp(http, GetUserAgent(tuner), PluginConfig.HttpTimeoutSeconds), creds);
        }

        public static string StreamExtension(XtreamTunerOptions options) =>
            string.Equals(options.StreamFormat, "m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
    }
}
