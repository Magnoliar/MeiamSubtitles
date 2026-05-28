using Jellyfin.MeiamSub.Thunder.Model;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.MeiamSub.Thunder
{
    /// <summary>
    /// 迅雷看看字幕提供程序
    /// 负责与迅雷 API 进行交互，通过 CID (Content ID) 匹配并下载字幕。
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2025-12-22</para>
    /// </summary>
    public class ThunderProvider : ISubtitleProvider, IHasOrder
    {
        #region 变量声明
        public const string ASS = "ass";
        public const string SSA = "ssa";
        public const string SRT = "srt";

        private readonly ILogger<ThunderProvider> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private static readonly JsonSerializerOptions _deserializeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public int Order => 100;
        public string Name => "MeiamSub.Thunder";

        /// <summary>
        /// 支持电影、剧集
        /// </summary>
        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };
        #endregion

        #region 构造函数
        public ThunderProvider(ILogger<ThunderProvider> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _logger.LogInformation($"{Name} Init");
        }
        #endregion

        #region 查询字幕

        /// <summary>
        /// 搜索字幕 (ISubtitleProvider 接口实现)
        /// 根据媒体信息请求字幕列表。
        /// </summary>
        /// <param name="request">包含媒体路径、语言等信息的搜索请求对象</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>远程字幕信息列表</returns>
                public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
                {
                    _logger.LogInformation("DEBUG: Received Search request for " + (request?.MediaPath ?? "NULL"));
        
                    var subtitles = await SearchSubtitlesAsync(request);
        
                    return subtitles;
                }

        /// <summary>
        /// 查询字幕
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request)
        {
            // 修改人: Meiam
            // 修改时间: 2025-12-22
            // 备注: 增加极致探测日志

            _logger.LogInformation("DEBUG: Entering SearchSubtitlesAsync (Thunder)");

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

                if (string.IsNullOrEmpty(request.MediaPath))
                {
                    _logger.LogInformation(Name + " Search | Summary -> MediaPath is empty, skip search.");
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var stopWatch = Stopwatch.StartNew();
                var cid = await GetCidByFileAsync(request.MediaPath);
                stopWatch.Stop();

                _logger.LogInformation(Name + " Search | FileHash -> " + cid + " (Took " + stopWatch.ElapsedMilliseconds + "ms)");

                using var options = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri($"https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name={Path.GetFileName(request.MediaPath)}")
                };

                using var httpClient = _httpClientFactory.CreateClient(Name);

                var response = await httpClient.SendAsync(options);

                _logger.LogInformation($"{Name} Search | Response -> {JsonSerializer.Serialize(response)}");

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var subtitleResponse = JsonSerializer.Deserialize<SubtitleResponseRoot>(await response.Content.ReadAsStringAsync(), _deserializeOptions);

                    if (subtitleResponse != null)
                    {
                        _logger.LogInformation($"{Name} Search | Response -> {JsonSerializer.Serialize(subtitleResponse)}");

                        var subtitles = subtitleResponse.Data.Where(m => !string.IsNullOrEmpty(m.Name));

                        var subtitleEntries = new List<(RemoteSubtitleInfo Info, int FpScore, int Score)>();

                        if (subtitles.Count() > 0)
                        {
                            foreach (var item in subtitles)
                            {
                                subtitleEntries.Add((new RemoteSubtitleInfo()
                                {
                                    Id = Base64Encode(JsonSerializer.Serialize(new DownloadSubInfo
                                    {
                                        Url = item.Url,
                                        Format = item.Ext,
                                        Language = request.Language,
                                        TwoLetterISOLanguageName = request.TwoLetterISOLanguageName,
                                    })),
                                    Name = $"[MEIAMSUB] {item.Name} | {(item.Langs == string.Empty ? "未知" : item.Langs)} | 迅雷",
                                    Author = "Meiam ",
                                    ProviderName = $"{Name}",
                                    Format = item.Ext,
                                    Comment = $"Format : {item.Ext}",
                                    IsHashMatch = cid == item.Cid,
                                }, item.FingerprintfScore, item.Score));
                            }
                        }

                        var remoteSubtitles = subtitleEntries
                            .OrderByDescending(e => e.Info.IsHashMatch)
                            .ThenByDescending(e => ComputeNameSimilarity(fileName, e.Info.Name))
                            .ThenByDescending(e => GetQualityScore(e.Info.Name))
                            .ThenByDescending(e => GetFormatPriority(e.Info.Format))
                            .ThenByDescending(e => e.FpScore)
                            .ThenByDescending(e => e.Score)
                            .Select(e => e.Info)
                            .ToList();

                        // AI 智能筛选
                        if (Plugin.Instance?.Configuration?.EnableAIFilter == true
                            && !string.IsNullOrEmpty(Plugin.Instance.Configuration.AIApiKey)
                            && remoteSubtitles.Count > 2)
                        {
                            remoteSubtitles = await FilterSubtitlesWithAI(remoteSubtitles, fileName);
                        }

                        _logger.LogInformation($"{Name} Search | Summary -> Get  {subtitles.Count()}  Subtitles");

                        return remoteSubtitles;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Name} Search | Exception -> {ex.Message}");
            }

            _logger.LogInformation($"{Name} Search | Summary -> Get  0  Subtitles");

            return Array.Empty<RemoteSubtitleInfo>();
        }
        #endregion

        #region 下载字幕
        /// <summary>
        /// 获取字幕内容 (ISubtitleProvider 接口实现)
        /// 根据字幕 ID 下载具体的字幕文件流。
        /// </summary>
        /// <param name="id">字幕唯一标识符 (Base64 编码的 JSON 数据)</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>包含字幕流的响应对象</returns>
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"{Name} DownloadSub | Request -> {id}");

            return await DownloadSubAsync(id);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        private async Task<SubtitleResponse> DownloadSubAsync(string info)
        {
            // 修改人: Meiam
            // 修改时间: 2025-12-22
            // 备注: 增加异常处理

            try
            {
                var downloadSub = JsonSerializer.Deserialize<DownloadSubInfo>(Base64Decode(info));

                if (downloadSub == null)
                {
                    return new SubtitleResponse();
                }

                _logger.LogInformation($"{Name} DownloadSub | Url -> {downloadSub.Url}  |  Format -> {downloadSub.Format} |  Language -> {downloadSub.Language} ");

                using var options = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(downloadSub.Url)
                };

                using var httpClient = _httpClientFactory.CreateClient(Name);

                var response = await httpClient.SendAsync(options);

                _logger.LogInformation($"{Name} DownloadSub | Response -> {response.StatusCode}");

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var stream = await response.Content.ReadAsStreamAsync();

                    return new SubtitleResponse()
                    {
                        Language = downloadSub.Language,
                        IsForced = false,
                        Format = downloadSub.Format,
                        Stream = stream,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} DownloadSub | Exception -> [{Type}] {Message}", Name, ex.GetType().Name, ex.Message);
            }

            return new SubtitleResponse();

        }
        #endregion

        #region 内部方法

        /// <summary>
        /// Base64 加密
        /// </summary>
        /// <param name="plainText">明文</param>
        /// <returns></returns>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }
        /// <summary>
        /// Base64 解密
        /// </summary>
        /// <param name="base64EncodedData"></param>
        /// <returns></returns>
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
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// <param name="language"></param>
        /// <returns></returns>
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
        /// 异步计算文件 CID (迅雷专用算法)
        /// <para>修改人: Meiam</para>
        /// <para>修改时间: 2025-12-22</para>
        /// <para>备注: 采用异步 I/O 读取文件特定位置的数据块进行 SHA1 计算。</para>
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>计算得到的 CID 字符串</returns>
        private async Task<string> GetCidByFileAsync(string filePath)
        {
            // 修改人: Meiam
            // 修改时间: 2025-12-22
            // 备注: 改造为异步方法，优化 I/O 性能，使用 SHA1.Create() 替代旧 API，并增加 using 语句释放资源

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                var fileSize = new FileInfo(filePath).Length;
                using (var sha1 = SHA1.Create())
                {
                    var buffer = new byte[0xf000];
                    if (fileSize < 0xf000)
                    {
                        await stream.ReadExactlyAsync(buffer, 0, (int)fileSize);
                        buffer = sha1.ComputeHash(buffer, 0, (int)fileSize);
                    }
                    else
                    {
                        await stream.ReadExactlyAsync(buffer, 0, 0x5000);
                        stream.Seek(fileSize / 3, SeekOrigin.Begin);
                        await stream.ReadExactlyAsync(buffer, 0x5000, 0x5000);
                        stream.Seek(fileSize - 0x5000, SeekOrigin.Begin);
                        await stream.ReadExactlyAsync(buffer, 0xa000, 0x5000);

                        buffer = sha1.ComputeHash(buffer, 0, 0xf000);
                    }
                    var result = "";
                    foreach (var i in buffer)
                    {
                        result += string.Format("{0:X2}", i);
                    }
                    return result;
                }
            }
        }

        /// <summary>
        /// 使用 AI 筛选字幕，返回推荐序列
        /// </summary>
        private async Task<List<RemoteSubtitleInfo>> FilterSubtitlesWithAI(List<RemoteSubtitleInfo> candidates, string videoName)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
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

                var requestBody = new
                {
                    model = config.AIModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    stream = false
                };

                var endpoint = string.IsNullOrEmpty(config.AIEndpoint)
                    ? "https://tokenhub.tencentmaas.com/v1/chat/completions"
                    : config.AIEndpoint;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var httpClient = _httpClientFactory.CreateClient(Name);
                var json = JsonSerializer.Serialize(requestBody, _deserializeOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                content.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.AIApiKey}");

                var response = await httpClient.PostAsync(endpoint, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"{Name} AI Filter | Response -> {responseBody}");

                    var responseObj = JsonSerializer.Deserialize<AIResponse>(responseBody, _deserializeOptions);
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
                            _logger.LogInformation($"{Name} AI Filter | Sequence -> {string.Join(",", indices)}");
                            return reordered;
                        }
                        else
                        {
                            _logger.LogInformation($"{Name} AI Filter | Invalid sequence, fallback to sort. Reply: {aiReply}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Name} AI Filter | Exception -> {ex.Message} (fallback to sort)");
            }

            return candidates;
        }

        #endregion
    }
}