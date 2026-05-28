using MediaBrowser.Model.Plugins;

namespace Jellyfin.MeiamSub.Thunder.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnableAIFilter { get; set; }

        public string AIApiKey { get; set; }

        public string AIModel { get; set; } = "deepseek-v4-flash";
    }
}
