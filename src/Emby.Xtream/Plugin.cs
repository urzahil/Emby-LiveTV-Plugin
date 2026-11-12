using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Xtream.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IHasTranslations
    {
        public const string StaticName = "Xtream IPTV";

        private static readonly Guid PluginId = new Guid("c5f61da7-48c1-48bd-a2d5-7734a333b6b7");

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>Raised after the plugin settings were saved from the dashboard.</summary>
        public static event EventHandler ConfigurationSaved;

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            ConfigurationSaved?.Invoke(this, EventArgs.Empty);
        }

        public override string Name => StaticName;

        public override string Description => "Xtream Codes IPTV provider as a Live TV tuner with guide data and catch-up.";

        public override Guid Id => PluginId;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            string ns = GetType().Namespace;
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "xtreamtuner",
                    EmbeddedResourcePath = ns + ".Web.xtreamtuner.html",
                    IsMainConfigPage = false
                },
                new PluginPageInfo
                {
                    Name = "xtreamtunerjs",
                    EmbeddedResourcePath = ns + ".Web.xtreamtuner.js"
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfig",
                    DisplayName = StaticName,
                    EmbeddedResourcePath = ns + ".Web.config.html",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfigjs",
                    EmbeddedResourcePath = ns + ".Web.config.js"
                }
            };
        }

        public TranslationInfo[] GetTranslations()
        {
            string basePath = GetType().Namespace + ".strings.";
            return GetType().Assembly.GetManifestResourceNames()
                .Where(i => i.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                .Select(i => new TranslationInfo
                {
                    Locale = Path.GetFileNameWithoutExtension(i.Substring(basePath.Length)),
                    EmbeddedResourcePath = i
                }).ToArray();
        }

        public Stream GetThumbImage()
        {
            Type type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }
    }
}
