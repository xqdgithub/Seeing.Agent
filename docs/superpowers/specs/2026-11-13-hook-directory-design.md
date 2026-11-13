# 钩子目录设计

**日期**: 2026-11-13
**状态**: 待审阅

## 问题

`HookManager` 能登记、摘除并触发 `IHookHandler`，但处理器只能由模块在 `Activate` 里用代码挂上。用户不能把一段脚本或一个 DLL 放进目录就让它在下一次钩子触发时运行。

## 目标

1. 只扫描用户级目录 `~/.seeing/hooks`（`ISeeingDirectories.UserSeeingDirectory` 下的 `hooks`）。换工作区不改变这套钩子，项目级 `.seeing` 不参与。
2. 把 `.js` 或 `.dll` 放进该目录后，主机启动完成时即挂上。之后新建和删除会在去抖后更新登记。脚本内容变化不改变登记，下一次触发重新 `require`。DLL 内容变化会摘掉旧实例并加载新副本。
3. `.js` 用系统 `PATH` 上的 `node` 执行，脚本可以使用 `require`、文件和网络。找不到 `node` 时跳过全部脚本并记一条日志，DLL 照常挂载。
4. `.dll` 中无参构造的 `IHookHandler` / `IMultiHookHandler` 登记进同一个 `HookManager`。
5. 脚本抛错、超时、协议不合法或写回类型对不上时，这次调用按没有这个脚本继续，`mutable` 保持调用前的值。脚本明确返回 `continue: false` 时才打断阻塞钩子。

## 非目标

- 不扫描项目级目录，不做两级合并。
- 不内嵌 Node，不支持 Python 或其他脚本运行时。
- 不把目录钩子做成能力模块，不进 `BuiltInCapabilitySets`，不受 `Boot` 启停。
- 不热卸载 DLL 的程序集。替换只摘掉旧处理器并加载新副本，旧程序集留到进程退出。
- 不改 `HookManager` 的触发策略、优先级排序和「阻塞钩子抛异常即中断」的现有行为。
- 不让脚本调用宿主服务或拿到 `ITool`、`CancellationToken` 等活对象。
- 不提供管理页面。

## 1. 目录约定

根目录：`Path.Combine(directories.UserSeeingDirectory, "hooks")`。宿主不创建钩子脚本或 DLL。`.cache` 里的影子副本和工作脚本由宿主自己写。目录尚不存在时视为空快照，监听最近的已存在祖先，发现 `hooks` 被创建后再改为监听该目录本身。

参与挂载的只有这两类：

| 路径 | 钩子点 |
|---|---|
| `hooks/<point>.js` | 文件名去掉扩展名，如 `tool.execute.before.js` |
| `hooks/<point>/<name>.js` | 上一级目录名，如 `tool.execute.before/audit.js` |
| `hooks/<name>.dll` | 类型自己的 `Spec` / `Specs`，不看文件名 |

忽略并在首次遇到时记一条 Debug：

- 名为 `.cache`、`node_modules`，或以 `.` 开头的目录及其全部后代
- 超过一层的嵌套文件（`hooks/<point>/lib/a.js` 不会成为钩子；脚本可以用 `require` 引用它）
- 放在钩子点子目录里的 `.dll`
- 扩展名不是 `.js` / `.dll` 的文件（比较忽略大小写）

钩子点字符串原样使用，区分大小写与 `HookRegistry` 常量一致。`chat.params` 等五个旧名由 `HookManager` 拒绝登记；目录宿主把该文件记为跳过并打出与 `HookManager` 相同的告警，不反复重试。未知点名仍然登记，没有触发方时它不会被调用。

## 2. 组件

代码放在 `src/spine/Seeing.Agent.Core/Hooks/Directory/`。它是脊柱的一部分，因为要直接使用 `HookManager`。不新建能力包。

| 类型 | 职责 |
|---|---|
| `HookDirectoryService` | `IHostedService`。`StartAsync` 做首次同步；`StopAsync` 摘掉本次挂上的全部处理器并结束 Node 进程 |
| `HookDirectoryCatalog` | 纯差分：给定当前文件快照，算出要登记和要摘除的脚本与 DLL。可单测，不碰文件系统监听 |
| `HookScriptBridge` | 每个脚本文件一个 `IHookHandler`。把载荷交给 Node，按第 6、7 节处理返回值 |
| `NodeHookClient` | 管理一个 Node 子进程。宿主侧对调用加锁，同一时刻只有一个请求 |
| `HookDllLoader` | 影子复制、建加载上下文、反射找出处理器、在替换时摘掉上一批实例 |
| `HookPayloadProjection` | 第 6 节的投影与写回。无 I/O |

`AddSeeingCore` 登记 `HookDirectoryService`。它随通用主机启动，不在 `InitializeSeeingAsync` 里启动。因此模块 `Activate` 期间目录钩子尚未挂上；主机 `StartAsync` 完成后才生效。`StartAsync` 重复调用直接返回，避免双登记。

文件监听沿用技能根监听的去抖方式（400ms，突发写合并成一次同步）。去抖只影响登记差分（新建、删除、DLL 内容变化）。脚本正文每次触发都重新加载，不等这次去抖。测试不走监听器，直接把文件列表交给 `HookDirectoryCatalog` 再由服务应用差分。监听器本身的去抖不另写等待型测试。

## 3. 脚本契约

脚本是 CommonJS。Node 以该文件的绝对路径 `require`，因此 `require` 相对该文件所在目录解析。

```javascript
module.exports = async function (event) {
  return { continue: true, mutable: {} };
};
```

`event`：

| 字段 | 含义 |
|---|---|
| `point` | 钩子点 |
| `sessionId` | 会话 id，没有时为空字符串 |
| `messageId` | 可空 |
| `step` | 可空 |
| `input` | 只读输入里能投影的键 |
| `mutable` | 本次调用可改的键 |
| `result` | 非阻塞钩子的结果里能投影的键 |

返回值：

- `null`、`undefined` 或省略：等同 `{ continue: true }`，不改 `mutable`。
- `continue` 缺省为 `true`。
- `continue: false` 且 `mutable` 合法：先写回 `mutable`，再让桥返回 `HookResult.Stop`（`Continue: false`，`Error: null`）。阻塞链在此停下，后面的处理器不再执行。非阻塞链本来就不会因 `Continue` 停下。
- `module.exports` 不是函数、返回值不是对象、JSON 不合法：按第 7 节失败放行。

工作脚本以嵌入资源放在 Core 程序集里，启动时写到 `hooks/.cache/hook-worker-<内容哈希>.js`。宿主执行 `node <worker>`，不经过 shell。工作脚本从 stdin 按行读 UTF-8 JSON，向 stdout 按行写 JSON。stderr 以 Debug 记入日志。

请求与响应用 `id` 配对：

```json
{"id":"1","script":"E:/.../tool.execute.before.js","event":{}}
```

```json
{"id":"1","ok":true,"continue":true,"mutable":{}}
```

```json
{"id":"1","ok":false,"error":"说明"}
```

每次调用前工作脚本删除该文件的 `require.cache` 再 `require`。文件内容变化不需要摘掉再登记处理器；下一次触发就是新正文。新建和删除才改变登记集合。

## 4. Node 进程

- 只有当前快照里至少有一个 `.js` 时才探测和启动。没有脚本时不启动。
- 启动前用 `node -e "process.exit(0)"` 探测。失败、超时或不在 `PATH`：记一条 Warning，并记住「本次进程不再探测」。此后新增的 `.js` 也保持未登记，直到重启 Seeing。探测尚未失败过时，稍后才放进目录的第一批脚本仍会探测。DLL 不受影响。
- 调用超时为 5 秒（常量 `HookScriptTimeout`，不做配置项）。超时后杀死进程，这次调用失败放行。下一次需要 Node 的调用再启动新进程。
- 进程意外退出：正在等待的调用失败放行，下一调用再启动。
- `StopAsync` 关闭 stdin 并在 2 秒内等进程退出，超时则杀死。
- 单测注入 `NodeHookClient` 的替身，不要求机器安装 Node。

## 5. DLL

每个 DLL 使用一个不回收的 `AssemblyLoadContext`。加载的是影子副本，不是目录里的原文件。

1. 将 DLL 复制到 `hooks/.cache/<内容 SHA256>.dll`。副本已存在则直接用。
2. 用原 DLL 路径构造 `AssemblyDependencyResolver`（依赖仍从原 DLL 旁边和它的 `deps.json` 解析）。
3. 加载上下文解析程序集时：若默认上下文已经加载了同名程序集，使用那一份；否则用 resolver 的路径加载。这样 `IHookHandler` 与宿主是同一个类型。
4. 取出公开、非抽象、实现了 `IHookHandler` 或 `IMultiHookHandler`、且具有公开无参构造的类型，各 `Activator.CreateInstance` 一次。单点处理器调用 `Register`，多点处理器调用 `RegisterMulti`。一个类型同时实现两者时只按 `IMultiHookHandler` 登记一次。
5. 没有合格类型、复制失败或反射失败：记 Warning，跳过该文件，不影响其他文件。

替换或删除原 DLL 时，摘掉该文件上次创建的处理器实例，再按新内容加载。旧的加载上下文不卸载。同内容的哈希副本可复用。`.cache` 不参与扫描，也不在停止时清空。

DLL 处理器抛出的异常继续走 `HookManager` 的现有路径：阻塞钩子会因此中断。失败放行只覆盖脚本桥，不覆盖 DLL。

## 6. JSON 投影与写回

送给脚本的 `input`、`mutable`、`result` 只保留能变成 JSON 的值：`null`、字符串、布尔、数字、`JsonElement`，以及由这些组成的字典、列表。其余对象（工具实例、取消令牌、响应对象等）丢掉该键，Debug 记录键名。`CancellationToken` 不进入 JSON。

写回只发生在脚本返回的 `mutable` 是 JSON 对象、且其中每一个**原来就存在**的键都能按下表转换时。未知键忽略。缺席的键不修改。先完成全部键的校验，再一次性写入；任一已存在键转换失败则一个键都不写，并按失败放行处理（即使同时写了 `continue: false` 也不打断）。

| 调用前的值 | 脚本返回的 JSON | 写回 |
|---|---|---|
| `JsonElement` | 任意合法 JSON | `JsonSerializer.SerializeToElement` 的结果 |
| `IDictionary<string, string>` | 值全是字符串的对象 | 新建 `Dictionary<string, string>(OrdinalIgnoreCase)` |
| `null` | 字符串 / 布尔 / null | 对应的 CLR 值 |
| `null` | 整数且在 `int` 范围内 | `int` |
| `null` | 其他数字 | `double`。`LlmService.TryConvert<int>` 仍按它今天的 `Convert.ToInt32` 处理小数，目录宿主不再取整 |
| `null` | 对象或数组 | `JsonElement` |
| 字符串、布尔、整数、浮点数 | 同种类的 JSON（数字之间允许 `Convert` 能做的转换） | 转换后的 CLR 值 |

`llm.headers` 的 `headers` 因此仍是 `IDictionary<string, string>`，`MergeHeaders` 读得到。`llm.params` 里的 `maxTokens` 写成 `int`，`temperature` 写成 `double`，`LlmService.TryConvert` 读得到。`tool.execute.before` 的 `args` 原值是 `JsonElement`，写回仍是 `JsonElement`。

## 7. 失败放行

下列情况桥记录 Warning（含脚本路径和钩子点）并返回 `HookResult.Success`，不修改 `mutable`：

- 脚本抛错，或工作脚本返回 `ok: false`
- 调用超时，或 Node 进程在等待期间退出
- 返回值不是对象，或 `mutable` 不是对象
- `mutable` 写回转换失败

桥自己吞掉这些异常，不向 `HookManager` 抛出。`ExecuteBlockingAsync` 对处理器异常会返回 `Continue: false`；异常一旦冒泡，就会变成打断，和本节矛盾。

调用方的 `CancellationToken` 已取消时，桥抛出 `OperationCanceledException`，不记成脚本失败，也不因此重启 Node。这沿用 `HookManager` 今天对异常的处理。

## 8. 优先级

`HookManager` 按 `Priority` 升序排序，相同优先级的相对顺序不稳定。

- 未写优先级的脚本：同一钩子点内按相对路径序排序，从 `-1000` 起每个加 1。现有内置处理器的优先级是 10、50、100。未声明优先级的脚本不超过 1010 个时，它们都早于这些内置处理器，并且彼此顺序稳定。
- 脚本第一行是 `// @priority <整数>` 时使用该整数，不参与自动编号。同一显式优先级之间的顺序不保证。
- DLL 使用类型上的 `Priority`，目录宿主不改写。

一个钩子点上的脚本各自是独立处理器。某个脚本返回 `continue: false` 后，排序在它后面的脚本和内置处理器都不会再被这次阻塞触发调用。

## 9. 测试

放在 `tests/spine/Seeing.Agent.Tests/Hooks/Directory/`。用目录快照和假的 Node 客户端驱动，不用 `Task.Delay` 等文件监听。

| 断言 | 锁住的行为 |
|---|---|
| 根文件与一层子目录 | 钩子点分别来自文件名和父目录名 |
| `node_modules`、`.cache`、点目录里的 DLL、两层嵌套 | 不登记 |
| 项目级目录中的同名文件 | 不登记 |
| `continue: false` 且 `mutable` 合法 | 先写回，再让阻塞链停止 |
| 脚本抛错、超时、`ok: false`、转换失败 | 返回 `Success`，`mutable` 与调用前相同，异常不冒泡 |
| `args` 为 `JsonElement` | 写回后仍是 `JsonElement` |
| `headers` 为字符串字典 | 写回后 `MergeHeaders` 能枚举 |
| `maxTokens` 从 null 写成 JSON 整数 | 袋中的值是 `int` |
| 探测 Node 失败 | 脚本全部跳过，同一快照里的 DLL 仍登记 |
| DLL 无合格类型 | 跳过该文件 |
| 替换 DLL 内容 | 旧实例被摘除，新实例已登记；加载的是 `.cache` 中的副本 |
| 删除脚本 | 对应处理器已从 `HookManager` 移除 |
| 第一行 `@priority` | 使用该整数，而不是 `-1000` 起的自动编号 |
| 旧点名 `chat.params.js` | 不登记 |

## 10. 已知边界

- 每次替换 DLL 都留下一个加载上下文，直到进程退出。
- Node 不在 `PATH` 时，装上 Node 后要重启 Seeing 才会再探测。
- 一条脚本死循环会占住唯一的 Node 调用，直到 5 秒超时并重启进程。这段时间里其他脚本调用会在锁上等待，超时后的那些等待调用失败放行。
- 主机 `StartAsync` 之前触发的钩子看不到目录里的文件。
- 脚本以当前用户权限运行，能读能写该用户能碰的文件和网络。目录放在用户级是为了避免打开工作区就执行仓库里的代码；它不构成沙箱。
