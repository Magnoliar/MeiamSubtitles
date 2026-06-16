using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.MeiamSub.Zimuku.Captcha
{
    /// <summary>
    /// Zimuku CAPTCHA 求解器
    /// 通过模板匹配解决 BMP 格式验证码 (100x27 像素, 5 位数字)
    /// <para>修改人: Meiam</para>
    /// <para>修改时间: 2025-12-22</para>
    /// </summary>
    public class ZimukuCaptchaSolver
    {
        private readonly ILogger<ZimukuCaptchaSolver> _logger;

        // 每个字符采样 9 个像素位置 (x, y)，用于与模板匹配
        // 这些位置经过精心选择，能有效区分 0-9 数字
        private static readonly (int X, int Y)[] SamplePositions = new (int, int)[]
        {
            (3, 3),   // 左上
            (10, 3),  // 上中
            (16, 3),  // 右上
            (3, 13),  // 中左
            (10, 13), // 中央
            (16, 13), // 中右
            (3, 23),  // 左下
            (10, 23), // 下中
            (16, 23), // 右下
        };

        // 每个字符宽度约 17 像素，间距约 3 像素
        private const int CharWidth = 17;
        private const int CharSpacing = 3;
        private const int NumChars = 5;

        // 数字模板 (每个数字对应 9 个采样像素的亮度阈值: 1=亮, 0=暗)
        // 这些模板是从实际验证码样本中提取的特征向量
        private static readonly Dictionary<int, int[]> DigitTemplates = new Dictionary<int, int[]>
        {
            { 0, new int[] { 1, 1, 1, 1, 0, 1, 1, 1, 1 } },
            { 1, new int[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 } },
            { 2, new int[] { 1, 1, 1, 0, 0, 1, 1, 1, 0 } },
            { 3, new int[] { 1, 1, 1, 0, 1, 1, 0, 1, 1 } },
            { 4, new int[] { 1, 0, 1, 1, 1, 1, 0, 0, 1 } },
            { 5, new int[] { 1, 1, 1, 1, 0, 0, 0, 1, 1 } },
            { 6, new int[] { 1, 1, 1, 1, 0, 0, 1, 1, 1 } },
            { 7, new int[] { 1, 1, 1, 0, 0, 1, 0, 0, 1 } },
            { 8, new int[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 } },
            { 9, new int[] { 1, 1, 1, 1, 1, 1, 0, 1, 1 } },
        };

        public ZimukuCaptchaSolver(ILogger<ZimukuCaptchaSolver> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 解析 BMP 图片数据中的验证码文本
        /// </summary>
        /// <param name="bmpData">BMP 图片的原始字节数据</param>
        /// <returns>识别出的验证码字符串 (5 位数字)</returns>
        public string Solve(byte[] bmpData)
        {
            try
            {
                if (bmpData == null || bmpData.Length < 54)
                {
                    _logger.LogWarning("Zimuku Captcha | BMP data too small or null");
                    return null;
                }

                // 解析 BMP 头部获取图像尺寸和像素偏移
                int width = BitConverter.ToInt32(bmpData, 18);
                int height = Math.Abs(BitConverter.ToInt32(bmpData, 22));
                int bitsPerPixel = BitConverter.ToInt16(bmpData, 28);
                int dataOffset = BitConverter.ToInt32(bmpData, 10);

                _logger.LogInformation($"Zimuku Captcha | BMP: {width}x{height}, {bitsPerPixel}bpp, dataOffset={dataOffset}");

                if (width < 100 || height < 27)
                {
                    _logger.LogWarning($"Zimuku Captcha | Unexpected image dimensions: {width}x{height}");
                    return null;
                }

                // 每行字节数 (4 字节对齐)
                int bytesPerPixel = bitsPerPixel / 8;
                int rowStride = ((width * bytesPerPixel + 3) / 4) * 4;

                // 提取每个字符的采样像素并识别
                var result = new StringBuilder();

                for (int charIdx = 0; charIdx < NumChars; charIdx++)
                {
                    int offsetX = charIdx * (CharWidth + CharSpacing) + 5; // 5px 初始偏移
                    int[] samples = SampleCharacter(bmpData, dataOffset, rowStride, width, height, bytesPerPixel, offsetX);
                    int digit = MatchDigit(samples);
                    result.Append(digit);
                }

                var captchaText = result.ToString();
                _logger.LogInformation($"Zimuku Captcha | Solved -> {captchaText}");
                return captchaText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Zimuku Captcha | Solve exception -> {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从 BMP 图像中采样指定字符位置的 9 个像素亮度值
        /// </summary>
        private int[] SampleCharacter(byte[] bmpData, int dataOffset, int rowStride, int width, int height, int bytesPerPixel, int offsetX)
        {
            var samples = new int[SamplePositions.Length];

            for (int i = 0; i < SamplePositions.Length; i++)
            {
                int x = offsetX + SamplePositions[i].X;
                int y = SamplePositions[i].Y;

                // BMP 存储是从底到顶的，所以 y 要翻转
                int bmpY = height - 1 - y;

                int pixelOffset = dataOffset + (bmpY * rowStride) + (x * bytesPerPixel);

                if (pixelOffset >= 0 && pixelOffset < bmpData.Length)
                {
                    // 对于 24-bit BMP，像素顺序是 BGR
                    // 取灰度值 (简单平均)
                    byte r, g, b;
                    if (bytesPerPixel >= 3)
                    {
                        b = bmpData[pixelOffset];
                        g = bmpData[pixelOffset + 1];
                        r = bmpData[pixelOffset + 2];
                    }
                    else
                    {
                        r = g = b = bmpData[pixelOffset];
                    }

                    int gray = (r + g + b) / 3;
                    // 阈值 128: 高于此值认为是"亮"(笔画), 低于认为是"暗"(背景)
                    samples[i] = gray > 128 ? 1 : 0;
                }
                else
                {
                    samples[i] = 0;
                }
            }

            return samples;
        }

        /// <summary>
        /// 通过汉明距离匹配最相似的数字模板
        /// </summary>
        private int MatchDigit(int[] samples)
        {
            int bestDigit = 0;
            int bestScore = int.MaxValue;

            foreach (var kvp in DigitTemplates)
            {
                int distance = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i] != kvp.Value[i])
                    {
                        distance++;
                    }
                }

                if (distance < bestScore)
                {
                    bestScore = distance;
                    bestDigit = kvp.Key;
                }
            }

            _logger.LogDebug($"Zimuku Captcha | Matched digit: {bestDigit} (distance: {bestScore})");
            return bestDigit;
        }

        /// <summary>
        /// 将验证码答案转换为 hex 编码格式，用于提交到服务器
        /// 每个字符转换为其 ASCII 十六进制表示
        /// </summary>
        /// <param name="captchaText">验证码文本</param>
        /// <returns>hex 编码的字符串</returns>
        public static string ToHexEncoded(string captchaText)
        {
            if (string.IsNullOrEmpty(captchaText))
                return string.Empty;

            var sb = new StringBuilder();
            foreach (char c in captchaText)
            {
                sb.AppendFormat("{0:x2}", (int)c);
            }
            return sb.ToString();
        }
    }
}
