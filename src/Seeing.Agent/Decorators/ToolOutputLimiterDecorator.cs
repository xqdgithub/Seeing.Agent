using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Helpers;
using Seeing.Agent.Output;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Decorators;

/// <summary>
/// 工具输出限制装饰器 - 装饰器链最外层兜底，统一限制所有工具输出长度。
/// <para>
/// 输出超过 ToolOutput.MaxInlineBytes（或工具声明 output.maxBytes）时：
/// 全文落盘到会话 ref 目录（IToolOutputStore），Output 替换为 &lt;persisted-output&gt;
/// 头+尾预览，Metadata 记录路径与统计；ref 目录加入会话白名单，agent 可经 read 恢复全文。
/// 工具声明 output.skip=true 时豁免（自身已控制输出，如内置分页工具）。
/// </para>
/// </summary>
public sealed class ToolOutputLimiterDecorator : ToolDecorator
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly IToolOutputStore _outputStore;
    private readonly IWorkspaceWhitelist _whitelist;
    private readonly ILogger<ToolOutputLimiterDecorator> _logger;

    public ToolOutputLimiterDecorator(
        ITool inner,
        IOptionsMonitor<SeeingAgentOptions> options,
        IToolOutputStore outputStore,
        IWorkspaceWhitelist whitelist,
        ILogger<ToolOutputLimiterDecorator> logger)
        : base(inner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var result = await base.ExecuteAsync(arguments, context).ConfigureAwait(false);

        var config = _options.CurrentValue.ToolOutput;
        if (!config.Enabled || !result.Success || string.IsNullOrEmpty(result.Output))
            return result;

        // 能力判断（优先于配置）
        if (ToolCapabilityReader.GetBool(this, ToolCapabilityKeys.OutputSkip))
            return result;

        if (ToolCapabilityReader.GetInt(this, ToolCapabilityKeys.OutputMaxBytes) is { } maxBytes && maxBytes > 0)
            config = new ToolOutputOptions
            {
                MaxInlineBytes = maxBytes,
                Enabled = true,
                PreviewHeadChars = config.PreviewHeadChars,
                PreviewTailChars = config.PreviewTailChars
            };

        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        if (bytes <= config.MaxInlineBytes)
            return result;

        var lines = result.Output.Count(c => c == '\n') + 1;
        var sessionId = context.SessionId ?? string.Empty;
        var callId = context.CallId ?? Guid.NewGuid().ToString("N");

        try
        {
            var outputPath = await _outputStore.SaveAsync(sessionId, callId, result.Output, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(sessionId))
                _whitelist.Add(sessionId, _outputStore.GetRefDirectory(sessionId));

            result.Output = BuildPreview(result.Output, outputPath, config, bytes, lines, spillFailed: false);
            result.Metadata["truncated"] = true;
            result.Metadata["outputPath"] = outputPath;
            result.Metadata["originalBytes"] = bytes;
            result.Metadata["originalLines"] = lines;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolOutputLimiter] 落盘失败，降级为 inline 截断: {ToolId}", Inner.Id);
            result.Output = BuildPreview(result.Output, outputPath: null, config, bytes, lines, spillFailed: true);
            result.Metadata["truncated"] = true;
            result.Metadata["spillFailed"] = true;
        }

        return result;
    }

    /// <summary>构建 &lt;persisted-output&gt; 头+尾预览模板。</summary>
    private static string BuildPreview(
        string output,
        string? outputPath,
        ToolOutputOptions config,
        int bytes,
        int lines,
        bool spillFailed)
    {
        var headLen = Math.Max(0, config.PreviewHeadChars);
        var tailLen = Math.Max(0, config.PreviewTailChars);
        var useEllipsis = output.Length > headLen + tailLen;

        var head = useEllipsis ? output.Substring(0, headLen) : output;
        var tail = useEllipsis ? output.Substring(output.Length - tailLen) : string.Empty;
        var omitted = useEllipsis ? output.Length - headLen - tailLen : 0;

        var sb = new StringBuilder();
        sb.AppendLine("<persisted-output>");
        if (spillFailed)
        {
            sb.AppendLine($"输出过长：原始 {bytes / 1024.0:0.0}KB（{lines} 行），写入会话目录失败，仅展示部分内容。");
        }
        else
        {
            sb.AppendLine($"输出过长：原始 {bytes / 1024.0:0.0}KB（{lines} 行），完整输出已保存到：");
            sb.AppendLine($"  {outputPath}");
            sb.AppendLine("需要完整内容时，请使用 read 工具读取该文件。");
        }
        sb.AppendLine();
        if (useEllipsis)
        {
            sb.AppendLine($"预览（头部 {headLen} + 尾部 {tailLen} 字符，中间省略 {omitted} 字符）：");
            sb.AppendLine(head);
            sb.AppendLine($"...（省略 {omitted} 字符）...");
            sb.AppendLine(tail);
        }
        else
        {
            sb.AppendLine("完整内容（未省略）：");
            sb.AppendLine(output);
        }
        sb.AppendLine("</persisted-output>");
        return sb.ToString();
    }
}
