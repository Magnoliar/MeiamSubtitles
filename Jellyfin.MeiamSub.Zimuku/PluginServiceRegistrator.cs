using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jellyfin.MeiamSub.Zimuku
{
    /// <summary>
    /// 插件服务注册器
    /// 负责注册插件所需的依赖服务，如 HTTP 客户端和字幕提供程序。
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2025-12-22</para>
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// 注册服务
        /// </summary>
        /// <param name="serviceCollection">服务集合</param>
        /// <param name="applicationHos">应用程序宿主</param>
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHos)
        {
            serviceCollection.AddHttpClient("MeiamSub.Zimuku", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                client.DefaultRequestHeaders.Add("Referer", "https://zimuku.org/");
            });

            serviceCollection.AddSingleton<ISubtitleProvider, ZimukuProvider>();
        }
    }
}
