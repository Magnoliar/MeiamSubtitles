using Emby.MeiamSub.Zimuku.Captcha;
using Emby.MeiamSub.Zimuku.Model;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Base;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.MeiamSub.Zimuku
{
    /// <summary>
    /// 字幕提供程序
    /// 负责从 Zimuku.org 搜索和下载字幕。
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2025-12-22</para>
    /// </summary>
    public class ZimukuProvider : ISubtitleProvider, IHasOrder
    {
        #region 变量声明
        public const string ASS = "ass";
        public const string SSA = "ssa";
        public const string SRT = "srt";

        private const string ZimukuBaseUrl = "https://zimuku.org";
        private const string DoubanSearchUrl = "https://search.douban.com/movie/subject_search";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        protected readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;
        private readonly IApplicationHost _applicationHost;
        private readonly ZimukuCaptchaSolver _captchaSolver;

        private Plugin MainPlugin { get; set; }

        // 匹配字幕页面链接 /subs/{id}.html
        private static readonly Regex SubPageRegex = new Regex(@"href=[""']/subs/(\d+)\.html[""']", RegexOptions.Compiled);

        // 匹配 CAPTCHA 验证码图片 (base64 BMP)
        private static readonly Regex CaptchaRegex = new Regex(@"class=""verifyimg""[^>]*src=""data:image/bmp;base64,([^""]+)""", RegexOptions.Compiled);

        // 匹配需要提交验证码的安全验证表单
        private static readonly Regex SecurityFormRegex = new Regex(@"action=""([^""]*security_verify[^""]*)""", RegexOptions.Compiled);

        // 匹配隐藏的 security_verify_data 字段
        private static readonly Regex SecurityDataRegex = new Regex(@"name=""security_verify_data""[^>]*value=""([^""]+)""", RegexOptions.Compiled);

        // 剧集匹配正则: S01E05, E05, EP05, 第5集, 第05集
        private static readonly Regex EpisodeRegex = new Regex(
            @"(?:S\d{1,2})?E(?:P)?(\d{1,4})|第(\d{1,4})集",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public int Order => 200;

        public string Name => "MeiamSub.Zimuku";

        /// <summary>
        /// 支持电影、剧集
        /// </summary>
        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };
        #endregion

        #region 构造函数
        public ZimukuProvider(ILogManager logManager, IJsonSerializer jsonSerializer, IHttpClient httpClient, IApplicationHost applicationHost)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            _applicationHost = applicationHost;
            MainPlugin = applicationHost.Plugins.OfType<Plugin>().FirstOrDefault();
            _captchaSolver = new ZimukuCaptchaSolver(_logger);

            _logger.Info("{0} Init", new object[1] { Name });
        }
        #endregion

        #region 查询字幕

        /// <summary>
        /// 搜索字幕 (ISubtitleProvider 接口实现)
        /// </summary>
        /// <param name="request">包含媒体路径、语言等信息的搜索请求对象</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>远程字幕信息列表</returns>
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.Info("{0} Search | SubtitleSearchRequest -> {1}", new object[2] { Name, _jsonSerializer.SerializeToString(request) });

            var subtitles = await SearchSubtitlesAsync(request, cancellationToken);

            return subtitles;
        }

        /// <summary>
        /// 查询字幕
        /// </summary>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (request == null)
                {
                    _logger.Info("{0} Search | Request is null", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var language = NormalizeLanguage(request.Language);
                var fileName = string.Empty;

                if (!string.IsNullOrEmpty(request.MediaPath))
                {
                    fileName = Path.GetFileName(request.MediaPath);
                }

                _logger.Info("{0} Search | Target -> {1} | Language -> {2}", new object[3] { Name, fileName, language });

                if (language != "chi")
                {
                    _logger.Info("{0} Search | Summary -> Language not supported, skip search.", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                // 1. 获取豆瓣 ID
                var doubanId = await ResolveDoubanIdAsync(request, cancellationToken);
                if (string.IsNullOrEmpty(doubanId))
                {
                    _logger.Info("{0} Search | Summary -> Could not resolve Douban ID, skip search.", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.Info("{0} Search | DoubanId -> {1}", new object[2] { Name, doubanId });

                // 2. 在 Zimuku 搜索
                var subtitlePageUrl = await SearchZimukuAsync(doubanId, cancellationToken);
                if (string.IsNullOrEmpty(subtitlePageUrl))
                {
                    _logger.Info("{0} Search | Summary -> No subtitle page found on Zimuku.", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.Info("{0} Search | SubtitlePage -> {1}", new object[2] { Name, subtitlePageUrl });

                // 3. 获取字幕列表
                var subtitleEntries = await GetSubtitleListAsync(subtitlePageUrl, request, cancellationToken);

                if (subtitleEntries == null || subtitleEntries.Count == 0)
                {
                    _logger.Info("{0} Search | Summary -> Get  0  Subtitles", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                // 4. 排序
                var remoteSubtitles = subtitleEntries
                    .OrderByDescending(e => e.LanguageTier)
                    .ThenByDescending(e => GetQualityScore(e.Info.Name))
                    .ThenByDescending(e => GetFormatPriority(e.Info.Format))
                    .Select(e => e.Info)
                    .ToList();

                // AI 智能筛选
                if (MainPlugin.Options.EnableAIFilter && !string.IsNullOrEmpty(MainPlugin.Options.AIApiKey) && remoteSubtitles.Count > 2)
                {
                    remoteSubtitles = await FilterSubtitlesWithAI(remoteSubtitles, fileName);
                }

                _logger.Info("{0} Search | Summary -> Get  {1}  Subtitles", new object[2] { Name, remoteSubtitles.Count });

                return remoteSubtitles;
            }
            catch (Exception ex)
            {
                _logger.Error("{0} Search | Exception -> [{1}] {2}", new object[3] { Name, ex.GetType().Name, ex.Message });
            }

            _logger.Info("{0} Search | Summary -> Get  0  Subtitles", new object[1] { Name });

            return Array.Empty<RemoteSubtitleInfo>();
        }

        /// <summary>
        /// 通过搜索请求解析豆瓣 ID
        /// </summary>
        private async Task<string> ResolveDoubanIdAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var searchTitle = string.Empty;

            string imdbId = null;
            request.ProviderIds?.TryGetValue("ImdbId", out imdbId);
            if (!string.IsNullOrEmpty(imdbId))
            {
                searchTitle = imdbId;
                _logger.Info("{0} Search | Using IMDB ID -> {1}", new object[2] { Name, searchTitle });
            }
            else if (!string.IsNullOrEmpty(request.Name))
            {
                searchTitle = request.Name;
                _logger.Info("{0} Search | Using title -> {1}", new object[2] { Name, searchTitle });
            }
            else if (!string.IsNullOrEmpty(request.MediaPath))
            {
                searchTitle = Path.GetFileNameWithoutExtension(request.MediaPath);
                _logger.Info("{0} Search | Using media path -> {1}", new object[2] { Name, searchTitle });
            }

            if (string.IsNullOrEmpty(searchTitle))
            {
                return null;
            }

            try
            {
                // 尝试直接通过 IMDB ID 在 Zimuku 搜索
                string imdbSearchId = null;
                request.ProviderIds?.TryGetValue("ImdbId", out imdbSearchId);
                if (!string.IsNullOrEmpty(imdbSearchId))
                {
                    var imdbSearchUrl = $"{ZimukuBaseUrl}/search?q={imdbSearchId}&chost=zimuku.org";
                    var imdbResponse = await FetchPageWithCaptchaAsync(imdbSearchUrl, cancellationToken);
                    if (imdbResponse != null && SubPageRegex.IsMatch(imdbResponse))
                    {
                        var match = SubPageRegex.Match(imdbResponse);
                        return $"{ZimukuBaseUrl}/subs/{match.Groups[1].Value}.html";
                    }
                }

                // 通过豆瓣搜索获取豆瓣 ID
                var encodedTitle = Uri.EscapeDataString(searchTitle);
                var doubanUrl = $"{DoubanSearchUrl}?search_text={encodedTitle}&cat=1002";

                _logger.Info("{0} Search | Douban search URL -> {1}", new object[2] { Name, doubanUrl });

                var options = new HttpRequestOptions
                {
                    Url = doubanUrl,
                    UserAgent = UserAgent,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                };

                var response = await _httpClient.GetResponse(options);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warn("{0} Search | Douban search failed -> {1}", new object[2] { Name, response.StatusCode });
                    return null;
                }

                var html = await ReadStreamAsync(response.Content);

                // 尝试从 window.__DATA__ 中提取豆瓣 ID
                var dataMatch = Regex.Match(html, @"window\.__DATA__\s*=\s*""([^""]+)""");
                if (dataMatch.Success)
                {
                    var dataStr = dataMatch.Groups[1].Value;
                    var idMatch = Regex.Match(dataStr, @"""id""\s*:\s*""(\d+)""");
                    if (idMatch.Success)
                    {
                        return idMatch.Groups[1].Value;
                    }
                }

                // 备用方案: 搜索结果页面直接提取豆瓣 subject ID
                var subjectMatch = Regex.Match(html, @"/subject/(\d+)/");
                if (subjectMatch.Success)
                {
                    return subjectMatch.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} Search | Douban resolve exception -> [{1}] {2}", new object[3] { Name, ex.GetType().Name, ex.Message });
            }

            return null;
        }

        /// <summary>
        /// 在 Zimuku 上通过 Douban ID 搜索字幕页面
        /// </summary>
        private async Task<string> SearchZimukuAsync(string doubanId, CancellationToken cancellationToken)
        {
            try
            {
                var searchUrl = $"{ZimukuBaseUrl}/search?q={doubanId}&chost=zimuku.org";

                var html = await FetchPageWithCaptchaAsync(searchUrl, cancellationToken);
                if (string.IsNullOrEmpty(html))
                {
                    return null;
                }

                // 查找字幕页面链接
                var match = SubPageRegex.Match(html);
                if (match.Success)
                {
                    return $"{ZimukuBaseUrl}/subs/{match.Groups[1].Value}.html";
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Error("{0} Search | Zimuku search exception -> [{1}] {2}", new object[3] { Name, ex.GetType().Name, ex.Message });
                return null;
            }
        }

        /// <summary>
        /// 获取字幕列表页面，解析每个字幕条目
        /// </summary>
        private async Task<List<(RemoteSubtitleInfo Info, int LanguageTier)>> GetSubtitleListAsync(
            string subtitlePageUrl, SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var results = new List<(RemoteSubtitleInfo Info, int LanguageTier)>();

            try
            {
                var html = await FetchPageWithCaptchaAsync(subtitlePageUrl, cancellationToken);

                if (string.IsNullOrEmpty(html))
                {
                    return results;
                }

                // 提取请求的集数 (如果是剧集)
                int? requestedEpisode = null;
                if (request.IndexNumber.HasValue)
                {
                    requestedEpisode = request.IndexNumber.Value;
                }
                else if (!string.IsNullOrEmpty(request.MediaPath))
                {
                    requestedEpisode = ExtractEpisodeNumber(request.MediaPath);
                }

                _logger.Info("{0} Search | Requested episode -> {1}", new object[2] { Name, requestedEpisode?.ToString() ?? "N/A" });

                // 解析字幕表格 div.subs.box.clearfix 中的每一行
                var subsDivMatch = Regex.Match(html, @"<div\s+class=""subs box clearfix"">([\s\S]*?)(?:</div>\s*</div>|<div\s+class=""[^""]+"">)", RegexOptions.IgnoreCase);
                if (!subsDivMatch.Success)
                {
                    // 尝试更宽松的匹配
                    subsDivMatch = Regex.Match(html, @"<div\s+class=""subs[\s\S]*?box[\s\S]*?clearfix"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
                }

                var tableHtml = subsDivMatch.Success ? subsDivMatch.Groups[1].Value : html;

                // 匹配每个 <tr> 行
                var rowMatches = Regex.Matches(tableHtml, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase);

                foreach (Match rowMatch in rowMatches)
                {
                    var rowHtml = rowMatch.Value;

                    try
                    {
                        // 跳过表头行
                        if (rowHtml.Contains("<th") || !rowHtml.Contains("<td"))
                            continue;

                        // 提取详情页链接
                        var linkMatch = Regex.Match(rowHtml, @"href=""([^""]*subs/[^""]*)""");
                        if (!linkMatch.Success)
                            continue;

                        var detailUrl = linkMatch.Groups[1].Value;
                        if (!detailUrl.StartsWith("http"))
                        {
                            detailUrl = ZimukuBaseUrl + detailUrl;
                        }

                        // 提取语言 (从 td.tac.lang 中的 img title 属性)
                        var langMatch = Regex.Match(rowHtml, @"class=""tac lang"">([\s\S]*?)</td>", RegexOptions.IgnoreCase);
                        string languageStr = "未知";
                        string langFlags = "";
                        if (langMatch.Success)
                        {
                            var imgMatches = Regex.Matches(langMatch.Groups[1].Value, @"title=""([^""]+)""");
                            var languages = imgMatches.Cast<Match>()
                                .Select(m => m.Groups[1].Value.Trim())
                                .Where(t => !string.IsNullOrEmpty(t))
                                .ToList();
                            langFlags = string.Join("+", languages);
                            languageStr = langFlags;
                        }

                        // 提取格式 (从 span.label-info)
                        var formatMatch = Regex.Match(rowHtml, @"<span\s+class=""label-info"">\s*([^<]+)\s*</span>", RegexOptions.IgnoreCase);
                        string format = formatMatch.Success ? formatMatch.Groups[1].Value.Trim().ToLower() : "srt";

                        // 提取评分
                        var ratingMatch = Regex.Match(rowHtml, @"<td\s+class=""tac rating"">\s*([\d.]+)", RegexOptions.IgnoreCase);
                        double rating = 0;
                        if (ratingMatch.Success)
                        {
                            double.TryParse(ratingMatch.Groups[1].Value, out rating);
                        }

                        // 提取字幕组
                        var teamMatch = Regex.Match(rowHtml, @"<td[^>]*>\s*<a[^>]*>([^<]+)</a>\s*</td>", RegexOptions.IgnoreCase);
                        string team = teamMatch.Success ? teamMatch.Groups[1].Value.Trim() : "";

                        // 集数过滤 (如果是剧集)
                        if (requestedEpisode.HasValue)
                        {
                            bool matchesEpisode = false;

                            // 检查标题/详情是否包含集数信息
                            var titleMatch = Regex.Match(rowHtml, @"<td[^>]*class=""[^""]*title[^""]*""[^>]*>([\s\S]*?)</td>", RegexOptions.IgnoreCase);
                            string titleText = titleMatch.Success ? titleMatch.Groups[1].Value : rowHtml;

                            // 匹配多种集数格式
                            var episodeMatches = EpisodeRegex.Matches(titleText);
                            if (episodeMatches.Count > 0)
                            {
                                foreach (Match epMatch in episodeMatches)
                                {
                                    string epStr = epMatch.Groups[1].Success ? epMatch.Groups[1].Value : epMatch.Groups[2].Value;
                                    if (int.TryParse(epStr, out int epNum) && epNum == requestedEpisode.Value)
                                    {
                                        matchesEpisode = true;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                // 如果没有明确的集数标记，检查是否包含关键字 "全集" 或 "全" (表示包含所有集)
                                if (titleText.Contains("全") || titleText.Contains("ALL") || titleText.Contains("Complete"))
                                {
                                    matchesEpisode = true;
                                }
                            }

                            if (!matchesEpisode)
                            {
                                continue;
                            }
                        }

                        // 计算语言优先级
                        int languageTier = GetLanguageTier(langFlags);

                        // 构建字幕 ID (Base64 编码的 JSON)
                        var downloadInfo = new DownloadSubInfo
                        {
                            DetailUrl = detailUrl,
                            Language = languageStr,
                            TwoLetterISOLanguageName = request.Language,
                            Format = ExtractFormat(format),
                            Name = $"[MEIAMSUB] {langFlags} | {team} | Zimuku"
                        };

                        var info = new RemoteSubtitleInfo
                        {
                            Id = Base64Encode(_jsonSerializer.SerializeToString(downloadInfo)),
                            Name = downloadInfo.Name,
                            Author = team,
                            ProviderName = Name,
                            Format = downloadInfo.Format,
                            Comment = $"Rating: {rating} | Format: {format} | Team: {team}",
                            IsHashMatch = false,
                        };

                        results.Add((info, languageTier));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("{0} Search | Parse row exception -> {1}", new object[2] { Name, ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} Search | GetSubtitleList exception -> [{1}] {2}", new object[3] { Name, ex.GetType().Name, ex.Message });
            }

            return results;
        }

        #endregion

        #region 下载字幕

        /// <summary>
        /// 获取字幕内容 (ISubtitleProvider 接口实现)
        /// </summary>
        /// <param name="id">字幕唯一标识符 (Base64 编码的 JSON 数据)</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>包含字幕流的响应对象</returns>
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            _logger.Info("{0} DownloadSub | Request -> {1}", new object[2] { Name, id });

            return await DownloadSubAsync(id, cancellationToken);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        private async Task<SubtitleResponse> DownloadSubAsync(string info, CancellationToken cancellationToken)
        {
            try
            {
                var downloadSub = _jsonSerializer.DeserializeFromString<DownloadSubInfo>(Base64Decode(info));

                if (downloadSub == null)
                {
                    return new SubtitleResponse();
                }

                _logger.Info("{0} DownloadSub | DetailUrl -> {1} | Language -> {2}", new object[3] { Name, downloadSub.DetailUrl, downloadSub.Language });

                // 1. 访问详情页面
                var detailHtml = await FetchPageWithCaptchaAsync(downloadSub.DetailUrl, cancellationToken);
                if (string.IsNullOrEmpty(detailHtml))
                {
                    _logger.Warn("{0} DownloadSub | Failed to load detail page", new object[1] { Name });
                    return new SubtitleResponse();
                }

                // 2. 查找下载链接 (li.dlsub)
                var dlsubMatch = Regex.Match(detailHtml, @"<li\s+class=""dlsub"">\s*<a\s+href=""([^""]+)""", RegexOptions.IgnoreCase);
                if (!dlsubMatch.Success)
                {
                    // 备用匹配
                    dlsubMatch = Regex.Match(detailHtml, @"href=""([^""]*download[^""]*)""", RegexOptions.IgnoreCase);
                }

                if (!dlsubMatch.Success)
                {
                    _logger.Warn("{0} DownloadSub | No download link found", new object[1] { Name });
                    return new SubtitleResponse();
                }

                var downloadUrl = dlsubMatch.Groups[1].Value;
                if (!downloadUrl.StartsWith("http"))
                {
                    downloadUrl = ZimukuBaseUrl + downloadUrl;
                }

                _logger.Info("{0} DownloadSub | DownloadUrl -> {1}", new object[2] { Name, downloadUrl });

                // 3. 访问下载页面，获取实际文件链接
                var downloadPageHtml = await FetchPageWithCaptchaAsync(downloadUrl, cancellationToken);
                if (string.IsNullOrEmpty(downloadPageHtml))
                {
                    _logger.Warn("{0} DownloadSub | Failed to load download page", new object[1] { Name });
                    return new SubtitleResponse();
                }

                // 4. 查找下载链接 (div.clearfix 中的 <a> 标签)
                var clearfixMatch = Regex.Match(downloadPageHtml, @"<div\s+class=""clearfix"">([\s\S]*?)</div>", RegexOptions.IgnoreCase);
                var linkSection = clearfixMatch.Success ? clearfixMatch.Groups[1].Value : downloadPageHtml;
                var linkMatches = Regex.Matches(linkSection, @"<a\s+href=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase);
                var candidateUrls = new List<string>();

                foreach (Match linkMatch in linkMatches)
                {
                    var url = linkMatch.Groups[1].Value;
                    if (url.StartsWith("http") && !url.Contains("javascript") && !url.Contains("#"))
                    {
                        candidateUrls.Add(url);
                    }
                    else if (url.StartsWith("/"))
                    {
                        candidateUrls.Add(ZimukuBaseUrl + url);
                    }
                }

                // 5. 尝试每个链接，取第一个有效响应 (>1024 bytes)
                foreach (var candidateUrl in candidateUrls)
                {
                    try
                    {
                        _logger.Info("{0} DownloadSub | Trying URL -> {1}", new object[2] { Name, candidateUrl });

                        var downloadOptions = new HttpRequestOptions
                        {
                            Url = candidateUrl,
                            UserAgent = UserAgent,
                            TimeoutMs = 30000,
                            AcceptHeader = "*/*",
                        };

                        var response = await _httpClient.GetResponse(downloadOptions);

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // Read the full stream to check size
                            var memoryStream = new MemoryStream();
                            await response.Content.CopyToAsync(memoryStream);
                            memoryStream.Position = 0;

                            if (memoryStream.Length > 1024)
                            {
                                _logger.Info("{0} DownloadSub | Success -> {1} (size: {2})", new object[3] { Name, candidateUrl, memoryStream.Length });

                                return new SubtitleResponse
                                {
                                    Language = downloadSub.Language,
                                    IsForced = false,
                                    Format = downloadSub.Format,
                                    Stream = memoryStream,
                                };
                            }

                            memoryStream.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("{0} DownloadSub | Try {1} failed -> {2}", new object[3] { Name, candidateUrl, ex.Message });
                    }
                }

                _logger.Warn("{0} DownloadSub | No valid subtitle file found among {1} candidates", new object[2] { Name, candidateUrls.Count });
            }
            catch (Exception ex)
            {
                _logger.Error("{0} DownloadSub | Exception -> [{1}] {2}", new object[3] { Name, ex.GetType().Name, ex.Message });
            }

            return new SubtitleResponse();
        }

        #endregion

        #region CAPTCHA 处理

        /// <summary>
        /// 带验证码处理的页面获取
        /// 如果页面包含验证码，自动识别并提交，最多重试 3 次
        /// </summary>
        private async Task<string> FetchPageWithCaptchaAsync(string url, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var options = new HttpRequestOptions
                    {
                        Url = url,
                        UserAgent = UserAgent,
                        TimeoutMs = 30000,
                        AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    };

                    var response = await _httpClient.GetResponse(options);
                    var html = await ReadStreamAsync(response.Content);

                    // 检查是否包含验证码
                    var captchaMatch = CaptchaRegex.Match(html);
                    if (!captchaMatch.Success)
                    {
                        return html;
                    }

                    _logger.Info("{0} Captcha | Detected CAPTCHA (attempt {1}/3)", new object[2] { Name, attempt + 1 });

                    // 解码 base64 BMP
                    var bmpBase64 = captchaMatch.Groups[1].Value;
                    var bmpData = Convert.FromBase64String(bmpBase64);

                    // 解决验证码
                    var captchaSolution = _captchaSolver.Solve(bmpData);
                    if (string.IsNullOrEmpty(captchaSolution))
                    {
                        _logger.Warn("{0} Captcha | Failed to solve CAPTCHA", new object[1] { Name });
                        return null;
                    }

                    // 转换为 hex 编码
                    var hexSolution = ZimukuCaptchaSolver.ToHexEncoded(captchaSolution);

                    // 构建提交 URL
                    var separator = url.Contains("?") ? "&" : "?";
                    var submitUrl = $"{url}{separator}security_verify_img={hexSolution}";

                    _logger.Info("{0} Captcha | Submitting solution -> {1} (hex: {2})", new object[3] { Name, captchaSolution, hexSolution });

                    // 提交验证码
                    var submitOptions = new HttpRequestOptions
                    {
                        Url = submitUrl,
                        UserAgent = UserAgent,
                        TimeoutMs = 30000,
                        AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    };

                    var submitResponse = await _httpClient.GetResponse(submitOptions);
                    var submitHtml = await ReadStreamAsync(submitResponse.Content);

                    // 检查是否还有验证码
                    if (!CaptchaRegex.Match(submitHtml).Success)
                    {
                        return submitHtml;
                    }

                    _logger.Warn("{0} Captcha | Still has CAPTCHA after submit, retrying...", new object[1] { Name });
                }
                catch (Exception ex)
                {
                    _logger.Error("{0} Captcha | Fetch exception (attempt {1}) -> [{2}] {3}", new object[4] { Name, attempt + 1, ex.GetType().Name, ex.Message });
                }
            }

            _logger.Warn("{0} Captcha | Failed to bypass CAPTCHA after 3 attempts", new object[1] { Name });
            return null;
        }

        #endregion

        #region 内部方法

        /// <summary>
        /// 从流中读取全部内容为字符串
        /// </summary>
        private static async Task<string> ReadStreamAsync(Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// Base64 加密
        /// </summary>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// Base64 解密
        /// </summary>
        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        /// <summary>
        /// 字幕质量关键词评分：特效(5) > 精校(4) > 官方(3) > 简中(2) > 中文(1)
        /// </summary>
        private static int GetQualityScore(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (name.Contains("特效", StringComparison.OrdinalIgnoreCase)) return 5;
            if (name.Contains("精校", StringComparison.OrdinalIgnoreCase)) return 4;
            if (name.Contains("官方", StringComparison.OrdinalIgnoreCase)) return 3;
            if (name.Contains("简中", StringComparison.OrdinalIgnoreCase)) return 2;
            if (name.Contains("中文", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// 格式优先级: ass/ssa > srt > 其他
        /// </summary>
        private static int GetFormatPriority(string format)
        {
            if (string.IsNullOrEmpty(format)) return 0;
            var f = format.ToLower();
            if (f.Contains(ASS) || f.Contains(SSA)) return 2;
            if (f.Contains(SRT)) return 1;
            return 0;
        }

        /// <summary>
        /// 提取格式化字幕类型
        /// </summary>
        protected string ExtractFormat(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            text = text.ToLower();
            if (text.Contains(ASS)) return ASS;
            if (text.Contains(SSA)) return SSA;
            if (text.Contains(SRT)) return SRT;

            return text;
        }

        /// <summary>
        /// 语言优先级排序: 简中+双语(7) > 简中(6) > 繁中+双语(5) > 繁中(4) > 英文(3) > 其他(0)
        /// </summary>
        private static int GetLanguageTier(string langFlags)
        {
            if (string.IsNullOrEmpty(langFlags)) return 0;

            var hasSimplified = langFlags.Contains("简中") || langFlags.Contains("简");
            var hasTraditional = langFlags.Contains("繁中") || langFlags.Contains("繁");
            var hasBilingual = langFlags.Contains("双语") || langFlags.Contains("+");
            var hasEnglish = langFlags.Contains("英");

            if (hasSimplified && hasBilingual) return 7;
            if (hasSimplified) return 6;
            if (hasTraditional && hasBilingual) return 5;
            if (hasTraditional) return 4;
            if (hasEnglish) return 3;

            return 0;
        }

        /// <summary>
        /// 规范化语言代码
        /// </summary>
        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language)) return language;

            if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("zho", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("chi", StringComparison.OrdinalIgnoreCase))
            {
                return "chi";
            }
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("eng", StringComparison.OrdinalIgnoreCase))
            {
                return "eng";
            }
            return language;
        }

        /// <summary>
        /// 从文件名中提取集数
        /// </summary>
        private static int? ExtractEpisodeNumber(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            var match = EpisodeRegex.Match(fileName);
            if (match.Success)
            {
                string epStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (int.TryParse(epStr, out int epNum))
                {
                    return epNum;
                }
            }

            return null;
        }

        /// <summary>
        /// 使用 AI 筛选字幕，返回推荐序列
        /// </summary>
        private async Task<List<RemoteSubtitleInfo>> FilterSubtitlesWithAI(List<RemoteSubtitleInfo> candidates, string videoName)
        {
            try
            {
                var topCandidates = candidates.Take(5).ToList();
                var candidateList = string.Join("\n", topCandidates.Select((s, i) => $"{i + 1}. {s.Name}"));

                var systemPrompt = @"你是字幕筛选助手。根据视频文件名，对候选字幕列表进行排序。
规则：
1. 输出所有序号，用英文逗号分隔，从最佳到最差
2. 不要输出任何其他文字
3. 优先选择名称与视频文件最相似的字幕
4. 质量关键词优先级：特效 > 精校 > 官方 > 简中 > 中文
5. ASS 格式优于 SRT
示例输出：3,1,5,2,4";
                var userMessage = $"视频文件: {videoName}\n候选字幕:\n{candidateList}";

                var jsonBody = _jsonSerializer.SerializeToString(new
                {
                    model = MainPlugin.Options.AIModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    stream = false
                });

                var endpoint = string.IsNullOrEmpty(MainPlugin.Options.AIEndpoint)
                    ? "https://tokenhub.tencentmaas.com/v1/chat/completions"
                    : MainPlugin.Options.AIEndpoint;

                var request = WebRequest.Create(endpoint) as HttpWebRequest;
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Headers.Add("Authorization", $"Bearer {MainPlugin.Options.AIApiKey}");
                request.Timeout = (MainPlugin.Options.AITimeout > 0 ? MainPlugin.Options.AITimeout : 12) * 1000;
                var bytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bytes.Length;

                using (var reqStream = await request.GetRequestStreamAsync())
                {
                    await reqStream.WriteAsync(bytes, 0, bytes.Length);
                }

                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var responseBody = await reader.ReadToEndAsync();
                    _logger.Info("{0} AI Filter | Response -> {1}", new object[2] { Name, responseBody });

                    var responseObj = _jsonSerializer.DeserializeFromString<AIResponse>(responseBody);
                    if (responseObj?.Choices?.Length > 0)
                    {
                        var aiReply = responseObj.Choices[0].Message.Content.Trim();
                        var indices = aiReply
                            .Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Select(s => int.TryParse(s, out var n) ? n : 0)
                            .Where(n => n >= 1 && n <= topCandidates.Count)
                            .Distinct()
                            .ToList();

                        if (indices.Count >= topCandidates.Count / 2)
                        {
                            var reordered = indices.Select(i => topCandidates[i - 1]).ToList();
                            reordered.AddRange(topCandidates.Where(s => !reordered.Contains(s)));
                            reordered.AddRange(candidates.Skip(topCandidates.Count));
                            _logger.Info("{0} AI Filter | Sequence -> {1}", new object[2] { Name, string.Join(",", indices) });
                            return reordered;
                        }
                        else
                        {
                            _logger.Info("{0} AI Filter | Invalid sequence, fallback to sort. Reply: {1}", new object[2] { Name, aiReply });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} AI Filter | Exception -> [{1}] {2} (fallback to sort)", new object[3] { Name, ex.GetType().Name, ex.Message });
            }

            return candidates;
        }

        #endregion
    }
}
