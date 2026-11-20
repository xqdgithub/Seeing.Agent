# 系统提示词分节边界统一设计

日期：2026-11-20
状态：待批准
范围：小改动（提示词渲染 + 装配层守门）

## 1. 问题

系统提示词由两段拼成：Agent 模板（`BuiltInAgents.cs`）与上下文基线（`ContextAssembler`）。
基线中的 System 源按 `Scope → Key` 排序后直接以 `\n\n` 连接，**框架不校验每个源是否自带分节边界**。

当前四个 System 源的渲染首行：

| 源 | 首行 | 边界 |
|----|------|------|
| `SkillsContextSource` | `## Skills` | 有 |
| `WorkbenchContextSource` | `## 当前打开的工作台` | 有 |
| `CollaborationDirectoriesContextSource` | `协同工作目录（可读写；相对路径以 Environment 分节的工作目录为基准）:` | **无** |
| `ToolCatalogContextSource`（经 `ToolExposureEngine.BuildCatalog`） | `以下工具当前未内联进本次请求的 schema…`，组标题为 `### 通用` | **无**（H3 无 H2 父级） |

后果：协同目录紧接在 `## Skills` 的条目列表之后，被模型读成 Skills 分节的续项；工具目录的 `### 命名空间` 悬空在 H2 层级之外。

协同目录的文案另有语义偏差：真实约定是「相对路径基准恒为工作区根，协同目录一律用绝对路径或命令级 `workdir` 访问」（`2026-11-19-single-cwd-source-design.md:78`），但现文案写成「相对路径以工作目录为基准」，模型容易把 `sub/a.txt` 当成协同目录下的文件。

## 2. 目标

1. 每个进入系统提示词的注入块都有明确的 H2 分节边界，不再出现裸段落粘连。
2. 协同目录分节讲清三件事：与工作目录平级、访问用绝对路径或 `workdir`、相对路径不相对它解析。
3. 装配层守门：System 源基线缺少分节边界时记录可诊断告警，防止未来新增源再漏。

## 3. 非目标

- 不引入 XML 标签包裹（已与用户确认走 H2 方案；守门允许 XML 信封，兼容既有 `Message` 落点写法）。
- 不调整既有分节的相对顺序，不重命名 `## Skills` / `## Agents` / `## Environment`。
- 不改 `Message` 落点源（`ProjectInstructionsContextSource` 自带 XML 信封）。
- 不为已有会话重写已存基线（见 §7 生效范围）。

## 4. 方案与选择

| 方案 | 取舍 |
|------|------|
| A. 统一 H2 + 装配层守门（选中） | 与现有模板/`## Skills` 一致；模型对 Markdown 标题最敏感；改动集中、测试成本低 |
| B. H2 + XML 标签双保险 | 边界机器可辨，但标题与标签重复、token 增加，且与既有测试断言冲突 |
| C. 只加 XML 标签 | 与模板 H2 风格并存两套约定，长期更难维护 |

选 A：成本最低，且守门把「每个 System 源必须自带边界」从约定变成可观测事实。

## 5. 具体改动

### 5.1 协同目录（`src/spine/Seeing.Agent.Core/Context/CollaborationDirectoriesContextSource.cs`）

`RenderInitial` 改为三行结构（有目录时）：

```
## 协同工作目录

这些是本会话额外授权的可读写目录，与 `## Environment` 中的工作目录平级，不是它的子目录。
访问时使用下面列出的绝对路径，或在命令中用 workdir 参数指定；相对路径只相对工作目录解析，不相对这些目录解析。

- <绝对路径>（备注）
```

无目录时：

```
## 协同工作目录

本会话未授权任何协同目录；可访问范围只有工作目录。
```

约束：

- 首行标题提取为常量；列表条目仍需按 path ordinal 排序，重复渲染逐字节相等。
- 继续不渲染 `exists`（存在性只走增量叙述，避免基线随磁盘抖动）。
- 移除旧的 `Title` 常量与 `- （无）` 写法。

### 5.2 工具目录（`src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolExposureEngine.cs`）

`BuildCatalog` 在开场白之前加 `## 工具清单` 标题与一个空行（用「清单」而非「目录」，避免与工作目录 / 协同工作目录混淆）：

```
## 工具清单

以下工具当前未内联进本次请求的 schema…
### 通用 (66 tools, 51 shown)
- …
```

约束：

- 预算计算 `used` 必须把标题行与空行计入，否则 `CatalogText` 会超出 `CatalogBudgetChars`（现有测试 `CatalogText.Length <= CatalogBudgetChars` 会失败）。
- 保持 `### <命名空间> (N tools, M shown)` 行格式不变，现有断言依赖它。
- 标题只在分层（Tiered）且有延迟工具时出现；`DeferredToolCount == 0` 时仍返回 null。

### 5.3 装配层守门（`src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs`）

新增私有方法，与 `WarnIfBaselineTooLong` 并列，在三处建立/重建/重锚定路径调用：

```csharp
private void WarnIfSystemBaselineNotSectioned(
    IContextSource source, string baselineText, List<string> issues)
```

判定规则：

- 仅对 `ContextSourceTarget.System` 生效。
- 首行 `Trim()` 后以 `## ` 开头 → 通过。
- 或文本整体匹配 XML 信封（`<tag …> … </tag>`）→ 通过。
- 其余：`issues.Add(...)` + `LogWarning`，文案说明「基线缺少分节标题，可能与上一分节粘连」。不抛异常、不自动补标题。

### 5.4 测试

- `CollaborationDirectoriesContextSourceTests`：首行断言 `## 协同工作目录`；含「绝对路径」；空列表含「未授权」。
- `ToolExposureEngineTests`：`CatalogText` 以 `## 工具清单` 开头；预算断言保持通过。
- 新增 `ContextAssembler` 用例：无标题的 System 源 → `Issues` 含告警；带 `## ` 的源 → 不含该告警；空白与 XML 信封不告警；版本提升后一次性重建且下一轮无告警；三源拼接顺序与空行分隔。
- 两个改了文本的源把 `ValueSchemaVersion` 1 → 2，旧会话下次装配走 `RebuildBaseline` 采纳新文本（否则每轮「同值不同文本」告警）。
- 全部相关测试用 VSTest 运行（MTP 在本环境发现不到用例）。

## 6. 验收

1. 新建会话的系统提示词尾部结构为：`## Skills` → `## 协同工作目录` → `## 工具清单`，每块之间空行分隔，无裸段落。
2. 协同目录分节明确写出绝对路径约定，模型不再把相对路径当协同目录相对路径。
3. 注册一个不带标题的 System 源时，装配结果 `Issues` 出现可诊断告警，且提示词仍可正常构建。
4. 既有测试全绿。

## 7. 生效范围（重要）

基线文本建立后按设计**字节稳定**并落库，`BuildSystemBaseline` 只读存库文本。因此：

- 两个改了文本的源把 `ValueSchemaVersion` 1 → 2：已有会话在升级后的**下一次装配**走 `RebuildBaseline`，一次性采纳新分节，之后重新字节稳定。
- 不提升版本的源仍保持「旧基线不重写」；这是框架对 System 源的既有语义。
- 必须提升版本的原因：若只改渲染文本而不改版本，旧基线文本会在「同值不同文本」检查里**每轮**触发 `Issues` 与 `LogWarning`（值不变、文本永不重写），并让新分节永远不生效。
- `ToolCatalogContextSource.Render` 在 `previous.Canonical` 为空时返回 `null`：版本重建会以 `Initial` 语义调用 `Render`，不短路会把整份目录重复叙述成「新增」。

## 8. 留给实现者的决定

- 守门告警的措辞与日志级别（建议 `LogWarning`，与「基线超限」同级）。
- 是否把「首行必须以 `## ` 开头」的判定抽成 `PromptBuilder.ContainsHeading` 之外的共用工具（当前不需要，两处语义不同：一个查锚点是否存在，一个查文本是否以标题开头）。
