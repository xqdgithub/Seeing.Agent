namespace Seeing.Agent.Tools.BuiltIn
{
    /// <summary>
    /// 输出截断器 - 限制输出大小防止内存溢出
    /// </summary>
    public static class OutputTruncator
    {
        /// <summary>
        /// 默认最大行数
        /// </summary>
        public const int DefaultMaxLines = 2000;

        /// <summary>
        /// 默认最大字节数 (50KB)
        /// </summary>
        public const int DefaultMaxBytes = 50 * 1024;

        /// <summary>
        /// 默认最大行长度
        /// </summary>
        public const int DefaultMaxLineLength = 2000;

        /// <summary>
        /// 截断结果
        /// </summary>
        public class TruncationResult
        {
            /// <summary>截断后的内容</summary>
            public string Content { get; set; } = string.Empty;

            /// <summary>是否被截断</summary>
            public bool Truncated { get; set; }

            /// <summary>原始行数</summary>
            public int TotalLines { get; set; }

            /// <summary>保留行数</summary>
            public int KeptLines { get; set; }

            /// <summary>截断消息</summary>
            public string? TruncationMessage { get; set; }
        }

        /// <summary>
        /// 截断文本输出
        /// </summary>
        public static TruncationResult Truncate(
            string content,
            int maxLines = DefaultMaxLines,
            int maxBytes = DefaultMaxBytes,
            int maxLineLength = DefaultMaxLineLength)
        {
            if (string.IsNullOrEmpty(content))
            {
                return new TruncationResult
                {
                    Content = content,
                    Truncated = false,
                    TotalLines = 0,
                    KeptLines = 0
                };
            }

            var lines = content.Split('\n');
            var totalLines = lines.Length;
            var keptLines = new List<string>();
            var totalBytes = 0L;
            var truncated = false;
            string? truncationMessage = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // 截断过长的行
                if (line.Length > maxLineLength)
                {
                    line = line.Substring(0, maxLineLength) + $"... (行截断至 {maxLineLength} 字符)";
                    truncated = true;
                }

                // 检查行数限制
                if (keptLines.Count >= maxLines)
                {
                    truncated = true;
                    truncationMessage = $"(显示前 {maxLines} 行，共 {totalLines} 行)";
                    break;
                }

                // 检查字节限制
                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
                if (totalBytes + lineBytes > maxBytes)
                {
                    truncated = true;
                    truncationMessage = $"(输出已达到 {maxBytes / 1024}KB 限制)";
                    break;
                }

                keptLines.Add(line);
                totalBytes += lineBytes;
            }

            var resultContent = string.Join("\n", keptLines);
            if (truncated && truncationMessage != null)
            {
                resultContent += "\n\n" + truncationMessage;
            }

            return new TruncationResult
            {
                Content = resultContent,
                Truncated = truncated,
                TotalLines = totalLines,
                KeptLines = keptLines.Count,
                TruncationMessage = truncationMessage
            };
        }

        /// <summary>
        /// 格式化行号输出
        /// </summary>
        public static string FormatWithLineNumbers(
            string content,
            int startLine = 1,
            int maxLineLength = DefaultMaxLineLength)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            var lines = content.Split('\n');
            var formatted = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > maxLineLength)
                {
                    line = line.Substring(0, maxLineLength) + $"... (行截断至 {maxLineLength} 字符)";
                }
                formatted.Add($"{startLine + i}: {line}");
            }

            return string.Join("\n", formatted);
        }
    }
}