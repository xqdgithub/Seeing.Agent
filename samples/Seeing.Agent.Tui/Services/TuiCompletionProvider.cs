using Seeing.Agent.Abstractions.Commands;

namespace Seeing.Agent.Tui.Services;

/// <summary>补全候选：<see cref="Name"/> 含前导斜杠。</summary>
public sealed record TuiCompletionItem(string Name, string Description);

/// <summary>补全结果：替换后的完整文本与光标位置（命令名后一格）。</summary>
public readonly record struct TuiCompletionApply(string Text, int Cursor);

/// <summary>
/// 斜杠命令 Tab 补全：候选为 TUI 本地命令表 ∪ <see cref="ICommandRegistry"/> 服务端命令（排除隐藏）。
/// 仅当光标处于行首 <c>/命令</c> token 内（前导空格后以 `/` 开头且无空白）时生效。
/// </summary>
public sealed class TuiCompletionProvider
{
    private static readonly (string Name, string Description)[] LocalCommands =
    [
        ("/agent", "切换 Agent"),
        ("/auto-approve", "会话级自动批准三态"),
        ("/cancel", "取消当前/级联执行"),
        ("/delete", "删除活跃会话"),
        ("/exit", "退出"),
        ("/expand", "展开工具完整输出"),
        ("/fork", "分支活跃会话并切换"),
        ("/help", "显示本帮助（含服务端命令）"),
        ("/h", "显示本帮助（/help 别名）"),
        ("/?", "显示本帮助（/help 别名）"),
        ("/model", "切换模型"),
        ("/new", "新建会话并切换"),
        ("/open", "打开指定会话/子会话"),
        ("/quit", "退出（/exit 别名）"),
        ("/q", "退出（/exit 别名）"),
        ("/reasoning", "推理显示开关"),
        ("/rename", "重命名活跃会话"),
        ("/resume", "切换到指定会话"),
        ("/scenario", "切换场景（空为清除）"),
        ("/sessions", "列出会话"),
        ("/thinking", "设置思考档（空为清除）"),
        ("/todo", "展开 Todo 面板"),
    ];

    private readonly ICommandRegistry _commandRegistry;

    public TuiCompletionProvider(ICommandRegistry registry)
    {
        _commandRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>仅当光标处于行首 `/` 命令 token 内（`/xxx` 且未出现空格）时返回候选；否则返回空。</summary>
    public IReadOnlyList<TuiCompletionItem> GetCompletions(string text, int cursor)
    {
        if (!TryGetCommandToken(text, cursor, out _, out var token))
            return [];

        return BuildCandidates()
            .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>唯一候选项时直接补全并在其后加空格；多候选返回 null（由引擎展示候选表）。</summary>
    public TuiCompletionApply? TryApply(string text, int cursor)
    {
        if (!TryGetCommandToken(text, cursor, out var tokenStart, out var token))
            return null;

        var matches = BuildCandidates()
            .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
            return null;

        var name = matches[0].Name;
        var tokenEnd = cursor;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
            tokenEnd++;

        var prefix = text[..tokenStart];
        var remainder = text[tokenEnd..];
        var newText = string.IsNullOrWhiteSpace(remainder)
            ? prefix + name + " "
            : prefix + name + remainder;

        return new TuiCompletionApply(newText, prefix.Length + name.Length + 1);
    }

    /// <summary>构建去重并排序后的候选（本地优先，服务端同名跳过）。</summary>
    private List<TuiCompletionItem> BuildCandidates()
    {
        var result = new List<TuiCompletionItem>(LocalCommands.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, description) in LocalCommands)
        {
            if (seen.Add(name[1..]))
                result.Add(new TuiCompletionItem(name, description));
        }

        foreach (var metadata in _commandRegistry.GetAllMetadata())
        {
            if (metadata.IsHidden || string.IsNullOrWhiteSpace(metadata.Name))
                continue;

            if (seen.Add(metadata.Name))
                result.Add(new TuiCompletionItem("/" + metadata.Name, metadata.Description));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>解析行首命令 token；成功时 <paramref name="token"/> 形如 <c>/xxx</c>（不含空白）。</summary>
    private static bool TryGetCommandToken(string text, int cursor, out int tokenStart, out string token)
    {
        tokenStart = 0;
        token = string.Empty;

        if (string.IsNullOrEmpty(text))
            return false;

        if (cursor < 0 || cursor > text.Length)
            cursor = Math.Clamp(cursor, 0, text.Length);

        var start = 0;
        while (start < cursor && char.IsWhiteSpace(text[start]))
            start++;

        if (start >= cursor || text[start] != '/')
            return false;

        var head = text[start..cursor];
        foreach (var ch in head)
        {
            if (char.IsWhiteSpace(ch))
                return false;
        }

        tokenStart = start;
        token = head;
        return true;
    }
}
