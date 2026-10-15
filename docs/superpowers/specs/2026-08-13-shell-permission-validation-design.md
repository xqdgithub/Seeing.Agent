# Shell 危险命令放宽与文件系统校验规则优化设计

**日期:** 2026-08-13
**状态:** 已确认
**范围:** 内置工具（Shell/FileSystem）拦截规则放宽与缺失机制补齐

---

## 1. 背景与问题

当前内置工具的校验规则存在"一刀切"问题，导致日常使用不便：

1. **无法删除文件/文件夹**：`BashTool` 硬编码封禁 `rm`/`rmdir`/`del`，并一刀切禁止所有递归删除（`rm -r`、`rm -rf`、`--recursive`），连删除单个文件都不行。
2. **无法使用绝对路径**：`BashTool` 硬编码禁止重定向到绝对路径（`> /path/to/file`）。
3. **工作区边界过于刚性**：`SerializingPermissionChannel` 只认单一 `WorkspaceRoot`，工作区外路径一律进入询问流程（无交互通道时直接失败）；而 `add_workspace_path` 工具声称可扩展白名单却未实现。
4. **资源级权限检查失效**：`DefaultToolPermissionPolicy` 中 `read`/`write`/`edit` 的资源参数键名写错（`file_path` vs `filePath`），导致资源级检查实际传入空路径。
5. **Windows Shell 优先级不合理**：`ShellService` 在 Windows 上优先 Git Bash，其次 cmd，未优先 pwsh/powershell；且 `File.Exists("powershell.exe")` 不搜索 PATH，永远不命中。

---

## 2. 目标

- 放宽 Bash 危险命令拦截：只拦截真正灾难性操作，普通删除与绝对路径重定向可用。
- 封禁规则可配置（`ShellOptions`），默认采用放宽后的规则。
- Windows 上 Shell 优先使用 `pwsh → powershell → git bash → cmd`。
- 修复 `DefaultToolPermissionPolicy` 参数键名 bug。
- 实现真正的会话级工作区白名单，`add_workspace_path` 生效，支持访问工作区外路径。
- 新增文件系统删除工具（`delete`），删除操作走权限体系。
- 修复路径边界判断缺陷（前缀匹配 + 符号链接加固）。
- 补齐单元测试。

---

## 3. 总体架构

```
配置层    ShellOptions（新增，可配置封禁规则 + 优先 Shell）
  │
  ├─→ BashTool       危险命令检测放宽（只拦灾难性）+ 可配置 + 可测
  ├─→ ShellService   Windows 优先 pwsh→powershell→git bash→cmd + 查找修复 + 死代码清理
  │
权限层    IWorkspaceWhitelist（新增，会话级路径白名单）
  │
  ├─→ AddWorkspacePathTool   真正把路径写入白名单（不再空返回）
  ├─→ SerializingPermissionChannel  工作区 = WorkspaceRoot ∪ 白名单
  ├─→ DefaultToolPermissionPolicy   修复 file_path→filePath 键名 bug + 新增 delete 映射
  └─→ DeleteTool（新增）            删除走 filesystem.delete 权限
  │
文件层    FileSystemHelper.IsPathWithinDirectory 修复前缀匹配 + 符号链接加固
          ReadDirectory 文案 bug 修复
```

---

## 4. 关键设计决策

### 决策 1：白名单存储用独立 `IWorkspaceWhitelist` 服务（而非复用 `IPermissionMemory`）

- **理由**：白名单语义是"路径扩展"，需要跨 `filesystem.read`/`filesystem.write`/`filesystem.delete` 所有 kind 生效；`IPermissionMemory` 是"按 kind 记录决定"，语义不同，混用会污染匹配逻辑。
- **实现**：`ConcurrentDictionary<string sessionId, List<string> directories>`（存储归一化后的目录列表，非精确集合）。
  - **`Contains(sessionId, path)` 必须做"子目录前缀匹配"**（而非精确相等）：判定 `path` 是否等于某白名单目录、或位于其子目录内。匹配规则复用修复后的 `FileSystemHelper.IsPathWithinDirectory(path, whitelistedDirectory)`。
  - `AddWorkspacePathTool` 写入。
  - `SerializingPermissionChannel` 查询。
  - 会话结束时可清理（复用会话生命周期钩子，若当前无统一清理入口则先提供 `ClearSession`，清理接线列为后续项）。

### 决策 2：`ShellOptions` 挂到 `SeeingAgentOptions.Shell`

- 与其他 Options 一致，走 `IOptionsMonitor<SeeingAgentOptions>` 支持热重载。
- 配置节：`SeeingAgent.Shell`（写入 `.seeing/seeing.json`，遵循项目"配置仅写在 seeing.json"约定）。

### 决策 3：`rm`/`rmdir`/`del`/`systemctl` 从封禁命令移除，递归删除改为「仅拦截针对根/家目录的灾难性 pattern」

- 不再拦截 `rm file.txt`、`rm -r ./node_modules`、`rmdir x`、`del x`、`systemctl ...` 等普通操作。
- 仍拦截 `rm -rf /`、`rm -r /`、`rm -rf ~`、`rm -rf .` 等 pattern。

### 决策 4：移除「禁止重定向到绝对路径」硬拦截

- 关键系统路径（`> /etc/`、`> /boot/`、`> /sys/`）已在 `BlockedPatterns` 中兜底。

### 决策 5：`delete` 工具工作区内自动放行（与 `write` 同级），工作区外询问

- 与"能删除文件夹"诉求对齐，工作区边界即防线。

---

## 5. 配置结构（新增）

```csharp
public sealed class ShellOptions
{
    /// <summary>Windows 优先 Shell（按顺序，找不到则顺延）。默认 pwsh → powershell → bash → cmd。其中 bash 条目在 Windows 上通过 git.exe 推断 git bash 路径（而非 PATH 查找）</summary>
    public List<string> PreferredShells { get; set; } = new() { "pwsh", "powershell", "bash", "cmd" };

    /// <summary>封禁命令（只保留灾难性）。默认：dd/mkfs/format/shutdown/reboot/halt/poweroff/init。匹配时大小写不敏感</summary>
    public List<string> BlockedCommands { get; set; } = new()
        { "dd", "mkfs", "format", "shutdown", "reboot", "halt", "poweroff", "init" };

    /// <summary>封禁模式（灾难性）。保留 rm -rf /、fork bomb、dd 写 /dev/、chmod 777 /、format 磁盘、reg delete HKLM、重定向关键系统路径等</summary>
    public List<string> BlockedPatterns { get; set; } = new()
    {
        "rm -rf /", "rm -r /", "rm -rf ~", "rm -rf .",
        ":(){ :|:& };:",
        "dd if=/dev/zero of=/dev/",
        "chmod 777 /", "chmod -R 777 /",
        "del /f /s C:\\",
        "format C:", "format D:",
        "reg delete HKLM",
        "> /etc/", "> /boot/", "> /sys/",
    };

    /// <summary>是否启用危险命令拦截（默认 true）。设为 false 可完全关闭硬拦截</summary>
    public bool EnableCommandGuard { get; set; } = true;
}
```

`SeeingAgentOptions` 新增：

```csharp
public ShellOptions Shell { get; set; } = new();
```

---

## 6. 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `Configuration/SeeingAgentOptions.cs` | 新增 `ShellOptions` + `Shell` 属性 |
| 2 | `Tools/BuiltIn/Shell/BashTool.cs` | 危险命令检测放宽 + 可配置 + 改实例方法 + 更新 `Description` 文案（去掉"谨慎使用 rm/删除"提示） |
| 3 | `Tools/BuiltIn/Shell/ShellService.cs` | pwsh 优先、查找修复、`[Obsolete]` 标记死代码 |
| 4 | `Tools/BuiltIn/FileSystem/FileSystemHelper.cs` | `IsPathWithinDirectory` 边界 + 符号链接修复 |
| 5 | `Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs` | 真正写入白名单 |
| 6 | `Tools/BuiltIn/FileSystem/DeleteTool.cs` | **新增**删除工具 |
| 7 | `Tools/BuiltIn/FileSystem/ReadTool.cs` | `ReadDirectory` 文案 bug |
| 8 | `Tools/BuiltIn/DefaultToolPermissionPolicy.cs` | `file_path`→`filePath`、新增 `delete` 映射 |
| 9 | `Core/Permission/SerializingPermissionChannel.cs` | 接入白名单（构造函数新增 `IWorkspaceWhitelist` 参数） |
| 10 | `Core/Permission/IWorkspaceWhitelist.cs` + `SessionWorkspaceWhitelist.cs` | **新增**白名单服务 |
| 11 | `Core/Permission/PermissionService.cs` | `IsSubPathOf` 边界修复 |
| 12 | `Tools/BuiltIn/BuiltInToolBase.cs` | 清理死代码 `IsWithinWorkingDirectory`/`GetRelativePath` |
| 13 | `Extensions/ServiceCollectionExtensions.cs` | 注册白名单服务、DeleteTool、ShellOptions；`SerializingPermissionChannel` 构造点（约 `:614`）同步传入白名单服务 |
| 14 | `tests/Seeing.Agent.Tests/...` | Bash 危险命令、路径边界、白名单单测 |

---

## 7. 各模块设计

### 7.1 BashTool 危险命令检测（#2）

- 构造函数新增 `IOptionsMonitor<SeeingAgentOptions>`，每次执行读取 `Shell`（即 `Shell.BlockedCommands`、`Shell.BlockedPatterns`、`Shell.EnableCommandGuard`）。
- `CheckDangerousCommand` 改为实例方法（当前为 `private static`），逻辑：
  1. `EnableCommandGuard == false` → 直接放行。
  2. 管道检查：保留 `curl|bash`、`wget|sh` 等"输出直接喂解释器"拦截。
  3. 命令替换 `$()`/反引号 + 网络请求组合：保留拦截。
  4. 封禁模式：遍历 `Shell.BlockedPatterns`（`Contains` 匹配）。
  5. 封禁命令：取命令首词，命中 `Shell.BlockedCommands` 则拦截（大小写不敏感比较；不再含 `rm`/`rmdir`/`del`/`systemctl`）。
  6. 移除「递归删除」与「重定向绝对路径」两个独立拦截分支。

### 7.2 ShellService pwsh 优先（#3）

- `GetFallbackShell()` Windows 分支改为按 `Shell.PreferredShells` 顺序查找：
  - `pwsh` → `FindExecutable("pwsh")`（`where pwsh`）。
  - `powershell` → `FindExecutable("powershell")`。
  - `bash` → **保留现有 `GetGitBashPath()` 推断逻辑**（`git.exe → Git\bin\bash.exe`），因为 git bash 通常不在 PATH，`where bash` 不会命中；`GetGitBashPath` 找不到再尝试 `FindExecutable("bash")`。
  - `cmd` → `COMSPEC` 或 `FindExecutable("cmd")`。
- 修复原 `File.Exists("powershell.exe")` 不搜索 PATH 的问题（统一走 `FindExecutable`）。
- 死代码 `GetPreferredShell`、`GetShellArgumentFormat` 及接口成员：因属 NuGet 公共 API，改为 `[Obsolete]` 标记保留（不直接删除），调用方已被移除。
- 注入 `IOptionsMonitor<SeeingAgentOptions>` 读取 `PreferredShells`。

### 7.3 路径边界修复（#4、#11、#12）

- `FileSystemHelper.IsPathWithinDirectory` 正确实现：
  - 用 `Path.GetFullPath` 归一化后，判断相等或 `path + 目录分隔符` 前缀，避免 `C:\foo` 误匹配 `C:\foobar`。
  - 顺带解析符号链接真实路径（`DirectoryInfo.ResolveLinkTarget(returnFinalTarget: true)` / `FileInfo`），失败时回退原路径，防止符号链接绕过白名单。
- `PermissionService.IsSubPathOf` 复用同一正确逻辑（可调用 `FileSystemHelper.IsPathWithinDirectory` 或实现等价判断）。
- `BuiltInToolBase.IsWithinWorkingDirectory`/`GetRelativePath` 为死代码，直接删除。

### 7.4 工作区白名单（#5、#9、#10）

新增接口与实现：

```csharp
public interface IWorkspaceWhitelist
{
    void Add(string sessionId, string directoryPath);
    /// <summary>判断 path 是否等于某白名单目录或位于其子目录内（子目录前缀匹配，非精确相等）</summary>
    bool Contains(string sessionId, string path);
    void ClearSession(string sessionId);
}
```

- `Contains` 实现为"子目录前缀匹配"：遍历该会话的白名单目录，用修复后的 `FileSystemHelper.IsPathWithinDirectory(path, dir)` 判断，任一命中即返回 true。这样 `add_workspace_path` 加入目录后，其下所有文件操作都能命中免询问。
- `AddWorkspacePathTool` 注入 `IWorkspaceWhitelist`，在 `ExecuteAsync` 中把 `path` 加入白名单（归一化为绝对路径）。注意：`add_workspace_path` 本身的"是否允许扩展"由 `filesystem.workspace_extend` 权限通道决定（询问用户），通过后才执行写入白名单。
- `SerializingPermissionChannel.RequestAsync` 的工作区边界判断改为：

```csharp
var inWorkspace = FileSystemHelper.IsPathWithinDirectory(resource, _workspace.WorkspaceRoot)
               || _whitelist.Contains(sessionId, resource);
if (inWorkspace) return PermissionChannelResult.Allowed();
```

- 白名单检查仅对 `filesystem.*` 权限生效（保持现有 `StartsWith("filesystem.")` 分支）。

### 7.5 修复参数键名 bug（#8）

- `DefaultToolPermissionPolicy.Mappings` 中 `read`/`write`/`edit` 的 `ArgKey` 从 `file_path` 改为 `filePath`（与实际工具参数一致：`ReadTool`/`WriteTool`/`EditTool` 均使用 `filePath`）。
- `grep`/`glob` 使用 `path`，保持不变。

### 7.6 新增 DeleteTool（#6、#8）

- 新增 `DeleteTool : BuiltInToolBase`，`Id = "delete"`。
- 参数：`path`（要删除的文件或目录绝对路径）。
- 行为：
  - 校验路径存在；文件用 `File.Delete`，目录用 `Directory.Delete(path, recursive: true)`。
  - 删除前可选生成受影响条目摘要（文件名/条目数）放入 metadata。
  - 失败返回明确错误（如目录非空时在 Windows 上 `Directory.Delete` 可能抛 `IOException`，捕获并返回）。
- `DefaultToolPermissionPolicy` 新增映射：`delete → filesystem.delete`（`ArgKey = "path"`）。
- 权限语义：`filesystem.delete` 命中 `SerializingPermissionChannel` 的 `filesystem.*` 分支，工作区内自动放行、工作区外询问。

### 7.7 ReadDirectory 文案 bug（#7）

- `ReadTool.ReadDirectory` 输出首行由 `$"文件不存在: {filePath}"` 改为 `$"目录: {filePath}"`（原为复制粘贴错误）。

### 7.8 DI 注册（#13）

- `services.AddSingleton<IWorkspaceWhitelist, SessionWorkspaceWhitelist>();`
- `services.AddSingleton<ITool, DeleteTool>();`
- `SerializingPermissionChannel` 构造点（`ServiceCollectionExtensions.cs` 约 `:614`）新增第 4 参 `sp.GetRequiredService<IWorkspaceWhitelist>()`。
- 确保 `ShellService`/`BashTool` 可从容器解析 `IOptionsMonitor<SeeingAgentOptions>`（已存在 `IOptionsMonitor` 注册，无需新增）。

---

## 8. 测试策略

- **BashTool 危险命令检测**：
  - `rm file.txt`、`rm -r ./node_modules`、`> output.txt`（相对重定向）→ 放行。
  - `rm -rf /`、`rm -rf ~`、`dd if=/dev/zero of=/dev/`、`format C:`、`curl ... | bash`、`$(curl ...)` → 拦截。
  - `EnableCommandGuard=false` → 全部放行。
  - 自定义 `BlockedCommands`/`BlockedPatterns` 覆盖生效。
- **路径边界**：`C:\foo` 与 `C:\foobar` 互不误判；符号链接指向工作区外时正确判定。
- **白名单**：`add_workspace_path` 后，工作区外路径的 `filesystem.read` 自动放行；不同 sessionId 隔离。
- **DeleteTool**：删除文件、删除空目录、删除非空目录（recursive）均成功；不存在的路径返回错误。
- **权限键名**：`read`/`write`/`edit` 的资源级检查能取到真实 `filePath`（可断言 `Evaluate` 返回的 `Resource` 等于传入路径）。

---

## 9. 非目标（本次不做）

- **权限标识体系统一**（`PermissionKind` 枚举 vs `filesystem.*` 字符串两套并行体系）——架构重构面大，单独立项。
- **`PermissionService.EvaluateFileAsync`（`FileOperation` 细粒度权限）接线**——当前无调用方，本次不接线，仅新增 `delete` 工具走既有 `filesystem.*` 字符串通道。
- **会话白名单的自动清理接线**——先提供 `ClearSession` 能力，若现有会话生命周期钩子可用则顺带接线，否则列为后续项。

---

## 10. 风险与回滚

- **放宽后误删风险**：删除操作仍受工作区边界 + `filesystem.delete` 权限通道约束；灾难性 pattern（`rm -rf /`、`format`、`dd`）仍硬拦截，且 `EnableCommandGuard` 可整体关闭/开启。
- **键名 bug 修复后权限检查"变紧"**：修复 `file_path`→`filePath` 会让 read/write/edit 的资源级检查真正生效。为保持"放宽"目标，配套工作区白名单机制，确保工作区内仍自动放行。
- **回滚**：改动集中于独立模块，各改动点可独立回滚；`ShellOptions` 默认值与旧行为兼容（旧封禁项仍可通过配置恢复）。
