using Jellyfin.MeiamSub.Zimuku.Captcha;
using Jellyfin.MeiamSub.Zimuku.Model;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.MeiamSub.Zimuku
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

        private readonly ILogger<ZimukuProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ZimukuCaptchaSolver _captchaSolver;

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
        public ZimukuProvider(ILogger<ZimukuProvider> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _captchaSolver = new ZimukuCaptchaSolver(logger);
            _logger.LogInformation($"{Name} Init");
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
            _logger.LogInformation("DEBUG: Received Search request for " + (request?.MediaPath ?? "NULL"));

            var subtitles = await SearchSubtitlesAsync(request, cancellationToken);

            return subtitles;
        }

        /// <summary>
        /// 查询字幕
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("DEBUG: Entering SearchSubtitlesAsync (Zimuku)");

            try
            {
                if (request == null)
                {
                    _logger.LogInformation("DEBUG: Request is null");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var language = NormalizeLanguage(request.Language);
                var fileName = string.Empty;

                if (!string.IsNullOrEmpty(request.MediaPath))
                {
                    fileName = Path.GetFileName(request.MediaPath);
                }

                _logger.LogInformation(Name + " Search | Target -> " + fileName + " | Language -> " + language);

                if (language != "chi")
                {
                    _logger.LogInformation(Name + " Search | Summary -> Language not supported, skip search.");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                // 1. 获取豆瓣 ID
                var doubanId = await ResolveDoubanIdAsync(request, cancellationToken);
                if (string.IsNullOrEmpty(doubanId))
                {
                    _logger.LogInformation(Name + " Search | Summary -> Could not resolve Douban ID, skip search.");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.LogInformation($"{Name} Search | DoubanId -> {doubanId}");

                // 2. 在 Zimuku 搜索
                var subtitlePageUrl = await SearchZimukuAsync(doubanId, cancellationToken);
                if (string.IsNullOrEmpty(subtitlePageUrl))
                {
                    _logger.LogInformation(Name + " Search | Summary -> No subtitle page found on Zimuku.");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.LogInformation($"{Name} Search | SubtitlePage -> {subtitlePageUrl}");

                // 3. 获取字幕列表
                var subtitleEntries = await GetSubtitleListAsync(subtitlePageUrl, request, cancellationToken);

                if (subtitleEntries == null || subtitleEntries.Count == 0)
                {
                    _logger.LogInformation(Name + " Search | Summary -> Get  0  Subtitles");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                // 4. 排序
                var remoteSubtitles = subtitleEntries
                    .OrderByDescending(e => e.LanguageTier)
                    .ThenByDescending(e => GetQualityScore(e.Info.Name))
                    .ThenByDescending(e => GetFormatPriority(e.Info.Format))
                    .Select(e => e.Info)
                    .ToList();

                _logger.LogInformation($"{Name} Search | Summary -> Get  {remoteSubtitles.Count}  Subtitles");

                return remoteSubtitles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Name} Search | Exception -> {ex.Message}");
            }

            _logger.LogInformation($"{Name} Search | Summary -> Get  0  Subtitles");

            return Array.Empty<RemoteSubtitleInfo>();
        }

        /// <summary>
        /// 通过搜索请求解析豆瓣 ID
        /// </summary>
        private async Task<string> ResolveDoubanIdAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            // 如果是 IMDB ID，先通过豆瓣搜索转换
            var searchTitle = string.Empty;

            if (!string.IsNullOrEmpty(request.ProviderIds?.ImdbId))
            {
                searchTitle = request.ProviderIds.ImdbId;
                _logger.LogInformation($"{Name} Search | Using IMDB ID -> {searchTitle}");
            }
            else if (!string.IsNullOrEmpty(request.Name))
            {
                searchTitle = request.Name;
                if (request.IndexNumber.HasValue)
                {
                    // 对于剧集，使用系列名而不是单集名
                    searchTitle = request.Name;
                }
                _logger.LogInformation($"{Name} Search | Using title -> {searchTitle}");
            }
            else if (!string.IsNullOrEmpty(request.MediaPath))
            {
                searchTitle = Path.GetFileNameWithoutExtension(request.MediaPath);
                _logger.LogInformation($"{Name} Search | Using media path -> {searchTitle}");
            }

            if (string.IsNullOrEmpty(searchTitle))
            {
                return null;
            }

            try
            {
                var httpClient = _httpClientFactory.CreateClient(Name);

                // 尝试直接通过 IMDB ID 在 Zimuku 搜索
                if (!string.IsNullOrEmpty(request.ProviderIds?.ImdbId))
                {
                    var imdbSearchUrl = $"{ZimukuBaseUrl}/search?q={request.ProviderIds.ImdbId}&chost=zimuku.org";
                    var imdbResponse = await FetchPageWithCaptchaAsync(httpClient, imdbSearchUrl, cancellationToken);
                    if (imdbResponse != null && SubPageRegex.IsMatch(imdbResponse))
                    {
                        var match = SubPageRegex.Match(imdbResponse);
                        return $"{ZimukuBaseUrl}/subs/{match.Groups[1].Value}.html";
                    }
                }

                // 通过豆瓣搜索获取豆瓣 ID
                var encodedTitle = Uri.EscapeDataString(searchTitle);
                var doubanUrl = $"{DoubanSearchUrl}?search_text={encodedTitle}&cat=1002";

                _logger.LogInformation($"{Name} Search | Douban search URL -> {doubanUrl}");

                var requestMsg = new HttpRequestMessage(HttpMethod.Get, doubanUrl);
                var response = await httpClient.SendAsync(requestMsg, cancellationToken);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.LogWarning($"{Name} Search | Douban search failed -> {response.StatusCode}");
                    return null;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                // 尝试从 window.__DATA__ 中提取豆瓣 ID
                var dataMatch = Regex.Match(html, @"window\.__DATA__\s*=\s*""([^""]+)""");
                if (dataMatch.Success)
                {
                    // __DATA__ 可能是加密的或者直接的 JSON
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

                // 如果搜索页面有重定向到某个 subject 页面
                if (response.Headers.Location != null)
                {
                    var locationMatch = Regex.Match(response.Headers.Location.ToString(), @"/subject/(\d+)");
                    if (locationMatch.Success)
                    {
                        return locationMatch.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Name} Search | Douban resolve exception -> {ex.Message}");
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
                var httpClient = _httpClientFactory.CreateClient(Name);

                var html = await FetchPageWithCaptchaAsync(httpClient, searchUrl, cancellationToken);
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
                _logger.LogError(ex, $"{Name} Search | Zimuku search exception -> {ex.Message}");
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
                var httpClient = _httpClientFactory.CreateClient(Name);
                var html = await FetchPageWithCaptchaAsync(httpClient, subtitlePageUrl, cancellationToken);

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

                _logger.LogInformation($"{Name} Search | Requested episode -> {requestedEpisode?.ToString() ?? "N/A"}");

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
                            TwoLetterISOLanguageName = request.TwoLetterISOLanguageName,
                            Format = ExtractFormat(format),
                            Name = $"[MEIAMSUB] {langFlags} | {team} | Zimuku"
                        };

                        var info = new RemoteSubtitleInfo
                        {
                            Id = Base64Encode(JsonSerializer.Serialize(downloadInfo)),
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
                        _logger.LogWarning(ex, $"{Name} Search | Parse row exception -> {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Name} Search | GetSubtitleList exception -> {ex.Message}");
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
            _logger.LogInformation($"{Name} DownloadSub | Request -> {id}");

            return await DownloadSubAsync(id, cancellationToken);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        /// <param name="info"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<SubtitleResponse> DownloadSubAsync(string info, CancellationToken cancellationToken)
        {
            try
            {
                var downloadSub = JsonSerializer.Deserialize<DownloadSubInfo>(Base64Decode(info));

                if (downloadSub == null)
                {
                    return new SubtitleResponse();
                }

                _logger.LogInformation($"{Name} DownloadSub | DetailUrl -> {downloadSub.DetailUrl} | Language -> {downloadSub.Language}");

                var httpClient = _httpClientFactory.CreateClient(Name);

                // 1. 访问详情页面
                var detailHtml = await FetchPageWithCaptchaAsync(httpClient, downloadSub.DetailUrl, cancellationToken);
                if (string.IsNullOrEmpty(detailHtml))
                {
                    _logger.LogWarning($"{Name} DownloadSub | Failed to load detail page");
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
                    _logger.LogWarning($"{Name} DownloadSub | No download link found");
                    return new SubtitleResponse();
                }

                var downloadUrl = dlsubMatch.Groups[1].Value;
                if (!downloadUrl.StartsWith("http"))
                {
                    downloadUrl = ZimukuBaseUrl + downloadUrl;
                }

                _logger.LogInformation($"{Name} DownloadSub | DownloadUrl -> {downloadUrl}");

                // 3. 访问下载页面，获取实际文件链接
                var downloadPageHtml = await FetchPageWithCaptchaAsync(httpClient, downloadUrl, cancellationToken);
                if (string.IsNullOrEmpty(downloadPageHtml))
                {
                    _logger.LogWarning($"{Name} DownloadSub | Failed to load download page");
                    return new SubtitleResponse();
                }

                // 4. 查找所有下载链接 (div.clearfix 中的 <a> 标签)
                var linkMatches = Regex.Matches(downloadPageHtml, @"<a\s+href=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase);
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
                        _logger.LogInformation($"{Name} DownloadSub | Trying URL -> {candidateUrl}");

                        var requestMsg = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                        var response = await httpClient.SendAsync(requestMsg, cancellationToken);

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            var contentLength = response.Content.Headers.ContentLength ?? 0;
                            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                            if (contentLength > 1024 || stream.Length > 1024)
                            {
                                _logger.LogInformation($"{Name} DownloadSub | Success -> {candidateUrl} (size: {contentLength})");

                                return new SubtitleResponse
                                {
                                    Language = downloadSub.Language,
                                    IsForced = false,
                                    Format = downloadSub.Format,
                                    Stream = stream,
                                };
                            }

                            stream.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"{Name} DownloadSub | Try {candidateUrl} failed -> {ex.Message}");
                    }
                }

                _logger.LogWarning($"{Name} DownloadSub | No valid subtitle file found among {candidateUrls.Count} candidates");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} DownloadSub | Exception -> [{Type}] {Message}", Name, ex.GetType().Name, ex.Message);
            }

            return new SubtitleResponse();
        }

        #endregion

        #region CAPTCHA 处理

        /// <summary>
        /// 带验证码处理的页面获取
        /// 如果页面包含验证码，自动识别并提交，最多重试 3 次
        /// </summary>
        private async Task<string> FetchPageWithCaptchaAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
                    var response = await httpClient.SendAsync(requestMsg, cancellationToken);
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);

                    // 检查是否包含验证码
                    var captchaMatch = CaptchaRegex.Match(html);
                    if (!captchaMatch.Success)
                    {
                        return html;
                    }

                    _logger.LogInformation($"{Name} Captcha | Detected CAPTCHA (attempt {attempt + 1}/3)");

                    // 解码 base64 BMP
                    var bmpBase64 = captchaMatch.Groups[1].Value;
                    var bmpData = Convert.FromBase64String(bmpBase64);

                    // 解决验证码
                    var captchaSolution = _captchaSolver.Solve(bmpData);
                    if (string.IsNullOrEmpty(captchaSolution))
                    {
                        _logger.LogWarning($"{Name} Captcha | Failed to solve CAPTCHA");
                        return null;
                    }

                    // 转换为 hex 编码
                    var hexSolution = ZimukuCaptchaSolver.ToHexEncoded(captchaSolution);

                    // 构建提交 URL
                    var separator = url.Contains("?") ? "&" : "?";
                    var submitUrl = $"{url}{separator}security_verify_img={hexSolution}";

                    _logger.LogInformation($"{Name} Captcha | Submitting solution -> {captchaSolution} (hex: {hexSolution})");

                    // 提交验证码
                    var submitRequest = new HttpRequestMessage(HttpMethod.Get, submitUrl);
                    var submitResponse = await httpClient.SendAsync(submitRequest, cancellationToken);
                    var submitHtml = await submitResponse.Content.ReadAsStringAsync(cancellationToken);

                    // 检查是否还有验证码
                    if (!CaptchaRegex.Match(submitHtml).Success)
                    {
                        return submitHtml;
                    }

                    _logger.LogWarning($"{Name} Captcha | Still has CAPTCHA after submit, retrying...");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{Name} Captcha | Fetch exception (attempt {attempt + 1}) -> {ex.Message}");
                }
            }

            _logger.LogWarning($"{Name} Captcha | Failed to bypass CAPTCHA after 3 attempts");
            return null;
        }

        #endregion

        #region 内部方法

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

        #endregion
    }
}
