using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Emby.MeiamSub.Zimuku.Captcha
{
    public class ZimukuCaptchaSolver
    {
        private readonly ILogger _logger;

        // 9 pixel sample positions per character (x, y) for template matching
        private static readonly (int X, int Y)[] SamplePositions = new (int, int)[]
        {
            (3, 3),   // top-left
            (10, 3),  // top-center
            (16, 3),  // top-right
            (3, 13),  // middle-left
            (10, 13), // center
            (16, 13), // middle-right
            (3, 23),  // bottom-left
            (10, 23), // bottom-center
            (16, 23), // bottom-right
        };

        private const int CharWidth = 17;
        private const int CharSpacing = 3;
        private const int NumChars = 5;

        // Digit templates: 9-element feature vectors (1=bright, 0=dark)
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

        public ZimukuCaptchaSolver(ILogger logger)
        {
            _logger = logger;
        }

        public string Solve(byte[] bmpData)
        {
            try
            {
                if (bmpData == null || bmpData.Length < 54)
                {
                    _logger.Warn("Zimuku Captcha | BMP data too small or null");
                    return null;
                }

                // Parse BMP header for dimensions and pixel offset
                int width = BitConverter.ToInt32(bmpData, 18);
                int height = Math.Abs(BitConverter.ToInt32(bmpData, 22));
                int bitsPerPixel = BitConverter.ToInt16(bmpData, 28);
                int dataOffset = BitConverter.ToInt32(bmpData, 10);

                _logger.Info("Zimuku Captcha | BMP: {0}x{1}, {2}bpp, dataOffset={3}", width, height, bitsPerPixel, dataOffset);

                if (width < 100 || height < 27)
                {
                    _logger.Warn("Zimuku Captcha | Unexpected image dimensions: {0}x{1}", width, height);
                    return null;
                }

                int bytesPerPixel = bitsPerPixel / 8;
                int rowStride = ((width * bytesPerPixel + 3) / 4) * 4;

                var result = new StringBuilder();

                for (int charIdx = 0; charIdx < NumChars; charIdx++)
                {
                    int offsetX = charIdx * (CharWidth + CharSpacing) + 5; // 5px initial offset
                    int[] samples = SampleCharacter(bmpData, dataOffset, rowStride, width, height, bytesPerPixel, offsetX);
                    int digit = MatchDigit(samples);
                    result.Append(digit);
                }

                var captchaText = result.ToString();
                _logger.Info("Zimuku Captcha | Solved -> {0}", captchaText);
                return captchaText;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Zimuku Captcha | Solve exception -> " + ex.Message, ex);
                return null;
            }
        }

        private int[] SampleCharacter(byte[] bmpData, int dataOffset, int rowStride, int width, int height, int bytesPerPixel, int offsetX)
        {
            var samples = new int[SamplePositions.Length];

            for (int i = 0; i < SamplePositions.Length; i++)
            {
                int x = offsetX + SamplePositions[i].X;
                int y = SamplePositions[i].Y;

                // BMP stores bottom-to-top, so flip y
                int bmpY = height - 1 - y;

                int pixelOffset = dataOffset + (bmpY * rowStride) + (x * bytesPerPixel);

                if (pixelOffset >= 0 && pixelOffset < bmpData.Length)
                {
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
                    // Threshold 128: above = "bright" (stroke), below = "dark" (background)
                    samples[i] = gray > 128 ? 1 : 0;
                }
                else
                {
                    samples[i] = 0;
                }
            }

            return samples;
        }

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

            _logger.Debug("Zimuku Captcha | Matched digit: {0} (distance: {1})", bestDigit, bestScore);
            return bestDigit;
        }

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
