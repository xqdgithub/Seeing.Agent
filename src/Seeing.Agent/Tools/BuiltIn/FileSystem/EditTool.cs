using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 文件编辑工具
    /// <para>
    /// 对文件进行精确的字符串替换编辑。
    /// 支持多种智能匹配策略，处理缩进、空白、转义字符等差异。
    /// </para>
    /// </summary>
    public class EditTool : ToolBase
    {
        /// <summary>
        /// 创建 EditTool 实例
        /// </summary>
        public EditTool(ILogger<EditTool> logger) : base(logger)
        {
        }

        public override string Id => "edit";

        public override string Description =>
            "对文件进行精确的字符串替换编辑。\n\n" +
            "使用此工具对现有文件进行精确编辑。工具会尝试多种匹配策略来找到要替换的内容。\n" +
            "必须先使用 Read 工具读取文件，确保了解要编辑的内容。\n" +
            "oldString 和 newString 必须完全匹配，包括空白和缩进。";

        public ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => BuildParametersSchema();

        private JsonElement BuildParametersSchema()
        {
            var schema = new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "要修改的文件的绝对路径"
                    },
                    oldString = new
                    {
                        type = "string",
                        description = "要替换的文本"
                    },
                    newString = new
                    {
                        type = "string",
                        description = "替换后的文本（必须与 oldString 不同）"
                    },
                    replaceAll = new
                    {
                        type = "boolean",
                        description = "替换所有匹配项（默认为 false）"
                    }
                },
                required = new[] { "filePath", "oldString", "newString" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取 filePath 参数
            var filePath = GetStringArgument(arguments, "filePath");
            if (string.IsNullOrEmpty(filePath))
            {
                return Failure("缺少必需参数: filePath");
            }

            // 获取 oldString 参数
            var oldString = GetStringArgument(arguments, "oldString");
            if (oldString == null)
            {
                return Failure("缺少必需参数: oldString");
            }

            // 获取 newString 参数
            var newString = GetStringArgument(arguments, "newString");
            if (newString == null)
            {
                return Failure("缺少必需参数: newString");
            }

            // 获取 replaceAll 参数
            var replaceAll = GetBoolArgument(arguments, "replaceAll") ?? false;

            // 检查 oldString 和 newString 是否相同
            if (oldString == newString)
            {
                return Failure("没有需要应用的更改: oldString 和 newString 相同。");
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.GetFullPath(filePath);
            }

            _logger.LogInformation("编辑文件: {FilePath}", filePath);

            // 检查文件是否存在
            if (!File.Exists(filePath))
            {
                // 如果 oldString 为空，可以创建新文件
                if (oldString == "")
                {
                    return await CreateNewFileAsync(filePath, newString, context);
                }
                return Failure($"文件不存在: {filePath}");
            }

            // 检查是否为目录
            if (Directory.Exists(filePath))
            {
                return Failure($"路径是目录而非文件: {filePath}");
            }

            // 读取文件内容
            var oldContent = await ReadFileContentAsync(filePath);

            // 执行替换
            string newContent;
            try
            {
                newContent = ReplaceContent(oldContent, oldString, newString, replaceAll);
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }

            // 生成 diff
            var diff = GenerateDiff(filePath, oldContent, newContent);

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "edit",
                    Patterns = new List<string> { filePath },
                    Metadata = new Dictionary<string, object>
                    {
                        ["filePath"] = filePath,
                        ["diff"] = diff
                    }
                });
            }

            // 写入文件
            try
            {
                await WriteFileContentAsync(filePath, newContent);
            }
            catch (Exception ex)
            {
                return Failure(ex, "写入文件失败");
            }

            return Success(
                Path.GetFileName(filePath) ?? filePath,
                "编辑应用成功。",
                new Dictionary<string, object>
                {
                    ["filePath"] = filePath,
                    ["diff"] = diff
                }
            );
        }

        /// <summary>
        /// 创建新文件
        /// </summary>
        private async Task<ToolResult> CreateNewFileAsync(string filePath, string content, ToolContext context)
        {
            // 生成 diff
            var diff = GenerateDiff(filePath, "", content);

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "edit",
                    Patterns = new List<string> { filePath },
                    Metadata = new Dictionary<string, object>
                    {
                        ["filePath"] = filePath,
                        ["diff"] = diff,
                        ["exists"] = false
                    }
                });
            }

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 写入文件
            try
            {
                await WriteFileContentAsync(filePath, content);
            }
            catch (Exception ex)
            {
                return Failure(ex, "创建文件失败");
            }

            return Success(
                Path.GetFileName(filePath) ?? filePath,
                "新文件已创建。",
                new Dictionary<string, object>
                {
                    ["filePath"] = filePath,
                    ["exists"] = false
                }
            );
        }

        /// <summary>
        /// 异步读取文件内容
        /// </summary>
        private async Task<string> ReadFileContentAsync(string filePath)
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        /// 异步写入文件内容
        /// </summary>
        private async Task WriteFileContentAsync(string filePath, string content)
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            await writer.WriteAsync(content);
        }

        /// <summary>
        /// 执行内容替换（尝试多种匹配策略）
        /// </summary>
        private string ReplaceContent(string content, string oldString, string newString, bool replaceAll)
        {
            // 尝试多种替换策略
            var strategies = new List<IReplaceStrategy>
            {
                new SimpleReplaceStrategy(),
                new LineTrimmedReplaceStrategy(),
                new BlockAnchorReplaceStrategy(),
                new WhitespaceNormalizedReplaceStrategy(),
                new IndentationFlexibleReplaceStrategy(),
                new TrimmedBoundaryReplaceStrategy(),
                new MultiOccurrenceReplaceStrategy()
            };

            foreach (var strategy in strategies)
            {
                foreach (var search in strategy.FindMatches(content, oldString))
                {
                    var index = content.IndexOf(search);
                    if (index == -1) continue;

                    if (replaceAll)
                    {
                        return content.Replace(search, newString);
                    }

                    // 检查是否有多个匹配
                    var lastIndex = content.LastIndexOf(search);
                    if (index != lastIndex)
                    {
                        // 继续尝试其他策略
                        continue;
                    }

                    // 执行替换
                    return content.Substring(0, index) + newString + content.Substring(index + search.Length);
                }
            }

            // 如果所有策略都失败
            throw new Exception(
                "无法在文件中找到 oldString。必须完全匹配，包括空白、缩进和行尾。\n" +
                "如果文件中有多个相同的匹配项，请提供更多上下文以使匹配唯一，或使用 replaceAll=true。");
        }

        /// <summary>
        /// 生成 diff 输出
        /// </summary>
        private string GenerateDiff(string filePath, string oldContent, string newContent)
        {
            oldContent = FileSystemHelper.NormalizeLineEndings(oldContent);
            newContent = FileSystemHelper.NormalizeLineEndings(newContent);

            var oldLines = oldContent.Split('\n');
            var newLines = newContent.Split('\n');

            var diffLines = new List<string>
            {
                $"--- {filePath}",
                $"+++ {filePath}"
            };

            // 计算最小公共缩进并移除
            var minIndent = CalculateMinIndent(diffLines);

            var maxLines = Math.Max(oldLines.Length, newLines.Length);
            for (var i = 0; i < maxLines; i++)
            {
                var oldLine = i < oldLines.Length ? oldLines[i] : null;
                var newLine = i < newLines.Length ? newLines[i] : null;

                if (oldLine != newLine)
                {
                    if (oldLine != null)
                    {
                        diffLines.Add($"-{oldLine}");
                    }
                    if (newLine != null)
                    {
                        diffLines.Add($"+{newLine}");
                    }
                }
                else if (oldLine != null)
                {
                    diffLines.Add($" {oldLine}");
                }
            }

            return string.Join("\n", diffLines);
        }

        /// <summary>
        /// 计算最小公共缩进
        /// </summary>
        private int CalculateMinIndent(List<string> diffLines)
        {
            var minIndent = int.MaxValue;
            
            foreach (var line in diffLines)
            {
                if (line.StartsWith("---") || line.StartsWith("+++")) continue;
                if (!line.StartsWith("+") && !line.StartsWith("-") && !line.StartsWith(" ")) continue;
                
                var content = line.Substring(1);
                if (string.IsNullOrWhiteSpace(content)) continue;
                
                var indent = 0;
                while (indent < content.Length && (content[indent] == ' ' || content[indent] == '\t'))
                {
                    indent++;
                }
                
                minIndent = Math.Min(minIndent, indent);
            }

            return minIndent == int.MaxValue ? 0 : minIndent;
        }
    }

    /// <summary>
    /// 替换策略接口
    /// </summary>
    internal interface IReplaceStrategy
    {
        /// <summary>
        /// 查找所有匹配项
        /// </summary>
        IEnumerable<string> FindMatches(string content, string find);
    }

    /// <summary>
    /// 简单替换策略 - 精确匹配
    /// </summary>
    internal class SimpleReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            if (content.Contains(find))
            {
                yield return find;
            }
        }
    }

    /// <summary>
    /// 行修剪替换策略 - 每行单独修剪后匹配
    /// </summary>
    internal class LineTrimmedReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            var originalLines = content.Split('\n');
            var searchLines = find.Split('\n');

            // 移除末尾空行
            if (searchLines.Length > 0 && searchLines[searchLines.Length - 1] == "")
            {
                searchLines = searchLines.Take(searchLines.Length - 1).ToArray();
            }

            for (var i = 0; i <= originalLines.Length - searchLines.Length; i++)
            {
                var matches = true;
                for (var j = 0; j < searchLines.Length; j++)
                {
                    if (originalLines[i + j].Trim() != searchLines[j].Trim())
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    // 计算匹配的起始和结束位置
                    var startIndex = 0;
                    for (var k = 0; k < i; k++)
                    {
                        startIndex += originalLines[k].Length + 1;
                    }

                    var endIndex = startIndex;
                    for (var k = 0; k < searchLines.Length; k++)
                    {
                        endIndex += originalLines[i + k].Length;
                        if (k < searchLines.Length - 1)
                        {
                            endIndex += 1;
                        }
                    }

                    yield return content.Substring(startIndex, endIndex - startIndex);
                }
            }
        }
    }

    /// <summary>
    /// 块锚点替换策略 - 使用首尾行作为锚点匹配块
    /// </summary>
    internal class BlockAnchorReplaceStrategy : IReplaceStrategy
    {
        private const float SingleCandidateSimilarityThreshold = 0.0f;
        private const float MultipleCandidatesSimilarityThreshold = 0.3f;

        public IEnumerable<string> FindMatches(string content, string find)
        {
            var originalLines = content.Split('\n');
            var searchLines = find.Split('\n');

            if (searchLines.Length < 3) yield break;

            // 移除末尾空行
            if (searchLines[searchLines.Length - 1] == "")
            {
                searchLines = searchLines.Take(searchLines.Length - 1).ToArray();
            }

            var firstLineSearch = searchLines[0].Trim();
            var lastLineSearch = searchLines[searchLines.Length - 1].Trim();
            var searchBlockSize = searchLines.Length;

            // 收集所有候选位置
            var candidates = new List<(int StartLine, int EndLine)>();
            for (var i = 0; i < originalLines.Length; i++)
            {
                if (originalLines[i].Trim() != firstLineSearch) continue;

                for (var j = i + 2; j < originalLines.Length; j++)
                {
                    if (originalLines[j].Trim() == lastLineSearch)
                    {
                        candidates.Add((i, j));
                        break;
                    }
                }
            }

            if (candidates.Count == 0) yield break;

            // 单候选场景
            if (candidates.Count == 1)
            {
                var (startLine, endLine) = candidates[0];
                var actualBlockSize = endLine - startLine + 1;

                var similarity = CalculateSimilarity(originalLines, searchLines, startLine, actualBlockSize, searchBlockSize);

                if (similarity >= SingleCandidateSimilarityThreshold)
                {
                    yield return ExtractBlock(content, originalLines, startLine, endLine);
                }
                yield break;
            }

            // 多候选场景 - 选择最相似的
            var bestMatch = (StartLine: -1, EndLine: -1);
            var maxSimilarity = -1f;

            foreach (var candidate in candidates)
            {
                var (startLine, endLine) = candidate;
                var actualBlockSize = endLine - startLine + 1;

                var similarity = CalculateSimilarity(originalLines, searchLines, startLine, actualBlockSize, searchBlockSize);

                if (similarity > maxSimilarity)
                {
                    maxSimilarity = similarity;
                    bestMatch = (startLine, endLine);
                }
            }

            if (maxSimilarity >= MultipleCandidatesSimilarityThreshold && bestMatch.StartLine >= 0)
            {
                yield return ExtractBlock(content, originalLines, bestMatch.StartLine, bestMatch.EndLine);
            }
        }

        private float CalculateSimilarity(string[] originalLines, string[] searchLines, int startLine, int actualBlockSize, int searchBlockSize)
        {
            var linesToCheck = Math.Min(searchBlockSize - 2, actualBlockSize - 2);
            if (linesToCheck <= 0) return 1.0f;

            var similarity = 0f;
            for (var j = 1; j < searchBlockSize - 1 && j < actualBlockSize - 1; j++)
            {
                var originalLine = originalLines[startLine + j].Trim();
                var searchLine = searchLines[j].Trim();
                var maxLen = Math.Max(originalLine.Length, searchLine.Length);
                if (maxLen == 0) continue;

                var distance = FileSystemHelper.LevenshteinDistance(originalLine, searchLine);
                similarity += (1 - distance / maxLen) / linesToCheck;
            }

            return similarity;
        }

        private string ExtractBlock(string content, string[] lines, int startLine, int endLine)
        {
            var startIndex = 0;
            for (var k = 0; k < startLine; k++)
            {
                startIndex += lines[k].Length + 1;
            }

            var endIndex = startIndex;
            for (var k = startLine; k <= endLine; k++)
            {
                endIndex += lines[k].Length;
                if (k < endLine) endIndex += 1;
            }

            return content.Substring(startIndex, endIndex - startIndex);
        }
    }

    /// <summary>
    /// 空白规范化替换策略
    /// </summary>
    internal class WhitespaceNormalizedReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            var normalizeWhitespace = (string text) => 
                System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            
            var normalizedFind = normalizeWhitespace(find);
            var lines = content.Split('\n');

            // 单行匹配
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (normalizeWhitespace(line) == normalizedFind)
                {
                    yield return line;
                }
            }

            // 多行匹配
            var findLines = find.Split('\n');
            if (findLines.Length > 1)
            {
                for (var i = 0; i <= lines.Length - findLines.Length; i++)
                {
                    var block = lines.Skip(i).Take(findLines.Length).ToArray();
                    if (normalizeWhitespace(string.Join("\n", block)) == normalizedFind)
                    {
                        yield return string.Join("\n", block);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 缩进灵活替换策略 - 忽略缩进差异
    /// </summary>
    internal class IndentationFlexibleReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            var removeIndentation = (string text) =>
            {
                var lines = text.Split('\n');
                var nonEmptyLines = lines.Where(l => l.Trim().Length > 0).ToList();
                if (nonEmptyLines.Count == 0) return text;

                var minIndent = nonEmptyLines.Min(l =>
                {
                    var match = System.Text.RegularExpressions.Regex.Match(l, @"^(\s*)");
                    return match.Success ? match.Groups[1].Value.Length : 0;
                });

                return string.Join("\n", lines.Select(l => l.Trim().Length == 0 ? l : l.Substring(minIndent)));
            };

            var normalizedFind = removeIndentation(find);
            var contentLines = content.Split('\n');
            var findLines = find.Split('\n');

            for (var i = 0; i <= contentLines.Length - findLines.Length; i++)
            {
                var block = contentLines.Skip(i).Take(findLines.Length).ToArray();
                if (removeIndentation(string.Join("\n", block)) == normalizedFind)
                {
                    yield return string.Join("\n", block);
                }
            }
        }
    }

    /// <summary>
    /// 修剪边界替换策略
    /// </summary>
    internal class TrimmedBoundaryReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            var trimmedFind = find.Trim();
            if (trimmedFind == find) yield break;

            if (content.Contains(trimmedFind))
            {
                yield return trimmedFind;
            }

            var lines = content.Split('\n');
            var findLines = find.Split('\n');

            for (var i = 0; i <= lines.Length - findLines.Length; i++)
            {
                var block = lines.Skip(i).Take(findLines.Length).ToArray();
                if (string.Join("\n", block).Trim() == trimmedFind)
                {
                    yield return string.Join("\n", block);
                }
            }
        }
    }

    /// <summary>
    /// 多匹配项策略 - 返回所有精确匹配
    /// </summary>
    internal class MultiOccurrenceReplaceStrategy : IReplaceStrategy
    {
        public IEnumerable<string> FindMatches(string content, string find)
        {
            var startIndex = 0;
            while (true)
            {
                var index = content.IndexOf(find, startIndex);
                if (index == -1) break;

                yield return find;
                startIndex = index + find.Length;
            }
        }
    }
}
