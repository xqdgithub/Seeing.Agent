namespace Seeing.Agent.Cli.Services;

/// <summary>一次性提示词执行选项（`seeing-cli -p ...`）。</summary>
/// <param name="Prompt">提示词正文（`-` 表示从 stdin 读取）。</param>
/// <param name="Yolo">本次会话自动批准全部审批（SessionAutoApprove.Enabled）。</param>
/// <param name="Stream">以 JSONL 事件流写 stdout。</param>
/// <param name="Agent">覆盖默认 Agent。</param>
/// <param name="Model">覆盖默认模型。</param>
/// <param name="TimeoutSeconds">整体超时（秒）；null = 不超时。</param>
public sealed record OneShotOptions(
    string Prompt,
    bool Yolo = false,
    bool Stream = false,
    string? Agent = null,
    string? Model = null,
    int? TimeoutSeconds = null);
