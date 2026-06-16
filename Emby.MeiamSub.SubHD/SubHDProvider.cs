using Emby.MeiamSub.SubHD.Model;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.System;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.MeiamSub.SubHD
{
    /// <summary>
    /// SubHD 字幕提供程序
    /// 负责从 SubHD.tv 搜索和下载字幕。
    /// </summary>
    public class SubHDProvider : ISubtitleProvider, IHasOrder
    {
        #region 变量声明
        public const string ASS = "ass";
        public const string SSA = "ssa";
        public const string SRT = "srt";

        protected readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;
        private readonly IServiceRoot _serviceRoot;

        private Plugin MainPlugin { get; set; }

        public int Order => 100;
        public string Name => "MeiamSub.SubHD";

        /// <summary>
        /// 支持电影、剧集
        /// </summary>
        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };
        #endregion

        #region 构造函数
        public SubHDProvider(ILogManager logManager, IJsonSerializer jsonSerializer, IHttpClient httpClient, IApplicationHost applicationHost)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            _serviceRoot = new ServiceRoot(applicationHost);
            MainPlugin = _serviceRoot.GetService<IApplicationHost>().Plugins.OfType<Plugin>().FirstOrDefault();

            _logger.Info("{0} Init", new object[1] { Name });
        }
        #endregion

        #region 查询字幕

        /// <summary>
        /// 搜索字幕 (ISubtitleProvider 接口实现)
        /// </summary>
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.Info("{0} Search | SubtitleSearchRequest -> {1}", new object[2] { Name, _jsonSerializer.SerializeToString(request) });

            var subtitles = await SearchSubtitlesAsync(request);

            return subtitles;
        }

        /// <summary>
        /// 查询字幕
        /// </summary>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request)
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

                var stopWatch = Stopwatch.StartNew();

                // Step 1: 获取 Douban ID
                var doubanId = MainPlugin?.Options?.DoubanId;

                if (string.IsNullOrEmpty(doubanId))
                {
                    doubanId = await GetDoubanIdFromSearch(request.Name, request.Year);
                }

                if (string.IsNullOrEmpty(doubanId))
                {
                    _logger.Info("{0} Search | Summary -> Cannot resolve Douban ID, skip search.", new object[1] { Name });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.Info("{0} Search | DoubanId -> {1} (Took {2}ms)", new object[3] { Name, doubanId, stopWatch.ElapsedMilliseconds });

                // Step 2: 通过 Douban ID 搜索 SubHD
                var subHdPageUrl = await SearchSubHDByDoubanId(doubanId);

                if (string.IsNullOrEmpty(subHdPageUrl))
                {
                    _logger.Info("{0} Search | Summary -> No SubHD page found for Douban ID {1}", new object[2] { Name, doubanId });
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                _logger.Info("{0} Search | SubHD Page -> {1}", new object[2] { Name, subHdPageUrl });

                // Step 3: 解析字幕列表
                var subtitleEntries = await ParseSubtitleEntries(subHdPageUrl, fileName, language, request);

                stopWatch.Stop();
                _logger.Info("{0} Search | Summary -> Get {1} Subtitles (Took {2}ms)", new object[3] { Name, subtitleEntries.Count, stopWatch.ElapsedMilliseconds });

                return subtitleEntries;
            }
            catch (Exception ex)
            {
                _logger.Error("{0} Search | Exception -> [{1}] {2}", Name, ex.GetType().Name, ex.Message);
            }

            _logger.Info("{0} Search | Summary -> Get  0  Subtitles", new object[1] { Name });

            return Array.Empty<RemoteSubtitleInfo>();
        }

        #endregion

        #region 下载字幕
        /// <summary>
        /// 获取字幕内容 (ISubtitleProvider 接口实现)
        /// </summary>
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            _logger.Info("{0} DownloadSub | Request -> {1}", new object[2] { Name, id });

            return await DownloadSubAsync(id);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        private async Task<SubtitleResponse> DownloadSubAsync(string info)
        {
            try
            {
                var downloadSub = _jsonSerializer.DeserializeFromString<DownloadSubInfo>(Base64Decode(info));

                if (downloadSub == null)
                {
                    return new SubtitleResponse();
                }

                _logger.Info("{0} DownloadSub | SubId -> {1} | Format -> {2} | Language -> {3}",
                    new object[4] { Name, downloadSub.SubId, downloadSub.Format, downloadSub.Language });

                // Step 1: 访问字幕详情页，获取下载链接
                var detailPageUrl = $"https://subhd.tv/a/{downloadSub.SubId}";
                var detailResponse = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = detailPageUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                });

                if (detailResponse.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Info("{0} DownloadSub | Detail page failed -> {1}", new object[2] { Name, detailResponse.StatusCode });
                    return new SubtitleResponse();
                }

                var detailHtml = ReadStreamToString(detailResponse.Content);

                // Step 2: 提取下载按钮链接
                var downLinkMatch = Regex.Match(detailHtml, @"href=""(\/down\/[^""]+)""", RegexOptions.IgnoreCase);
                if (!downLinkMatch.Success)
                {
                    // 备用匹配
                    downLinkMatch = Regex.Match(detailHtml, @"class=""down""[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase);
                }

                if (!downLinkMatch.Success)
                {
                    _logger.Info("{0} DownloadSub | No download link found", new object[1] { Name });
                    return new SubtitleResponse();
                }

                var downPath = downLinkMatch.Groups[1].Value;
                _logger.Info("{0} DownloadSub | DownPath -> {1}", new object[2] { Name, downPath });

                // Step 3: 访问下载页面
                var downPageUrl = $"https://subhd.tv{downPath}";
                var downResponse = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = downPageUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                });

                if (downResponse.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Info("{0} DownloadSub | Down page failed -> {1}", new object[2] { Name, downResponse.StatusCode });
                    return new SubtitleResponse();
                }

                var downHtml = ReadStreamToString(downResponse.Content);

                // Step 4: 提取 sid
                // Emby IHttpClient doesn't expose redirected URL, try from HTML
                var sidMatch = Regex.Match(downHtml, @"""sid""\s*:\s*""([^""]+)""");
                if (!sidMatch.Success)
                {
                    sidMatch = Regex.Match(downHtml, @"sid\s*=\s*['""]([^'""]+)['""]");
                }
                if (!sidMatch.Success)
                {
                    sidMatch = Regex.Match(downHtml, @"sid=([^&""\s]+)");
                }

                if (!sidMatch.Success)
                {
                    _logger.Info("{0} DownloadSub | No sid found", new object[1] { Name });
                    return new SubtitleResponse();
                }

                var sid = sidMatch.Groups[1].Value;
                _logger.Info("{0} DownloadSub | SID -> {1}", new object[2] { Name, sid });

                // Step 5: 调用 SubHD API 下载
                var downloadUrl = await DownloadFromSubHDApi(sid);

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    _logger.Info("{0} DownloadSub | API download failed", new object[1] { Name });
                    return new SubtitleResponse();
                }

                _logger.Info("{0} DownloadSub | DownloadUrl -> {1}", new object[2] { Name, downloadUrl });

                // Step 6: 下载文件
                var fileResponse = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = downloadUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "*/*",
                });

                if (fileResponse.StatusCode == HttpStatusCode.OK)
                {
                    return new SubtitleResponse()
                    {
                        Language = downloadSub.Language,
                        IsForced = false,
                        Format = downloadSub.Format,
                        Stream = fileResponse.Content,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} DownloadSub | Exception -> [{1}] {2}", Name, ex.GetType().Name, ex.Message);
            }

            return new SubtitleResponse();
        }

        /// <summary>
        /// 调用 SubHD API 下载字幕，处理验证码
        /// </summary>
        private async Task<string> DownloadFromSubHDApi(string sid)
        {
            var requestBody = new { sid = sid, cap = "" };
            var json = _jsonSerializer.SerializeToString(requestBody);

            var response = await _httpClient.Post(new HttpRequestOptions
            {
                Url = "https://subhd.tv/api/sub/down",
                UserAgent = Name,
                TimeoutMs = 30000,
                RequestContent = json,
                RequestContentType = "application/json",
                AcceptHeader = "application/json",
            });

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Info("{0} DownloadSub | API response -> {1}", new object[2] { Name, response.StatusCode });
                return null;
            }

            var responseJson = ReadStreamToString(response.Content);
            _logger.Info("{0} DownloadSub | API body -> {1}", new object[2] { Name, responseJson });

            // Parse the JSON response manually using regex since we can't use JsonDocument easily with Emby serializer
            // Check for "pass":false (captcha required)
            var passMatch = Regex.Match(responseJson, @"""pass""\s*:\s*(true|false)");
            var hasUrl = Regex.Match(responseJson, @"""url""\s*:\s*""([^""]+)""");

            if (passMatch.Success && passMatch.Groups[1].Value == "false")
            {
                _logger.Info("{0} DownloadSub | Captcha required, attempting to solve...", new object[1] { Name });

                // Extract msg (SVG content)
                var msgMatch = Regex.Match(responseJson, @"""msg""\s*:\s*""([^""]*?)""");
                if (msgMatch.Success)
                {
                    var svgContent = msgMatch.Groups[1].Value;
                    // Unescape JSON string
                    svgContent = svgContent.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
                    var captchaAnswer = SubHDCaptchaSolver.Solve(svgContent);

                    if (!string.IsNullOrEmpty(captchaAnswer))
                    {
                        _logger.Info("{0} DownloadSub | Captcha answer -> {1}", new object[2] { Name, captchaAnswer });

                        // 重试，带验证码
                        var retryBody = new { sid = sid, cap = captchaAnswer };
                        var retryJson = _jsonSerializer.SerializeToString(retryBody);

                        var retryResponse = await _httpClient.Post(new HttpRequestOptions
                        {
                            Url = "https://subhd.tv/api/sub/down",
                            UserAgent = Name,
                            TimeoutMs = 30000,
                            RequestContent = retryJson,
                            RequestContentType = "application/json",
                            AcceptHeader = "application/json",
                        });

                        if (retryResponse.StatusCode == HttpStatusCode.OK)
                        {
                            var retryJsonStr = ReadStreamToString(retryResponse.Content);
                            _logger.Info("{0} DownloadSub | Retry API body -> {1}", new object[2] { Name, retryJsonStr });

                            var retryUrlMatch = Regex.Match(retryJsonStr, @"""url""\s*:\s*""([^""]+)""");
                            if (retryUrlMatch.Success)
                            {
                                return retryUrlMatch.Groups[1].Value;
                            }
                        }
                    }
                }
            }

            // 直接成功
            if (hasUrl.Success)
            {
                return hasUrl.Groups[1].Value;
            }

            return null;
        }

        #endregion

        #region 搜索与解析

        /// <summary>
        /// 从搜索请求解析 Douban ID
        /// </summary>
        private async Task<string> GetDoubanIdFromSearch(string name, int? year)
        {
            if (string.IsNullOrEmpty(name)) return null;

            try
            {
                var searchText = Uri.EscapeDataString(name);
                var searchUrl = $"https://search.douban.com/movie/subject_search?search_text={searchText}&cat=1002";

                var response = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = searchUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                });

                if (response.StatusCode != HttpStatusCode.OK) return null;

                var html = ReadStreamToString(response.Content);

                // 尝试从 window.__DATA__ 提取 JSON
                var dataMatch = Regex.Match(html, @"window\.__DATA__\s*=\s*""([^""]+)""");
                if (dataMatch.Success)
                {
                    var encodedData = dataMatch.Groups[1].Value;
                    // Douban 有时使用 base64 编码
                    try
                    {
                        var decodedBytes = Convert.FromBase64String(encodedData);
                        var decodedJson = Encoding.UTF8.GetString(decodedBytes);
                        var subjectId = ExtractSubjectIdFromJson(decodedJson);
                        if (!string.IsNullOrEmpty(subjectId)) return subjectId;
                    }
                    catch { }
                }

                // 备用：从 HTML 直接提取 subject ID
                var subjectMatch = Regex.Match(html, @"/subject/(\d+)");
                if (subjectMatch.Success)
                {
                    return subjectMatch.Groups[1].Value;
                }

                // 再备用：从 href 链接中提取
                var hrefMatch = Regex.Match(html, @"sid=(\d+)");
                if (hrefMatch.Success)
                {
                    return hrefMatch.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} GetDoubanId | Exception -> [{1}] {2}", Name, ex.GetType().Name, ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 从 Douban JSON 数据中提取 Subject ID
        /// </summary>
        private string ExtractSubjectIdFromJson(string json)
        {
            try
            {
                // Use regex to extract from JSON since we don't have JsonDocument in Emby
                var idMatch = Regex.Match(json, @"""id""\s*:\s*""(\d+)""");
                if (idMatch.Success) return idMatch.Groups[1].Value;

                var urlMatch = Regex.Match(json, @"""url""\s*:\s*""[^""]*?/subject/(\d+)");
                if (urlMatch.Success) return urlMatch.Groups[1].Value;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通过 Douban ID 在 SubHD 搜索字幕
        /// </summary>
        private async Task<string> SearchSubHDByDoubanId(string doubanId)
        {
            try
            {
                var searchUrl = $"https://subhd.tv/search/{doubanId}";
                var response = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = searchUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                });

                if (response.StatusCode != HttpStatusCode.OK) return null;

                var html = ReadStreamToString(response.Content);

                // 查找字幕页面链接 /d/{id}
                var match = Regex.Match(html, @"href=""\/d\/(\d+)""");
                if (!match.Success)
                {
                    match = Regex.Match(html, @"/d/(\d+)");
                }

                if (match.Success)
                {
                    return $"https://subhd.tv/d/{match.Groups[1].Value}";
                }
            }
            catch (Exception ex)
            {
                _logger.Error("{0} SearchSubHD | Exception -> [{1}] {2}", Name, ex.GetType().Name, ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 解析字幕列表页面
        /// </summary>
        private async Task<List<RemoteSubtitleInfo>> ParseSubtitleEntries(string pageUrl, string fileName, string language, SubtitleSearchRequest request)
        {
            var result = new List<RemoteSubtitleInfo>();

            try
            {
                var response = await _httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = pageUrl,
                    UserAgent = Name,
                    TimeoutMs = 30000,
                    AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                });

                if (response.StatusCode != HttpStatusCode.OK) return result;

                var html = ReadStreamToString(response.Content);

                // 提取字幕条目 - 查找容器中的条目
                // 每个字幕条目在 div.bg-white.shadow-sm.rounded-3.mb-5 中
                // 提取所有 a.link-dark 链接作为字幕页面
                var entryPattern = new Regex(
                    @"<a[^>]*class=""link-dark""[^>]*href=""\/a\/(\d+)""[^>]*>(.*?)<\/a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                var containerPattern = new Regex(
                    @"<div[^>]*class=""[^""]*bg-white[^""]*shadow-sm[^""]*rounded-3[^""]*mb-5[^""]*""[^>]*>(.*?)<\/div>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                // 尝试匹配容器内容
                var containerMatch = containerPattern.Match(html);
                var searchArea = containerMatch.Success ? containerMatch.Value : html;

                var matches = entryPattern.Matches(searchArea);

                // 如果 link-dark 没匹配到，用更宽松的模式
                if (matches.Count == 0)
                {
                    entryPattern = new Regex(
                        @"href=""\/a\/(\d+)""[^>]*>(.*?)<\/a>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    matches = entryPattern.Matches(searchArea);
                }

                foreach (Match match in matches)
                {
                    var subId = match.Groups[1].Value;
                    var linkText = match.Groups[2].Value;

                    // 清除 HTML 标签
                    var cleanText = Regex.Replace(linkText, @"<[^>]+>", " ").Trim();
                    cleanText = Regex.Replace(cleanText, @"\s+", " ");

                    // 提取标签（来源、语言、格式）
                    var tags = ExtractTags(match.Value);
                    var sourceType = tags.SourceType;
                    var subLanguage = tags.Language;
                    var format = tags.Format;

                    // 如果无法从标签解析语言，尝试从标题推断
                    if (string.IsNullOrEmpty(subLanguage))
                    {
                        subLanguage = InferLanguage(cleanText);
                    }

                    // 如果无法从标签解析格式，从标题推断
                    if (string.IsNullOrEmpty(format))
                    {
                        format = ExtractFormat(cleanText);
                    }

                    // 集数过滤（仅对剧集）
                    // Note: Emby SubtitleSearchRequest has IndexNumber but not IndexNumberEnd
                    if (request.IndexNumber.HasValue && request.ContentType == VideoContentType.Episode)
                    {
                        if (!EpisodeMatches(cleanText, request.IndexNumber.Value))
                        {
                            continue;
                        }
                    }

                    // 构建名称
                    var displayName = string.IsNullOrEmpty(sourceType)
                        ? cleanText
                        : $"{cleanText} | {sourceType}";

                    var normalizedLang = NormalizeLanguage(subLanguage ?? request.Language);

                    var info = new RemoteSubtitleInfo()
                    {
                        Id = Base64Encode(_jsonSerializer.SerializeToString(new DownloadSubInfo
                        {
                            SubId = subId,
                            Title = cleanText,
                            Format = format,
                            Language = normalizedLang,
                        })),
                        Name = $"[MEIAMSUB] {displayName} | SubHD",
                        Language = request.Language,
                        Author = "Meiam ",
                        ProviderName = $"{Name}",
                        Format = format,
                        Comment = $"Source: {sourceType ?? "Unknown"} | Format: {format ?? "Unknown"}",
                    };

                    result.Add(info);
                }

                // 排序
                result = result
                    .OrderByDescending(e => ComputeNameSimilarity(fileName, e.Name))
                    .ThenByDescending(e => GetQualityScore(e.Name))
                    .ThenByDescending(e => GetFormatPriority(e.Format))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error("{0} ParseSubtitleEntries | Exception -> [{1}] {2}", Name, ex.GetType().Name, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 从字幕条目 HTML 中提取标签信息
        /// </summary>
        private (string SourceType, string Language, string Format) ExtractTags(string html)
        {
            var sourceType = "";
            var language = "";
            var format = "";

            // 提取所有 badge/span 标签内容
            var tagPattern = new Regex(@"<(?:span|div|a)[^>]*class=""[^""]*(?:badge|tag|label)[^""]*""[^>]*>([^<]+)<", RegexOptions.IgnoreCase);
            var tagMatches = tagPattern.Matches(html);

            foreach (Match tagMatch in tagMatches)
            {
                var tagText = tagMatch.Groups[1].Value.Trim();

                // 来源类型
                if (tagText.Contains("官方字幕") || tagText.Contains("转载精修") ||
                    tagText.Contains("原创翻译") || tagText.Contains("机器翻译") || tagText.Contains("AI翻润色"))
                {
                    sourceType = tagText;
                }

                // 语言
                if (tagText.Contains("简体") || tagText.Contains("繁体") || tagText.Contains("英语"))
                {
                    language = tagText;
                }

                // 格式
                var upperTag = tagText.ToUpper();
                if (upperTag.Contains("ASS") || upperTag.Contains("SRT") || upperTag.Contains("SSA"))
                {
                    format = tagText.ToUpper();
                }
            }

            // 备用：从标题文本中查找
            if (string.IsNullOrEmpty(format))
            {
                var upperHtml = html.ToUpper();
                if (upperHtml.Contains("ASS")) format = "ASS";
                else if (upperHtml.Contains("SRT")) format = "SRT";
                else if (upperHtml.Contains("SSA")) format = "SSA";
            }

            return (sourceType, language, format);
        }

        /// <summary>
        /// 从文本推断语言
        /// </summary>
        private string InferLanguage(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Contains("简体") || text.Contains("简中") || text.Contains("中文")) return "简体";
            if (text.Contains("繁体") || text.Contains("繁中")) return "繁体";
            if (text.Contains("英语") || text.Contains("英文") || text.Contains("English")) return "英语";
            return "";
        }

        /// <summary>
        /// 集数匹配检查
        /// </summary>
        private bool EpisodeMatches(string text, int episodeNumber)
        {
            if (string.IsNullOrEmpty(text)) return true;

            // S01E05 格式
            var sxxexxMatch = Regex.Match(text, @"S\d+E(\d+)", RegexOptions.IgnoreCase);
            if (sxxexxMatch.Success)
            {
                var ep = int.Parse(sxxexxMatch.Groups[1].Value);
                return ep == episodeNumber;
            }

            // E05 格式
            var exxMatch = Regex.Match(text, @"(?:^|[^A-Za-z])E(\d+)(?:[^A-Za-z]|$)", RegexOptions.IgnoreCase);
            if (exxMatch.Success)
            {
                var ep = int.Parse(exxMatch.Groups[1].Value);
                return ep == episodeNumber;
            }

            // EP05 格式
            var epMatch = Regex.Match(text, @"EP?(\d+)", RegexOptions.IgnoreCase);
            if (epMatch.Success)
            {
                var ep = int.Parse(epMatch.Groups[1].Value);
                return ep == episodeNumber;
            }

            // 第5集 格式
            var cnMatch = Regex.Match(text, @"第(\d+)[集话]");
            if (cnMatch.Success)
            {
                var ep = int.Parse(cnMatch.Groups[1].Value);
                return ep == episodeNumber;
            }

            // 如果没找到任何集数标记，可能是一个通用字幕（包含全部集数），允许通过
            return true;
        }

        #endregion

        #region 内部方法

        /// <summary>
        /// 从 Stream 读取字符串
        /// </summary>
        private static string ReadStreamToString(Stream stream)
        {
            if (stream == null) return string.Empty;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
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
        /// 计算视频名称与字幕名称的相似度 (0-1)
        /// </summary>
        private static double ComputeNameSimilarity(string videoName, string subtitleName)
        {
            if (string.IsNullOrEmpty(videoName) || string.IsNullOrEmpty(subtitleName)) return 0;
            var cleanVideo = new string(videoName.Where(char.IsLetterOrDigit).ToArray()).ToLower();
            var cleanSub = new string(subtitleName.Where(char.IsLetterOrDigit).ToArray()).ToLower();
            if (cleanVideo.Length == 0) return 0;
            var matched = cleanVideo.Count(c => cleanSub.Contains(c));
            return (double)matched / cleanVideo.Length;
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

            return null;
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

        #endregion

        #region Captcha Solver

        /// <summary>
        /// SubHD SVG 验证码求解器
        /// 从 SVG 路径中提取 d 属性，根据路径长度映射表解析出字符。
        /// </summary>
        private static class SubHDCaptchaSolver
        {
            // 路径长度 -> 字符 映射表（131 条）
            private static readonly Dictionary<double, char> LENGTH_MAP = new Dictionary<double, char>
            {
                {1148.7812, 'A'},{1133.4062, 'A'},{943.5156, 'A'},{1127.8438, 'A'},{1216.0156, 'A'},
                {867.0625, 'B'},{868.5312, 'B'},{846.8437, 'B'},{875.2656, 'B'},{857.3281, 'B'},
                {925.25, 'C'},{887.8594, 'C'},{873.5625, 'C'},{870.5, 'C'},{888.2188, 'C'},
                {1079.0156, 'D'},{1085.125, 'D'},{1041.3906, 'D'},{1076.25, 'D'},{1072.1719, 'D'},
                {806.7344, 'E'},{778.6719, 'E'},{780.7656, 'E'},{785.5625, 'E'},{793.5312, 'E'},
                {797.3437, 'F'},{767.6719, 'F'},{769.4062, 'F'},{772.0625, 'F'},{778.8281, 'F'},
                {1026.4531, 'G'},{1029.7031, 'G'},{993.5156, 'G'},{1025.7344, 'G'},{1025.7031, 'G'},
                {1026.9062, 'H'},{1030.1562, 'H'},{1023.7031, 'H'},{1022.75, 'H'},{1017.7188, 'H'},
                {419.2188, 'I'},{409.5156, 'I'},{404.2344, 'I'},{420.4688, 'I'},{404.2031, 'I'},
                {635.25, 'J'},{626.9688, 'J'},{626.1875, 'J'},{624.6094, 'J'},{619.3281, 'J'},
                {965.7812, 'K'},{988.8281, 'K'},{932.9062, 'K'},{963.7969, 'K'},{957.25, 'K'},
                {822.7031, 'L'},{828.8125, 'L'},{808.9375, 'L'},{820.4688, 'L'},{816.3906, 'L'},
                {1144.2969, 'M'},{1143.2812, 'M'},{1143.3906, 'M'},{1140.7031, 'M'},{1141.7812, 'M'},
                {1024.4375, 'N'},{1024.4375, 'N'},{1018.0781, 'N'},{1018.25, 'N'},{1018.9531, 'N'},
                {1011.3906, 'O'},{995.9844, 'O'},{1028.0781, 'O'},{980.875, 'O'},{1019.9688, 'O'},
                {871.4062, 'P'},{888.1719, 'P'},{859.1562, 'P'},{859.5312, 'P'},{862.3125, 'P'},
                {1075.4375, 'Q'},{1106.6875, 'Q'},{1060.4844, 'Q'},{1102.3906, 'Q'},{1077.0781, 'Q'},
                {943.9062, 'R'},{952.0781, 'R'},{917.5, 'R'},{953.4219, 'R'},{937.8594, 'R'},
                {888.6875, 'S'},{853.2656, 'S'},{866.9375, 'S'},{858.4062, 'S'},{870.0781, 'S'},
                {569.25, 'T'},{558.0937, 'T'},{553.5156, 'T'},{553.75, 'T'},{559.7188, 'T'},
                {982.7656, 'U'},{973.0, 'U'},{967.0156, 'U'},{968.625, 'U'},{964.4688, 'U'},
                {897.1094, 'V'},{896.125, 'V'},{885.6562, 'V'},{891.4375, 'V'},{889.0, 'V'},
                {1176.5938, 'W'},{1180.125, 'W'},{1169.1875, 'W'},{1163.3438, 'W'},{1163.375, 'W'},
                {976.0937, 'X'},{970.25, 'X'},{968.8281, 'X'},{973.5312, 'X'},{977.0625, 'X'},
                {573.75, 'Y'},{573.75, 'Y'},{563.2656, 'Y'},{569.7812, 'Y'},{563.2969, 'Y'},
                {954.5156, 'Z'},{926.9375, 'Z'},{929.625, 'Z'},{926.9688, 'Z'},{928.9531, 'Z'},
                {1030.0, '0'},{1041.1094, '0'},{1054.9219, '0'},{1064.4844, '0'},{1046.5, '0'},
                {534.75, '1'},{542.5, '1'},{534.25, '1'},{530.0, '1'},{528.75, '1'},
                {865.625, '2'},{868.75, '2'},{861.375, '2'},{853.0625, '2'},{851.625, '2'},
                {892.1875, '3'},{893.7188, '3'},{883.6562, '3'},{875.9219, '3'},{873.3125, '3'},
                {957.0156, '4'},{978.9219, '4'},{969.0156, '4'},{984.4844, '4'},{943.7344, '4'},
                {930.75, '5'},{927.8125, '5'},{915.9375, '5'},{922.375, '5'},{926.5, '5'},
                {950.6875, '6'},{944.1094, '6'},{946.3281, '6'},{956.7188, '6'},{940.0469, '6'},
                {818.4375, '7'},{836.0625, '7'},{815.2188, '7'},{814.875, '7'},{819.3594, '7'},
                {953.8594, '8'},{949.0625, '8'},{963.5, '8'},{955.6406, '8'},{943.5937, '8'},
                {943.7969, '9'},{948.5625, '9'},{950.4531, '9'},{944.9062, '9'},{942.1406, '9'},
            };

            /// <summary>
            /// 解析 SVG 验证码
            /// </summary>
            /// <param name="svgContent">SVG 内容（HTML 编码或原始 SVG）</param>
            /// <returns>解析出的验证码字符串</returns>
            public static string Solve(string svgContent)
            {
                if (string.IsNullOrEmpty(svgContent)) return "";

                try
                {
                    // 解码 HTML 实体
                    var svg = WebUtility.HtmlDecode(svgContent);

                    // 提取所有 path 的 d 属性
                    var pathPattern = new Regex(@"d=""([^""]+)""", RegexOptions.IgnoreCase);
                    var pathMatches = pathPattern.Matches(svg);

                    if (pathMatches.Count == 0)
                    {
                        // 尝试另一种格式
                        pathPattern = new Regex(@"d='([^']+)'", RegexOptions.IgnoreCase);
                        pathMatches = pathPattern.Matches(svg);
                    }

                    var paths = new List<(double Length, double StartX)>();

                    foreach (Match pathMatch in pathMatches)
                    {
                        var d = pathMatch.Groups[1].Value;

                        // 过滤长度 > 500 的路径
                        if (d.Length <= 500) continue;

                        // 提取起点 X 坐标
                        var startX = ExtractStartX(d);

                        // 计算路径长度
                        var pathLength = ApproximatePathLength(d);

                        paths.Add((pathLength, startX));
                    }

                    if (paths.Count == 0) return "";

                    // 按起始 X 坐标排序
                    paths = paths.OrderBy(p => p.StartX).ToList();

                    var result = new StringBuilder();
                    var usedChars = new HashSet<int>();

                    foreach (var path in paths)
                    {
                        // 在映射表中查找最接近的长度
                        var bestChar = LookupByLength(path.Length, usedChars);
                        if (bestChar.HasValue)
                        {
                            result.Append(bestChar.Value);
                            usedChars.Add(bestChar.Value);
                        }
                    }

                    return result.ToString();
                }
                catch (Exception)
                {
                    return "";
                }
            }

            /// <summary>
            /// 从 SVG path d 属性中提取起始 X 坐标
            /// </summary>
            private static double ExtractStartX(string d)
            {
                // 匹配 M x,y 或 M x y
                var match = Regex.Match(d, @"M\s*([-\d.]+)[\s,]+([-\d.]+)");
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                {
                    return x;
                }
                return 0;
            }

            /// <summary>
            /// 近似计算 SVG path 长度
            /// </summary>
            private static double ApproximatePathLength(string d)
            {
                // 解析 path 中的坐标点，计算总线段长度
                var pointPattern = new Regex(@"([-\d.]+)[\s,]+([-\d.]+)");
                var points = new List<(double X, double Y)>();

                foreach (Match m in pointPattern.Matches(d))
                {
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                        double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                    {
                        points.Add((x, y));
                    }
                }

                if (points.Count < 2) return d.Length;

                double totalLength = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    var dx = points[i].X - points[i - 1].X;
                    var dy = points[i].Y - points[i - 1].Y;
                    totalLength += Math.Sqrt(dx * dx + dy * dy);
                }

                return totalLength > 0 ? totalLength : d.Length;
            }

            /// <summary>
            /// 根据路径长度查找映射表中的字符
            /// 使用碰撞解决：当多个字符有相同长度时，选择未使用的
            /// </summary>
            private static char? LookupByLength(double length, HashSet<int> usedChars)
            {
                // 精确匹配
                if (LENGTH_MAP.TryGetValue(length, out var exactChar))
                {
                    if (!usedChars.Contains(exactChar))
                        return exactChar;
                }

                // 查找最接近的
                double minDiff = double.MaxValue;
                char? bestChar = null;

                foreach (var kvp in LENGTH_MAP)
                {
                    if (usedChars.Contains(kvp.Value)) continue;

                    var diff = Math.Abs(kvp.Key - length);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestChar = kvp.Value;
                    }
                }

                return bestChar;
            }
        }

        #endregion
    }
}
