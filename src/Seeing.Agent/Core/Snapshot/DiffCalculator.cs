using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// Diff 计算器 - 简化的 Myers diff 算法实现
    /// </summary>
    public class DiffCalculator
    {
        /// <summary>计算两个文本之间的 Diff</summary>
        public List<DiffLine> ComputeDiff(string text1, string text2)
        {
            var lines1 = text1.Split('\n');
            var lines2 = text2.Split('\n');
            var result = new List<DiffLine>();

            var lcs = LongestCommonSubsequence(lines1, lines2);
            
            int i1 = 0, i2 = 0, lcsIndex = 0;
            
            while (i1 < lines1.Length || i2 < lines2.Length)
            {
                if (lcsIndex < lcs.Count)
                {
                    var (lcsLine1, lcsLine2) = lcs[lcsIndex];
                    
                    // 处理删除的行
                    while (i1 < lcsLine1)
                    {
                        result.Add(new DiffLine(DiffOperation.Delete, lines1[i1]));
                        i1++;
                    }
                    
                    // 处理新增的行
                    while (i2 < lcsLine2)
                    {
                        result.Add(new DiffLine(DiffOperation.Insert, lines2[i2]));
                        i2++;
                    }
                    
                    // 处理相同的行
                    if (i1 < lines1.Length && i2 < lines2.Length)
                    {
                        result.Add(new DiffLine(DiffOperation.Equal, lines1[i1]));
                        i1++;
                        i2++;
                        lcsIndex++;
                    }
                }
                else
                {
                    // 处理剩余的行
                    while (i1 < lines1.Length)
                    {
                        result.Add(new DiffLine(DiffOperation.Delete, lines1[i1]));
                        i1++;
                    }
                    while (i2 < lines2.Length)
                    {
                        result.Add(new DiffLine(DiffOperation.Insert, lines2[i2]));
                        i2++;
                    }
                }
            }

            return result;
        }

        /// <summary>应用补丁恢复内容</summary>
        public string ApplyPatch(string originalText, List<DiffLine> diffs)
        {
            var result = new List<string>();
            var originalLines = originalText.Split('\n');
            int origIndex = 0;

            foreach (var diff in diffs)
            {
                switch (diff.Operation)
                {
                    case DiffOperation.Equal:
                        if (origIndex < originalLines.Length)
                        {
                            result.Add(originalLines[origIndex]);
                            origIndex++;
                        }
                        break;
                    case DiffOperation.Delete:
                        origIndex++; // 跳过原始行
                        break;
                    case DiffOperation.Insert:
                        result.Add(diff.Content);
                        break;
                }
            }

            return string.Join("\n", result);
        }

        /// <summary>序列化补丁</summary>
        public string SerializePatch(List<DiffLine> diffs)
        {
            var sb = new StringBuilder();
            foreach (var diff in diffs)
            {
                var prefix = diff.Operation switch
                {
                    DiffOperation.Equal => " ",
                    DiffOperation.Insert => "+",
                    DiffOperation.Delete => "-",
                    _ => " "
                };
                sb.AppendLine($"{prefix}{diff.Content}");
            }
            return sb.ToString();
        }

        /// <summary>反序列化补丁</summary>
        public List<DiffLine> DeserializePatch(string serialized)
        {
            var result = new List<DiffLine>();
            var lines = serialized.Split('\n');
            
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                
                var operation = line[0] switch
                {
                    ' ' => DiffOperation.Equal,
                    '+' => DiffOperation.Insert,
                    '-' => DiffOperation.Delete,
                    _ => DiffOperation.Equal
                };
                
                result.Add(new DiffLine(operation, line[1..]));
            }

            return result;
        }

        /// <summary>生成 Unified Diff 格式</summary>
        public string ToUnifiedDiff(string filePath, string text1, string text2)
        {
            var diffs = ComputeDiff(text1, text2);
            var sb = new StringBuilder();
            
            sb.AppendLine($"--- {filePath}");
            sb.AppendLine($"+++ {filePath}");
            
            var lines1 = text1.Split('\n');
            var lines2 = text2.Split('\n');
            
            sb.AppendLine($"@@ -1,{lines1.Length} +1,{lines2.Length} @@");
            
            foreach (var diff in diffs)
            {
                var prefix = diff.Operation switch
                {
                    DiffOperation.Equal => " ",
                    DiffOperation.Insert => "+",
                    DiffOperation.Delete => "-",
                    _ => " "
                };
                sb.AppendLine($"{prefix}{diff.Content}");
            }

            return sb.ToString();
        }

        private List<(int, int)> LongestCommonSubsequence(string[] lines1, string[] lines2)
        {
            var dp = new int[lines1.Length + 1, lines2.Length + 1];
            
            for (int i = 1; i <= lines1.Length; i++)
            {
                for (int j = 1; j <= lines2.Length; j++)
                {
                    if (lines1[i - 1] == lines2[j - 1])
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            var result = new List<(int, int)>();
            int i1 = lines1.Length, i2 = lines2.Length;
            
            while (i1 > 0 && i2 > 0)
            {
                if (lines1[i1 - 1] == lines2[i2 - 1])
                {
                    result.Add((i1 - 1, i2 - 1));
                    i1--;
                    i2--;
                }
                else if (dp[i1 - 1, i2] > dp[i1, i2 - 1])
                {
                    i1--;
                }
                else
                {
                    i2--;
                }
            }

            result.Reverse();
            return result;
        }
    }

    /// <summary>Diff 行</summary>
    public record DiffLine(DiffOperation Operation, string Content);

    /// <summary>Diff 操作类型</summary>
    public enum DiffOperation
    {
        Equal,
        Insert,
        Delete
    }
}
