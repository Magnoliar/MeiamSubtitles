# MeiamSubtitles

**MeiamSubtitles** 是一款专为 **Emby** 和 **Jellyfin** 媒体服务器打造的中文字幕下载插件，集成了射手网、迅雷看看、SubHD、Zimuku 四大字幕源，支持视频哈希匹配与豆瓣元数据精确搜索。

---

## 核心特性

- **多字幕源**: 射手网（Hash 匹配）、迅雷看看（CID 匹配）、SubHD（豆瓣 ID 搜索 + SVG 验证码自动识别）、Zimuku（豆瓣 ID 搜索 + BMP 验证码 OCR）
- **豆瓣元数据解析**: 通过 search.douban.com 解析影片豆瓣 ID，大幅提升中文影片匹配准确率
- **剧集单集过滤**: 支持 S01E05/E05/EP05/第5集 等多种格式的单集字幕精准匹配
- **智能排序**: 语言优先级（简中+双语 > 简中 > 繁中 > 英文）+ 质量标签（官方 > 精校 > 原创 > AI > 机翻）
- **AI 智能筛选**: 可选接入 OpenAI 兼容 API 对候选字幕进行智能重排
- **异步 I/O**: 核心算法全面采用 Async/Await 模式，不阻塞服务器线程
- **GBK 编码修复**: 自动修复中文字幕 ZIP 包中的 GBK 编码文件名

## 项目组件

| 组件 | 平台 | 框架 | 说明 |
|------|------|------|------|
| **Emby.MeiamSub.Shooter** | Emby | .NET Standard 2.1 | 射手网字幕源 |
| **Emby.MeiamSub.Thunder** | Emby | .NET Standard 2.1 | 迅雷看看字幕源 |
| **Emby.MeiamSub.SubHD** | Emby | .NET Standard 2.1 | SubHD 字幕源（含验证码求解） |
| **Emby.MeiamSub.Zimuku** | Emby | .NET Standard 2.1 | Zimuku 字幕源（含验证码 OCR） |
| **Jellyfin.MeiamSub.Shooter** | Jellyfin | .NET 9.0 | 射手网字幕源 |
| **Jellyfin.MeiamSub.Thunder** | Jellyfin | .NET 9.0 | 迅雷看看字幕源 |
| **Jellyfin.MeiamSub.SubHD** | Jellyfin | .NET 9.0 | SubHD 字幕源（含验证码求解） |
| **Jellyfin.MeiamSub.Zimuku** | Jellyfin | .NET 9.0 | Zimuku 字幕源（含验证码 OCR） |
| **Emby.MeiamSub.DevTool** | 开发调试 | .NET 8.0 | 哈希算法测试工具 |

## 快速安装

前往 [GitHub Releases](https://github.com/Magnoliar/MeiamSubtitles/releases) 下载最新版本。

### Jellyfin 存储库安装

1. 控制台 -> **插件** -> **存储库** -> 添加
2. 输入 URL: `https://github.com/Magnoliar/MeiamSubtitles/releases/download/latest/manifest-stable.json`
3. 在目录中安装插件，重启服务

### 手动安装

将 `.dll` 文件（Jellyfin 用户解压完整目录）放入 `plugins` 文件夹：

- **Windows**: `AppData\Local\jellyfin\plugins` 或 `Emby-Server\programdata\plugins`
- **Linux/Docker**: `/config/plugins` 或 `/var/lib/emby/plugins`

## 常见问题

**Q: Jellyfin 10.11+ 搜不到字幕？**
A: 新版使用三位字母语言代码（如 `zho`），请确保使用 v1.0.13.0+ 版本。

**Q: SubHD/Zimuku 需要登录吗？**
A: 不需要。插件通过验证码自动识别完成下载，无需账号。

**Q: 安装后会影响 OpenSubtitles 吗？**
A: 不会。本插件优先级 (Order) 设为 100，确保官方插件优先。

## 许可

Apache License 2.0
