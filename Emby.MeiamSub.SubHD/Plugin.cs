using Emby.Web.GenericEdit;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.ComponentModel;
using System.IO;

namespace Emby.MeiamSub.SubHD
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
        public override Guid Id => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        /// <summary>
        /// 插件名称
        /// </summary>
        public override string Name => "MeiamSub.SubHD";

        /// <summary>
        /// 插件描述
        /// </summary>
        public override string Description => "Download subtitles from SubHD.tv";

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
        public override string EditorTitle => "MeiamSub SubHD Options";

        [Description("手动指定 Douban ID（可选，覆盖自动搜索）")]
        public string DoubanId { get; set; }
    }
}
