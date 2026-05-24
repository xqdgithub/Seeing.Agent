using System.Text;

namespace Seeing.Agent.Memory.Core
{
    /// <summary>
    /// MemoryChunker provides a simple 50KB chunking mechanism for large content.
    /// It chunks by Markdown-like paragraphs when possible, preserving structure.
    /// If a single paragraph exceeds the chunk size, it will be subdivided by lines.
    /// Each produced chunk is annotated with a small HTML comment marker to denote its ordinal.
    /// </summary>
    public static class MemoryChunker
    {
        // 50 KB in bytes
        public const int ChunkSizeBytes = 50 * 1024;

        /// <summary>
        /// Determine whether the content should be chunked based on UTF-8 byte size.
        /// </summary>
        public static bool ShouldChunk(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return Encoding.UTF8.GetByteCount(content) > ChunkSizeBytes;
        }

        /// <summary>
        /// Split content into a list of chunks, each not exceeding 50 KB.
        /// Chunking strategy:
        /// - Prefer paragraph-based chunks (split on double newlines).
        /// - If a paragraph would overflow, flush current chunk and continue with the paragraph.
        /// - If a single paragraph itself is larger than max, split it by lines to respect Markdown structure.
        /// - Each chunk is annotated with an HTML comment marker to denote its id.
        /// </summary>
        public static List<string> ChunkContent(string content)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(content))
            {
                result.Add(string.Empty);
                return result;
            }

            // Normalize newlines to a consistent form
            string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");

            // Split into logical paragraphs. A paragraph here is a block separated by two newlines.
            var paragraphs = SplitIntoParagraphs(normalized);

            var currentChunk = new StringBuilder();
            int currentBytes = 0;
            int chunkIndex = 1;

            for (int i = 0; i < paragraphs.Count; i++)
            {
                string para = paragraphs[i];
                string addition = para;
                if (i < paragraphs.Count - 1)
                {
                    addition += "\n\n";
                }
                int additionBytes = Encoding.UTF8.GetByteCount(addition);
                if (currentBytes + additionBytes <= ChunkSizeBytes)
                {
                    currentChunk.Append(addition);
                    currentBytes += additionBytes;
                }
                else
                {
                    if (currentChunk.Length > 0)
                    {
                        result.Add(AnnotateChunk(currentChunk.ToString(), chunkIndex++));
                        currentChunk.Clear();
                        currentBytes = 0;
                    }

                    if (additionBytes <= ChunkSizeBytes)
                    {
                        currentChunk.Append(addition);
                        currentBytes = additionBytes;
                    }
                    else
                    {
                        // The addition (paragraph) is too large for a single chunk.
                        // Break it down by lines to respect markdown structure.
                        var lineSegments = SplitParagraphByLines(para, ChunkSizeBytes);
                        for (int s = 0; s < lineSegments.Count; s++)
                        {
                            string seg = lineSegments[s];
                            int segBytes = Encoding.UTF8.GetByteCount(seg);
                            if (currentBytes + segBytes <= ChunkSizeBytes)
                            {
                                currentChunk.Append(seg);
                                currentBytes += segBytes;
                            }
                            else
                            {
                                if (currentChunk.Length > 0)
                                {
                                    result.Add(AnnotateChunk(currentChunk.ToString(), chunkIndex++));
                                    currentChunk.Clear();
                                    currentBytes = 0;
                                }
                                currentChunk.Append(seg);
                                currentBytes += segBytes;
                            }
                        }
                    }
                }
            }

            if (currentChunk.Length > 0)
            {
                result.Add(AnnotateChunk(currentChunk.ToString(), chunkIndex++));
            }

            return result;
        }

        /// <summary>
        /// Merge multiple chunks back into a single content blob.
        /// </summary>
        public static string MergeChunks(IEnumerable<string> chunks)
        {
            if (chunks == null) return string.Empty;
            // Simply join with blank lines between chunks to preserve separation
            return string.Join("\n\n", chunks);
        }

        // Helpers
        private static List<string> SplitIntoParagraphs(string text)
        {
            var parts = text.Split(new[] { "\n\n" }, StringSplitOptions.None);
            return parts.ToList();
        }

        private static List<string> SplitParagraphByLines(string paragraph, int maxBytes)
        {
            // Split paragraph into lines first, then pack lines into chunks
            var lines = paragraph.Split(new[] { '\n' }, StringSplitOptions.None);
            var segments = new List<string>();
            var current = new StringBuilder();
            int currentBytes = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string lineWithNewline = line + (i == lines.Length - 1 ? "" : "\n");
                int lineBytes = Encoding.UTF8.GetByteCount(lineWithNewline);
                if (currentBytes + lineBytes <= maxBytes)
                {
                    current.Append(lineWithNewline);
                    currentBytes += lineBytes;
                }
                else
                {
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                        currentBytes = 0;
                    }
                    if (lineBytes > maxBytes)
                    {
                        // split the line into smaller pieces
                        var sub = TrimToBytes(lineWithNewline, maxBytes);
                        if (!string.IsNullOrEmpty(sub)) segments.Add(sub);
                    }
                    else
                    {
                        current.Append(lineWithNewline);
                        currentBytes += lineBytes;
                    }
                }
            }
            if (current.Length > 0) segments.Add(current.ToString());
            if (segments.Count == 0 && !string.IsNullOrEmpty(paragraph))
            {
                segments.Add(paragraph);
            }
            return segments;
        }

        private static string TrimToBytes(string input, int maxBytes)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Build by incremental chars until maxBytes is reached
            var sb = new StringBuilder();
            int bytes = 0;
            foreach (var ch in input)
            {
                int b = Encoding.UTF8.GetByteCount(new[] { ch });
                if (bytes + b > maxBytes) break;
                sb.Append(ch);
                bytes += b;
            }
            return sb.ToString();
        }

        private static string AnnotateChunk(string content, int index)
        {
            // Use a lightweight HTML comment marker to denote chunk index without affecting Markdown rendering
            return $"<!-- _chunk{index} -->\n{content}";
        }
    }
}
