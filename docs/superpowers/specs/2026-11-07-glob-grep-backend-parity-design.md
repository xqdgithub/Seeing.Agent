# glob / grep 双后端语义一致化设计

**日期:** 2026-11-07
**状态:** 待批准
**影响包:** `Seeing.Agent.Tools.FileSystem`

---

## 1. 意图与背景

`glob` 与 `grep` 工具当前有两条执行路径：优先经 `ISubprocess` 调用 `rg`，失败则回退到托管枚举（`FileSystemHelper`）。**保留 rg 优先是本设计的前提**，不做替换。

现状缺陷（已实测确认）：

| # | 位置 | 缺陷 |
|---|---|---|
| D1 | `RipgrepSearch.Quote()` | POSIX 分支生成单引号 `'...'`，但 `SubprocessSpec.Arguments` 由 .NET 自身的切分器处理，只认双引号与反斜杠 → rg 收到带字面单引号的参数 → 永远 exit=1 |
| D2 | `RipgrepSearch` 两处 | 仅 `ExitCode > 1` 视为失败；把"参数被污染"的 exit=1 当成"无匹配"的真相，永不回退 |
| D3 | `FileSystemHelper.ConvertGlobToRegex` | `**` 分支条件 `regex.EndsWith(".*")` 恒假，`**` 退化为单个 `*` |
| D4 | `FileSystemHelper.SearchDirectory` / `SearchDirectoryForContent` | 用**绝对路径**去撞锚定正则 `^...$`，任何模式都匹配不到；`include` 同样失效 |
| D5 | 两后端 | 大小写、排序、隐藏文件、路径形态、截断判定五项不一致 |
| D6 | `FileSystemHelper.SearchDirectory` | 跳过 `.` 前缀**目录**但不过滤 `.` 前缀**文件**，与 rg 的 hidden 默认相反 |
| D7 | `RipgrepSearch.TryParseMatchEvent` | 只认 `path.text`，非 UTF-8 文件名（`path.bytes`）被静默丢弃 |
| D8 | `RipgrepSearch` | rg 超时后回退托管，30s + 30s 吃掉 60s 工具预算 |
| D9 | 测试 | 托管路径无任何 happy-path 断言；`GrepGlobSubprocessTests` 只测取消 |

用户可见症状：`glob` 报"未找到匹配文件"、`grep` 报"未找到匹配的文件"，但文件确实存在。两条路径各有各的坏法，且都在静默降级。

---

## 2. 目标 / 非目标

### 目标

1. 同一输入下，rg 后端与托管后端的**结果集语义一致**（路径集合、排序、截断标记）。
2. "没搜到"与"没搜成"严格区分，且"没搜成"必须可诊断。
3. 删除全部命令行字符串拼接与平台转义分叉。
4. 托管路径恢复可用并被 happy-path 测试覆盖。

### 非目标

- 不替换 rg 优先的设计，不引入新的第三方 glob 库。
- 不实现托管版的 `.gitignore` 解析（见 §7 已知差异）。
- 不改 `IFileSystem` 契约、不改工具 schema 形状。
- 不改工具描述里承诺的行为（只让实现符合描述）。

---

## 3. 方案比较

| | A 最小可信 | **B 等价后端（选定）** | C 能力协商 |
|---|---|---|---|
| argv 传递 | ✅ | ✅ | ✅ |
| `**` 转换修复 | 基本 | 完整 rg 语义 | 完整 |
| exit 0/1/2 分离 | ✅ | ✅ | ✅ |
| 两后端结果对齐 | ❌ | ✅ | ✅ |
| 超时不回退 | ❌ | ✅ | ✅ |
| `metadata.backend` + stderr 摘要 | ❌ | ✅ | ✅ |
| `auto/rg/managed` 配置开关 | ❌ | ❌ | ✅ |

**选 B。** A 把"装没装 rg 行为不同"的坑留到下次；C 的配置开关当前无真实需求（无人会主动关 rg），属投机。

---

## 4. 设计

### 4.1 `RipgrepSearch`：参数、判定、诊断

**参数构造** — 改用 `ArgumentList`，删除 `Quote()`：

```csharp
// glob —— 注意：--files 不接受 --max-count（实测传了被忽略），截断只能在 C# 侧做
ArgumentList = ["--files", "--color", "never", "--sortr=modified",
                ...(caseInsensitive ? ["--glob-case-insensitive"] : []),
                "-g", NormalizePattern(pattern), searchPath]

// grep —— --max-count 是「每文件」上限，不是全局上限（实测 3 文件 × 5 行 = 15 条）
ArgumentList = ["--json", "--max-count", PerFileCap.ToString(),
                ...(caseInsensitive ? ["--glob-case-insensitive"] : []),
                ...(include is null ? [] : ["-g", NormalizePattern(include)]),
                "--", pattern, searchPath]
```

**两个上限必须区分开**（这是实测推翻的原始设计）：

| 上限 | 参数 | 语义 | 谁负责 |
|---|---|---|---|
| 全局结果上限 | 无对应参数 | 最多返回 `limit` 条 | **C# 侧** `Take(limit + 1)` 后判定 `truncated` |
| 每文件上限 | `--max-count` | 单文件内最多取 N 条 | rg（仅 grep；glob 无此概念） |

- `--max-count` 在 `--files` 下**完全无效**（实测 `rg --files --max-count 1` 仍输出全部），故 glob 不得传它。
- grep 的 `--max-count` 取 `PerFileCap`（建议 `Math.Max(1, limit)`），作用是防止单文件刷满输出；它**不能**用来判定全局截断。
- 大小写不敏感平台追加 `--glob-case-insensitive`。**作用域仅限 `-g`（glob 与 include 模式），不影响 `--` 之后的搜索正则**（实测 `-g '*.md'` 加该参数后才命中 `X.MD`）。
- 删除 `WorkingDirectory = searchPath`（已传绝对路径，避免输出形态歧义）。
- `NormalizePattern`：`\` → `/`。rg 内部一律按 `/` 处理（实测 `src\**\*.md` 在 Windows 上 exit=1，`src/**/*.md` 命中），无平台分支。**代价见 §7：`\` 不再可作转义符**。

**三态判定**（两处统一）：

| 退出码 | 含义 | 处理 |
|---|---|---|
| 0 | 有匹配 | 返回结果 |
| 1 | 无匹配 | **返回空结果，不回退**（这是可信答案） |
| 2 | 出错 | 记录 `LogWarning` + stderr 首行，**回退托管** |
| 超时/被杀 | `stdout == null` | **不回退**，返回"搜索超时"诊断 |

超时不回退的理由：两次 30s 串行会突破 60s 的 `TimeoutBudget`，且超时通常意味着目录过大，托管遍历只会更慢。

**诊断可见性**：

- `LogStderr` 从 `LogDebug` 提升为 `LogWarning`（`rg` 缺失走的是探测分支，不在此列）。
- 工具 `metadata` 增加 `backend`（`"rg"` / `"managed"`）与可选 `diagnostic`（stderr 首行，仅回退时）。
- rg 出错回退时，结果输出尾部追加一行 `(ripgrep 不可用，已回退托管搜索: <首行>)`，让"结果可能不同"对模型可见。

**`path.bytes` 分支**：`TryParseMatchEvent` 在 `text` 缺失时读取 `bytes`（base64）按 UTF-8 解码；两者都缺才丢弃。

**探测缓存**：保留进程级缓存（`rg` 可用性在同一进程内稳定）；探测失败时 `LogWarning` 一次带上异常消息。`s_probed` 改 `volatile` 读写，避免并发重复探测。

### 4.2 新增 `GlobMatcher`：唯一 glob 语义实现

内部静态类，`InternalsVisibleTo` 已含测试项目。

```
GlobMatcher
  ├─ NormalizePattern(string pattern)                       // '\' → '/'，去前导 "./"
  ├─ ConvertToRegex(string pattern, bool caseSensitive)     // → ^...$ 正则
  └─ Matches(string relativePathOrName, GlobPattern compiled)
```

**转换规则（对齐 rg / globset）**

| 语法 | 正则 | 说明 |
|---|---|---|
| `**` | `.*` | 跨目录；`**/` 前缀可省略 |
| `*` | `[^/]*` | 不跨目录 |
| `?` | `[^/]` | 单字符，不跨分隔符 |
| `{a,b}` | `(?:a|b)` | 花括号展开（工具描述已承诺 `*.{ts,tsx}`；实测 rg 支持） |
| `[abc]` | `[abc]` | 字符类透传（`!` → `^` 取反；实测 rg 支持） |
| `\` | `/` | 视为分隔符，**不作转义**（见下方取舍） |

**`\` 的取舍（已知限制）**：本实现把 `\` 一律归一为 `/`，因此**无法用模式表达文件名中含字面 `*` 或 `?` 的情况**。理由：用户写 `src\*.cs` 的意图几乎总是路径分隔符，而支撑转义需要两后端同时实现一致的转义语义，收益远低于成本。此限制写入 §7。

**`**/` 与 `*.md` 必须等价**：二者都返回「任意深度的 .md」。实现上，`**/*.md` 的 `**/` 段转成可选的 `(?:.*/)?`；`*.md` 因不含 `/` 而走 basename 规则（见下），天然等价。若实现者漏掉这点，`*.md` 只匹配根级文件——这是必须覆盖的测试用例（§6 第 2 条）。

**匹配对象规则**（修 D4 的根因）

- 模式**含 `/`** → 匹配「相对搜索根的路径」（`Path.GetRelativePath(searchPath, file)` 后归一为 `/`）。
- 模式**不含 `/`** → 匹配文件名（basename）。
- 模式**以 `/` 开头** → 锚定搜索根。

**大小写**：由调用方传入，来源 `PathBoundary.PathComparison`（Windows/macOS 不敏感、Linux 敏感）。与 rg 侧的 `--glob-case-insensitive` 同源，保证一致。

### 4.3 `FileSystemHelper`：托管回退重构

- `GlobSearch` / `GrepSearch` 改用 `GlobMatcher`，删除 `ConvertGlobToRegex` 与 `MatchesGlobPattern`（后者仅测试在用，随语义迁移重写测试）。
- 遍历对象改为「相对路径 + basename」，不再拿绝对路径撞正则。
- **统一过滤**（对齐 rg 默认）：跳过 `.` 前缀目录 **和** `.` 前缀文件（修 D6）。
- `include` 走同一 `GlobMatcher`。
- 排序统一：glob 按 mtime 倒序（现有行为，rg 侧靠 `--sortr=modified` 对齐）；grep 按 mtime 倒序（现有行为，两后端一致）。
- **截断判定**：两后端均取 `limit + 1` 条，返回前 `Take(limit)`，`truncated = count > limit`。修 `count >= limit` 的等量误报。

### 4.4 `GrepTool` / `GlobTool`

- 正则预校验**只在托管分支**做（D 现状用 .NET 语法验 Rust 正则，属验错编译器）。rg 可用时不预校验，让 rg 报错并走 exit=2 诊断。
- 输出文案区分三态：
  - 可信无匹配：`未找到匹配的文件` / `未找到匹配文件`
  - 超时：`搜索超时（已搜索 <duration>），请缩小 path 或使用更具体的模式`
  - 回退：正常结果 + 尾部回退说明
- `metadata`：`count` / `pattern` / `path` / `truncated`（精确）+ `backend` + 可选 `diagnostic`。

---

## 5. 两后端对齐清单

| 维度 | 统一为 |
|---|---|
| 返回路径 | 绝对路径 |
| 隐藏项（默认） | 均跳过：点前缀 **+ Windows `FileAttributes.Hidden`**（对齐 rg 实测行为） |
| 隐藏项（`hidden=true`） | 均包含，**不额外排除 `.git` / `node_modules`**（完全对齐 `rg --hidden`） |
| 大小写 | 跟随 `PathBoundary.PathComparison`（仅作用于 glob/include 模式） |
| glob 排序 | mtime 倒序 |
| grep 排序 | mtime 倒序 |
| `include` 语义 | 同一 `GlobMatcher` |
| 全局截断 | `limit + 1` 精确判定（两后端均在 C# 侧） |
| 路径分隔符 | 模式内一律 `/` |
| `.gitignore` | **不等价**（见 §7） |

### 4.5 隐藏项开关（`hidden`）

`glob` / `grep` 均新增可选布尔参数 `hidden`（默认 `false`），语义**完全照搬 ripgrep**：

| `hidden` | rg 侧 | 托管侧 |
|---|---|---|
| `false`（默认） | 不传 `--hidden` | 跳过 `IsHidden` 命中项 |
| `true` | 追加 `--hidden` | 不跳过 |

**"隐藏"的判定**由 `IFileSystem.IsHidden(path)` 单点定义（`LocalFileSystem` 实现）：

- **全平台**：文件名以 `.` 开头的项（`.git`、`.env`、`.github`）
- **Windows 额外**：`FileAttributes.Hidden` 位

实测确认 rg 在 Windows 上同样跳过 Hidden 属性项，因此这是必需的，不是可选项——否则同一份目录在"装了 rg"和"没装 rg"下结果不同。

**不额外排除 `.git` / `node_modules`**：实测 `rg --hidden` 会完整列出 `.git/config` 与 `.git/objects/*`，因此托管侧也照做。防止误开导致超时的责任交给参数描述与 30s 超时诊断，不由实现偷偷加排除规则（那会造成又一次后端不一致）。

> 注：`-g '!.git'` 在 rg 中可整目录排除，但 `--glob '!.git/**'` **不能**（实测仍会遍历 `.git` 内容）。本设计不引入任何排除规则。

---

## 6. 测试

**托管路径（当前盲区，必须新增）**

1. `**/*.md` 命中嵌套与根级文件
2. `*.md` 命中任意深度（basename 规则）
3. `*.{ts,tsx}` 花括号展开命中
4. 中文文件名命中（回归用户实际场景）
5. `.CS` 在大小写敏感 / 不敏感两档下的预期结果
6. `[abc]` 字符类命中
7. `count == limit` 不误报截断；`count == limit + 1` 正确标截断
8. `.hidden.md` 被跳过
9. `hidden=true` 时 `.hidden.md` / `.github/**` 可见
10. `hidden=true` 时 `.git/config` **也可见**（不额外排除）
11. `IsHidden` 对 Windows Hidden 属性返回 true（仅 Windows 断言）

**rg 路径**

12. 参数以 `ArgumentList` 传递，断言**不含**引号字符
13. exit=1 → 空结果 + **不回退**（断言托管未被调用）
14. exit=2 → 回退 + 输出含 stderr 首行
15. `path.bytes`（base64）解析
16. 超时 → 不回退 + 超时诊断
17. `hidden=true` → 参数含 `--hidden`；缺省不含

**一致性**

18. 同一临时目录树，rg 与托管两种后端输出路径集合相等（rg 缺失时跳过该用例）

---

## 7. 已知差异（接受）

| 差异 | 处理 |
|---|---|
| `.gitignore` / 全局 excludes | rg 尊重，托管不实现。托管回退是降级路径，不因此给 rg 加 `--no-ignore`（那会把主路径语义拉低）。工具描述标注"回退时忽略规则不完整"。 |
| `.git` 目录 | rg 默认跳过（属 hidden）；托管同样按 `.` 前缀跳过，一致。 |
| 非 UTF-8 文件名 | rg 经 `path.bytes` 支持；托管经 `EnumerateFiles` 返回 .NET 字符串，行为一致。 |
| **文件名含字面 `*` / `?`** | 因 `\` 被归一为分隔符，模式无法表达"字面星号"（见 §4.2 取舍）。实际文件名极少含这些字符，接受。 |
| **grep 全局排序** | 两后端均按 mtime 倒序，但 rg 的 mtime 在 `--json` 事件中不提供，需回查文件系统。若某文件在搜索后立即被删除，回查失败则按 0 处理（排末位）。 |
| **`--sortr=modified` 需 rg ≥ 13** | 不支持时表现为 exit=2 → 回退托管（结果正确，仅慢）。 |
| **`hidden=true` 会扫进 `.git` / `node_modules`** | 完全对齐 rg 语义，不偷偷排除。参数描述已警示；30s 超时会给出明确诊断而非静默空结果。 |
| **`IsHidden` 是 `IFileSystem` 新增成员** | 破坏性契约变更（外部自定义实现需补实现）。已同步 `ExecutionWorldContractsTests` 守门与 3 处实现（`LocalFileSystem` + 2 个测试 fake）。 |

---

## 8. 风险

| 风险 | 缓解 |
|---|---|
| `ArgumentList` 在旧 `LocalSubprocessFactory` 路径已支持，但自定义 `ISubprocessFactory` 实现可能只读 `Arguments` | 契约注释已声明「同时存在时优先 ArgumentList」；改动后补一条测试断言 spec 形态 |
| `--glob-case-insensitive` 需 rg ≥ 13 | 探测阶段已在跑 `--version`；不解析版本，若不支持表现为 exit=2 → 回退托管（可接受降级） |
| `--sortr=modified` 需 rg ≥ 13，且会禁用并行排序 | 同上；排序仅影响展示顺序，不影响结果集 |
| 托管遍历大目录仍慢 | 保留取消令牌穿透；超时不回退已消除二次等待 |
| glob 无全局上限参数，rg 会把整个目录吐进管道（1MB 上限截断，但进程仍在扫） | 保持 `MaxSearchOutputChars` 排空；事件预算 60s 兜底。若需真上限，rg `--files` 无可用参数，只能靠提前 `Kill`（本设计不做） |

---

## 9. 交付顺序

1. `GlobMatcher` + 单测（纯函数，无 IO，红→绿）
2. `IFileSystem.IsHidden` 契约 + `LocalFileSystem` 实现 + 契约守门测试
3. `FileSystemHelper` 改用 `GlobMatcher` / `IsHidden` + happy-path 测试
4. `RipgrepSearch` 改 `ArgumentList` + 三态判定 + 诊断 + `--hidden` + 测试
5. `GrepTool` / `GlobTool` 输出与 metadata 收口 + `hidden` 参数 + 测试
6. 一致性对照测试
7. 更新 `docs/architecture/` 相关条目（`06-compliance-audit.md` 的 ripgrep 条目补注）
