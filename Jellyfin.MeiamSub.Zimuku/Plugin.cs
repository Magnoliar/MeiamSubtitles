using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using System;

namespace Jellyfin.MeiamSub.Zimuku
{
    /// <summary>
    /// 插件入口
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2025-12-22</para>
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>
    {
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


        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }
    }
}
