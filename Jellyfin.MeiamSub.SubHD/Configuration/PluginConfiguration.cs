using MediaBrowser.Model.Plugins;

namespace Jellyfin.MeiamSub.SubHD.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// 手动指定 Douban ID（可选，覆盖自动搜索）
        /// </summary>
        public string DoubanId { get; set; }
    }
}
