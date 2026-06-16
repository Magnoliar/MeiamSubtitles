using Emby.Web.GenericEdit;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using System;
using System.ComponentModel;
using System.IO;

namespace Emby.MeiamSub.Zimuku
{
    /// <summary>
    /// 插件入口
    /// </summary>
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage
    {
        public Plugin(IApplicationPaths applicationPaths, IApplicationHost applicationHost) : base(applicationHost)
        {
            Instance = this;
        }

        /// <summary>
        /// 插件ID
        /// </summary>
        public override Guid Id => new Guid("B2C3D4E5-F6A7-8901-BCDE-F12345678901");

        /// <summary>
        /// 插件名称
        /// </summary>
        public override string Name => "MeiamSub.Zimuku";

        /// <summary>
        /// 插件描述
        /// </summary>
        public override string Description => "Download subtitles from Zimuku.org";

        /// <summary>
        /// 缩略图格式化类型
        /// </summary>
        public ImageFormat ThumbImageFormat => ImageFormat.Gif;

        /// <summary>
        /// 获取插件选项
        /// </summary>
        public PluginConfiguration Options => this.GetOptions();

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// 获取插件缩略图资源流
        /// </summary>
        public Stream GetThumbImage()
        {
            var type = GetType();
            var resourceName = $"{type.Namespace}.Thumb.png";
            var stream = type.Assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                return null;
            }

            return stream;
        }
    }

    /// <summary>
    /// 插件配置类
    /// </summary>
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "MeiamSub Zimuku Options";

        [Description("启用 AI 智能筛选字幕（需要 API Key）")]
        public bool EnableAIFilter { get; set; }

        [Description("AI API Key")]
        public string AIApiKey { get; set; }

        [Description("AI 模型名称")]
        public string AIModel { get; set; }

        [Description("AI API 端点 (OpenAI 兼容格式)")]
        public string AIEndpoint { get; set; }

        [Description("AI 请求超时 (秒)")]
        public int AITimeout { get; set; }

        public PluginConfiguration()
        {
            EnableAIFilter = false;
            AIModel = "deepseek-v4-flash";
            AIEndpoint = "https://tokenhub.tencentmaas.com/v1/chat/completions";
            AITimeout = 12;
        }
    }
}
