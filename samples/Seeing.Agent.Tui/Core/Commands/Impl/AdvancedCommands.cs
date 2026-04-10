using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Llm;
using Seeing.Agent.MCP;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Tui.Core.Commands.Impl;

/// <summary>
/// 模型命令
/// </summary>
public class ModelCommand : ICommand
{
    private readonly Core.TuiState _state;
    private readonly IAgentRegistry _registry;
    private readonly IAgentRuntimeManager _runtimeManager;
    private readonly ILlmService _llm;
    private readonly IOptions<SeeingAgentOptions> _options;

    public ModelCommand(Core.TuiState state, IAgentRegistry registry, IAgentRuntimeManager runtimeManager, ILlmService llm, IOptions<SeeingAgentOptions> options)
    {
        _state = state;
        _registry = registry;
        _runtimeManager = runtimeManager;
        _llm = llm;
        _options = options;
    }

    public CommandMetadata Metadata => new()
    {
        Name = "model",
        Aliases = new[] { "models" },
        Description = "显示或设置模型",
        Usage = "/model [list|set <model>|default <model>] 或 /models",
        Category = CommandCategory.Agent,
        SortOrder = 55
    };

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var parts = context.Arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var modelArg = parts.Length > 1 ? parts[1].Trim() : "";

        return subCmd switch
        {
            "list" => ShowModels(),
            "set" when !string.IsNullOrEmpty(modelArg) => await SetCurrentAgentModelAsync(modelArg),
            "default" when !string.IsNullOrEmpty(modelArg) => SetDefaultModel(modelArg),
            _ => await ShowCurrentModelAsync()
        };
    }

    private async Task<CommandResult> ShowCurrentModelAsync()
    {
        var eff = await _registry.GetEffectiveModelAsync(_state.CurrentAgentKey);
        var effStr = eff?.ToString() ?? "未配置";
        var def = _options.Value.DefaultModel ?? "未配置";
        var available = _llm.GetAvailableModels();
        return CommandResult.Ok(
            $"当前 Agent {_state.CurrentAgentKey} 有效模型: {effStr}\n" +
            $"全局默认模型: {def}\n" +
            $"LLM 可用模型数: {available.Count} · 使用 /model list 查看");
    }

    private async Task<CommandResult> SetCurrentAgentModelAsync(string modelId)
    {
        try
        {
            await _runtimeManager.SetSessionModelOverrideAsync(_state.CurrentAgentKey, modelId);
            return CommandResult.Ok($"已将 Agent {_state.CurrentAgentKey} 的模型设置为 {modelId}");
        }
        catch (ArgumentException ex)
        {
            return CommandResult.Fail($"{ex.Message}\n使用 /model list 查看可用模型");
        }
    }

    private CommandResult SetDefaultModel(string modelId)
    {
        _options.Value.DefaultModel = modelId;
        return CommandResult.Ok($"已设置全局默认模型: {modelId}\n注意：此修改仅在当前会话有效");
    }

    private CommandResult ShowModels()
    {
        var available = _llm.GetAvailableModels();
        if (available.Count == 0)
            return CommandResult.Ok("LLM 服务中暂无可用模型。");

        var configuredProviders = _options.Value.Providers?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        var lines = new List<string> { "LLM 服务 — 可用模型:", "" };

        foreach (var kv in available.OrderBy(k => k.Key))
        {
            var slashIdx = kv.Key.IndexOf('/');
            if (slashIdx < 0) continue;
            var provider = kv.Key.Substring(0, slashIdx);
            if (!configuredProviders.Contains(provider)) continue;

            var displayName = kv.Key.Substring(slashIdx + 1);
            lines.Add($"  {displayName} ({provider})");
        }

        lines.Add($"共 {available.Count} 个模型");
        return CommandResult.Ok(string.Join("\n", lines));
    }
}

/// <summary>
/// 搜索命令
/// </summary>
public class SearchCommand : ICommand
{
    private readonly Core.TuiState _state;

    public SearchCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "search",
        Description = "搜索消息",
        Usage = "/search [keyword|clear|next|prev]",
        Category = CommandCategory.Navigation,
        SortOrder = 30
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var parts = context.Arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        var result = subCmd switch
        {
            "clear" or "c" => ClearSearch(),
            "next" or "n" => NavigateNext(),
            "prev" or "p" => NavigatePrev(),
            _ when !string.IsNullOrEmpty(subCmd) => DoSearch(subCmd),
            _ => CommandResult.Ok("用法:\n  /search <keyword> - 搜索消息\n  /search clear - 清除搜索\n  /search next|prev - 导航匹配项")
        };

        return Task.FromResult(result);
    }

    private CommandResult ClearSearch()
    {
        _state.ClearSearch();
        return CommandResult.Ok("已清除搜索", true);
    }

    private CommandResult NavigateNext()
    {
        if (!_state.IsSearchMode || _state.SearchMatchIndices.Count == 0)
            return CommandResult.Ok("没有活跃的搜索");
        _state.NavigateNextMatch();
        return CommandResult.Ok($"导航到第 {_state.CurrentSearchMatchIndex + 1}/{_state.SearchMatchIndices.Count} 个匹配项", true);
    }

    private CommandResult NavigatePrev()
    {
        if (!_state.IsSearchMode || _state.SearchMatchIndices.Count == 0)
            return CommandResult.Ok("没有活跃的搜索");
        _state.NavigatePrevMatch();
        return CommandResult.Ok($"导航到第 {_state.CurrentSearchMatchIndex + 1}/{_state.SearchMatchIndices.Count} 个匹配项", true);
    }

    private CommandResult DoSearch(string keyword)
    {
        _state.SetSearchKeyword(keyword);
        if (_state.SearchMatchIndices.Count == 0)
            return CommandResult.Fail($"未找到匹配项: \"{keyword}\"");
        return CommandResult.Ok($"找到 {_state.SearchMatchIndices.Count} 个匹配项: \"{keyword}\"", true);
    }
}

/// <summary>
/// 工具命令
/// </summary>
public class ToolsCommand : ICommand
{
    private readonly ToolInvoker _tools;
    private readonly McpClientManager _mcp;

    public ToolsCommand(ToolInvoker tools, McpClientManager mcp) => (_tools, _mcp) = (tools, mcp);

    public CommandMetadata Metadata => new()
    {
        Name = "tools",
        Description = "显示已注册工具",
        Usage = "/tools",
        Category = CommandCategory.Tools,
        SortOrder = 60
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var all = _tools.GetTools().OrderBy(t => t.Id).ToList();
        if (all.Count == 0)
            return Task.FromResult(CommandResult.Ok("ToolInvoker 中未注册任何工具"));

        var mcpIds = new HashSet<string>(_mcp.GetTools().Select(t => t.Id), StringComparer.Ordinal);
        var localTools = all.Where(t => !mcpIds.Contains(t.Id)).ToList();
        var mcpTools = all.Where(t => mcpIds.Contains(t.Id)).ToList();

        var lines = new List<string> { "ToolInvoker — 已注册工具:", "" };

        if (localTools.Count > 0)
        {
            lines.Add("本地工具:");
            foreach (var tool in localTools.Take(10))
                lines.Add($"  {tool.Id} - {tool.Description ?? ""}");
            if (localTools.Count > 10)
                lines.Add($"  ... 还有 {localTools.Count - 10} 个");
        }

        if (mcpTools.Count > 0)
        {
            lines.Add("");
            lines.Add("MCP 工具:");
            foreach (var tool in mcpTools.Take(10))
                lines.Add($"  {tool.Id} - {tool.Description ?? ""}");
            if (mcpTools.Count > 10)
                lines.Add($"  ... 还有 {mcpTools.Count - 10} 个");
        }

        lines.Add("");
        lines.Add($"共 {all.Count} 个工具 (本地: {localTools.Count}, MCP: {mcpTools.Count})");
        return Task.FromResult(CommandResult.Ok(string.Join("\n", lines)));
    }
}

/// <summary>
/// 技能命令
/// </summary>
public class SkillsCommand : ICommand
{
    private readonly SkillManager _skills;

    public SkillsCommand(SkillManager skills) => _skills = skills;

    public CommandMetadata Metadata => new()
    {
        Name = "skills",
        Description = "显示已加载技能",
        Usage = "/skills",
        Category = CommandCategory.Tools,
        SortOrder = 65
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var skillInfos = _skills.GetAllSkillInfos();
        if (skillInfos.Count == 0)
            return Task.FromResult(CommandResult.Ok("SkillManager 中未加载任何技能"));

        var lines = new List<string> { "SkillManager — 已加载技能:", "" };
        foreach (var kv in skillInfos.OrderBy(k => k.Key))
        {
            var desc = kv.Value.Description.Length > 50 ? kv.Value.Description.Substring(0, 50) + "..." : kv.Value.Description;
            lines.Add($"  {kv.Key} ({kv.Value.Location ?? ""})");
            lines.Add($"    {desc}");
        }
        lines.Add("");
        lines.Add($"共 {skillInfos.Count} 个技能");
        return Task.FromResult(CommandResult.Ok(string.Join("\n", lines)));
    }
}

/// <summary>
/// MCP 命令
/// </summary>
public class McpCommand : ICommand
{
    private readonly McpClientManager _mcp;
    private readonly ToolInvoker _tools;

    public McpCommand(McpClientManager mcp, ToolInvoker tools) => (_mcp, _tools) = (mcp, tools);

    public CommandMetadata Metadata => new()
    {
        Name = "mcp",
        Description = "显示 MCP 服务器和工具",
        Usage = "/mcp",
        Category = CommandCategory.Tools,
        SortOrder = 70
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var servers = _mcp.GetConnectedServers();
        var mcpTools = _mcp.GetTools();

        if (servers.Count == 0)
            return Task.FromResult(CommandResult.Ok("MCP 管理器中无已连接服务器"));

        var lines = new List<string> { "MCP 管理器 — 已连接服务:", "" };

        foreach (var serverName in servers.OrderBy(s => s))
        {
            var serverTools = mcpTools.Where(t => t.Id.StartsWith(serverName + "_", StringComparison.Ordinal)).ToList();
            lines.Add($"  ✓ {serverName} ({serverTools.Count} 个工具)");
            foreach (var tool in serverTools.Take(3))
            {
                var toolName = tool.Id.Contains('_') ? tool.Id[(tool.Id.IndexOf('_') + 1)..] : tool.Id;
                lines.Add($"    - {toolName}");
            }
            if (serverTools.Count > 3)
                lines.Add($"    ... 还有 {serverTools.Count - 3} 个工具");
        }

        lines.Add("");
        lines.Add($"共 {servers.Count} 个服务器, {mcpTools.Count} 个 MCP 工具");
        return Task.FromResult(CommandResult.Ok(string.Join("\n", lines)));
    }
}

/// <summary>
/// 规则命令
/// </summary>
public class RulesCommand : ICommand
{
    private readonly Core.TuiState _state;

    public RulesCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "rules",
        Description = "显示规则来源",
        Usage = "/rules",
        Category = CommandCategory.System,
        SortOrder = 80
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var msg = $"规则字符数: {_state.RulesMarkdown.Length}\n";
        if (_state.RulesSources.Count == 0)
            msg += "无规则文件";
        else
        {
            msg += "规则来源:\n";
            msg += string.Join("\n", _state.RulesSources.Select(s => $"- {s}"));
        }
        return Task.FromResult(CommandResult.Ok(msg));
    }
}

/// <summary>
/// 折叠命令
/// </summary>
public class FoldCommand : ICommand
{
    private readonly Core.TuiState _state;

    public FoldCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "fold",
        Description = "折叠/展开消息",
        Usage = "/fold <index>",
        Category = CommandCategory.Navigation,
        SortOrder = 35
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.Arguments))
        {
            var foldedCount = _state.FoldedMessageIds.Count;
            return Task.FromResult(CommandResult.Ok(
                $"当前折叠消息数: {foldedCount}\n用法: /fold <消息索引>"));
        }

        if (!int.TryParse(context.Arguments, out var index) || index < 1 || index > _state.Messages.Count)
            return Task.FromResult(CommandResult.Fail($"无效的消息索引: {context.Arguments}\n有效范围: 1 - {_state.Messages.Count}"));

        var msg = _state.Messages[index - 1];
        var messageId = Core.TuiState.GetMessageId(msg);
        _state.ToggleMessageFold(messageId);

        var isFolded = _state.FoldedMessageIds.Contains(messageId);
        return Task.FromResult(CommandResult.Ok(isFolded ? $"已折叠消息 #{index}" : $"已展开消息 #{index}", true));
    }
}