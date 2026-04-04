namespace Seeing.Agent.Tools.BuiltIn
{
    /// <summary>
    /// 二进制文件检测器 - 检测文件是否为二进制格式
    /// </summary>
    public static class BinaryFileDetector
    {
        /// <summary>
        /// 已知二进制扩展名
        /// </summary>
        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // 压缩文件
            ".zip", ".tar", ".gz", ".7z", ".rar", ".bz2",
            // 可执行文件
            ".exe", ".dll", ".so", ".dylib", ".a", ".lib", ".o", ".obj",
            // 字节码
            ".class", ".jar", ".war", ".pyc", ".pyo", ".wasm",
            // 办公文档
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".odt", ".ods", ".odp", ".pdf",
            // 媒体文件
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
            ".mp3", ".mp4", ".wav", ".avi", ".mkv", ".mov",
            ".ttf", ".otf", ".woff", ".woff2", ".eot",
            // 数据库
            ".db", ".sqlite", ".mdb",
            // 其他
            ".bin", ".dat", ".iso", ".img"
        };

        /// <summary>
        /// 图片扩展名（可作为附件返回）
        /// </summary>
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp"
        };

        /// <summary>
        /// 检测文件是否为二进制文件
        /// </summary>
        public static bool IsBinaryFile(string filePath)
        {
            // 检查扩展名
            var ext = Path.GetExtension(filePath);
            if (BinaryExtensions.Contains(ext))
                return true;

            // 检查文件内容
            return IsBinaryContent(filePath);
        }

        /// <summary>
        /// 检测文件是否为图片
        /// </summary>
        public static bool IsImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ImageExtensions.Contains(ext);
        }

        /// <summary>
        /// 检测内容是否为二进制
        /// </summary>
        private static bool IsBinaryContent(string filePath, int sampleSize = 4096)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                if (fs.Length == 0)
                    return false;

                var bytes = new byte[Math.Min(sampleSize, fs.Length)];
                var bytesRead = fs.Read(bytes, 0, bytes.Length);

                if (bytesRead == 0)
                    return false;

                // 检查 null 字节（二进制文件的强信号）
                for (int i = 0; i < bytesRead; i++)
                {
                    if (bytes[i] == 0)
                        return true;
                }

                // 计算非打印字符比例
                var nonPrintableCount = 0;
                for (int i = 0; i < bytesRead; i++)
                {
                    var b = bytes[i];
                    // 控制字符（除了 \t, \n, \r）
                    if (b < 9 || (b > 13 && b < 32))
                    {
                        nonPrintableCount++;
                    }
                }

                // 如果非打印字符超过 30%，认为是二进制文件
                return (double)nonPrintableCount / bytesRead > 0.3;
            }
            catch
            {
                // 无法读取时假设为二进制
                return true;
            }
        }

        /// <summary>
        /// 获取文件 MIME 类型
        /// </summary>
        public static string GetMimeType(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                // 文本
                ".txt" => "text/plain",
                ".html" or ".htm" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                
                // 图片
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                
                // 文档
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                
                // 其他
                _ => "application/octet-stream"
            };
        }
    }
}