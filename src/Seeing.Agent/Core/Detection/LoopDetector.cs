using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Core.Detection;

/// <summary>
/// 循环检测结果
/// </summary>
public record LoopDetectionResult
{
    /// <summary>
    /// 是否检测到循环
    /// </summary>
    public bool IsLoop { get; init; }

    /// <summary>
    /// 连续调用次数
    /// </summary>
    public int ConsecutiveCount { get; init; }

    /// <summary>
    /// 涉及的工具名称
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// 推荐的处理动作
    /// </summary>
    public LoopAction RecommendedAction { get; init; }
}

/// <summary>
/// 循环处理动作
/// </summary>
public enum LoopAction
{
    /// <summary>
    /// 继续执行
    /// </summary>
    Continue,

    /// <summary>
    /// 发出警告
    /// </summary>
    Warn,

    /// <summary>
    /// 终止执行
    /// </summary>
    Terminate
}

/// <summary>
/// Doom Loop 检测器，用于检测连续相同的工具调用
/// </summary>
public class LoopDetector
{
    private readonly int _threshold;
    private readonly object _lock = new();
    private readonly LinkedList<CallRecord> _callHistory = new();
    private const int MaxHistorySize = 100;

    /// <summary>
    /// 当前连续调用计数
    /// </summary>
    public int CurrentConsecutiveCount { get; private set; }

    /// <summary>
    /// 当前连续调用的工具名称
    /// </summary>
    public string? CurrentToolName { get; private set; }

    /// <summary>
    /// 当前连续调用的参数哈希
    /// </summary>
    private string? _currentArgumentsHash;

    /// <summary>
    /// 初始化 LoopDetector
    /// </summary>
    /// <param name="threshold">检测阈值，连续相同调用达到此数量时触发循环检测</param>
    public LoopDetector(int threshold = 3)
    {
        if (threshold < 2)
            throw new ArgumentException("阈值必须至少为 2", nameof(threshold));

        _threshold = threshold;
    }

    /// <summary>
    /// 检查是否存在循环调用
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="argumentsHash">参数哈希值</param>
    /// <returns>检测结果</returns>
    public LoopDetectionResult Check(string toolName, string argumentsHash)
    {
        if (string.IsNullOrEmpty(toolName))
            throw new ArgumentNullException(nameof(toolName));

        if (string.IsNullOrEmpty(argumentsHash))
            throw new ArgumentNullException(nameof(argumentsHash));

        lock (_lock)
        {
            // 检查是否与上一次调用相同
            if (IsSameAsLastCall(toolName, argumentsHash))
            {
                CurrentConsecutiveCount++;
            }
            else
            {
                // 不同调用，重置计数
                CurrentConsecutiveCount = 1;
                CurrentToolName = toolName;
                _currentArgumentsHash = argumentsHash;
            }

            var isLoop = CurrentConsecutiveCount >= _threshold;

            return new LoopDetectionResult
            {
                IsLoop = isLoop,
                ConsecutiveCount = CurrentConsecutiveCount,
                ToolName = CurrentToolName,
                RecommendedAction = DetermineAction(CurrentConsecutiveCount)
            };
        }
    }

    /// <summary>
    /// 记录一次工具调用
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="argumentsHash">参数哈希值</param>
    public void RecordCall(string toolName, string argumentsHash)
    {
        if (string.IsNullOrEmpty(toolName))
            throw new ArgumentNullException(nameof(toolName));

        if (string.IsNullOrEmpty(argumentsHash))
            throw new ArgumentNullException(nameof(argumentsHash));

        lock (_lock)
        {
            // 添加到历史记录
            _callHistory.AddLast(new CallRecord(toolName, argumentsHash, DateTime.UtcNow));

            // 限制历史记录大小
            while (_callHistory.Count > MaxHistorySize)
            {
                _callHistory.RemoveFirst();
            }

            // 更新连续调用计数
            if (IsSameAsLastCall(toolName, argumentsHash))
            {
                CurrentConsecutiveCount++;
            }
            else
            {
                CurrentConsecutiveCount = 1;
                CurrentToolName = toolName;
                _currentArgumentsHash = argumentsHash;
            }
        }
    }

    /// <summary>
    /// 重置检测器状态
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _callHistory.Clear();
            CurrentConsecutiveCount = 0;
            CurrentToolName = null;
            _currentArgumentsHash = null;
        }
    }

    /// <summary>
    /// 获取调用历史记录
    /// </summary>
    /// <returns>调用历史记录的只读列表</returns>
    public IReadOnlyList<CallRecord> GetCallHistory()
    {
        lock (_lock)
        {
            return _callHistory.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 计算参数的哈希值
    /// </summary>
    /// <param name="arguments">参数字符串</param>
    /// <returns>SHA256 哈希值</returns>
    public static string ComputeArgumentsHash(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return string.Empty;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(arguments));
        return Convert.ToHexString(bytes);
    }

    private bool IsSameAsLastCall(string toolName, string argumentsHash)
    {
        return CurrentToolName == toolName && _currentArgumentsHash == argumentsHash;
    }

    private LoopAction DetermineAction(int consecutiveCount)
    {
        if (consecutiveCount < _threshold)
            return LoopAction.Continue;

        if (consecutiveCount < _threshold + 2)
            return LoopAction.Warn;

        return LoopAction.Terminate;
    }
}

/// <summary>
/// 调用记录
/// </summary>
public record CallRecord(string ToolName, string ArgumentsHash, DateTime Timestamp);