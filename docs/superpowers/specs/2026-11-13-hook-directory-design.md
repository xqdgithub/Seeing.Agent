# 钩子目录设计

**日期**: 2026-11-13
**状态**: 已按审查修订

## 问题

`HookManager` 能登记、摘除并触发 `IHookHandler`，但处理器只能由模块在 `Activate` 里用代码挂上。用户不能把一段脚本或一个 DLL 放进目录就让它在下一次钩子触发时运行。

## 目标

1. 只扫描用户级目录 `~/.seeing/hooks`（`ISeeingDirectories.UserSeeingDirectory` 下的 `hooks`）。换工作区不改变这套钩子，项目级 `.seeing` 不参与。
2. 把 `.js` 或 `.dll` 放进该目录后，`HookDirectoryService.StartAsync` 返回后即挂上。之后新建、删除、DLL 内容变化、以及脚本第一行优先级变化，会在去抖后更新登记。脚本正文变化不摘登记，下一次触发重新 `require`。
3. `.js` 用系统 `PATH` 上的 `node` 执行，脚本可以使用 `require`、文件和网络。找不到 `node` 时跳过全部脚本并记一条日志，DLL 照常挂载。
4. `.dll` 中无参构造的 `IHookHandler` / `IMultiHookHandler` 经 `IHookManager` 登记。目录宿主只调用该接口上已有的方法。
5. 脚本抛错、超时、协议不合法、投影或写回失败时，这次调用按没有这个脚本继续，`mutable` 保持调用前的值。脚本明确返回 `continue: false` 且 `mutable` 合法时，桥返回 `HookResult.Stop`。这只保证排序在它后面的处理器不再执行。调用方是否因此取消原操作，见第 8 节。

## 非目标

- 不扫描项目级目录，不做两级合并。
- 不内嵌 Node，不支持 Python 或其他脚本运行时。
- 不把目录钩子做成能力模块，不进 `BuiltInCapabilitySets`，不受 `Boot` 启停。
- 不把文件监听、常驻 `node` 或 `AssemblyLoadContext` 放进 `Seeing.Agent.Core` 程序集。
- 不热卸载 DLL 的程序集。替换只摘掉旧处理器并加载新副本，旧程序集留到进程退出。
- 不改 `HookManager` 的触发策略、优先级排序和「阻塞钩子抛异常即中断」的现有行为。
- 不让脚本调用宿主服务或拿到 `ITool`、`CancellationToken` 等活对象。
- 不提供管理页面。

## 1. 目录约定

根目录：`Path.Combine(directories.UserSeeingDirectory, "hooks")`。宿主不创建钩子脚本或 DLL。`.cache` 里的影子副本和工作脚本由宿主自己写。目录尚不存在时视为空快照。

参与挂载的只有这两类：

| 路径 | 钩子点 |
|---|---|
| `hooks/<point>.js` | 文件名去掉扩展名，如 `tool.execute.before.js` |
| `hooks/<point>/<name>.js` | 上一级目录名，如 `tool.execute.before/audit.js` |
| `hooks/<name>.dll` | 类型自己的 `Spec` / `Specs`，不看文件名 |

只认 `.js` 与钩子根上的 `.dll`。`.cjs`、`.mjs` 不是钩子。

忽略并在首次遇到时记一条 Debug：

- 名为 `.cache`、`node_modules`，或以 `.` 开头的目录及其全部后代
- 超过一层的嵌套文件（`hooks/<point>/lib/a.js` 不会成为钩子；脚本可以用 `require` 引用它）
- 放在钩子点子目录里的 `.dll`，以及钩子根上没有合格处理器的伴随 DLL（依赖、本地库）。伴随 DLL 只记一次 Debug，不记 Warning
- 扩展名不是 `.js` / `.dll` 的文件（比较忽略大小写）

快照里的每个候选文件先用 `PathBoundary` 判定真实路径仍在 `hooks` 根内。符号链接或联接越界的记一条 Warning 并跳过。禁止用字符串前缀比较代替 `PathBoundary`。

钩子点字符串与 `HookRegistry` 常量的 `Point` 逐字比较（`Ordinal`）。文件名与某个常量的 `Point` 相等时，处理器的 `HookSpec` 用该常量，从而带上登记表里的策略。对不上任何常量时仍登记，`HookSpec` 用 `HookPolicy.Blocking` 占位。触发方读的是自己载荷上的策略，不读处理器 `Spec.Policy`，这个占位值不改变分发。

`chat.params`、`chat.headers`、`chat.message`、`chat.after_complete`、`chat.on_error` 由 `HookManager` 拒绝登记。目录宿主用与 `s_legacyLlmPoints` 相同的 `OrdinalIgnoreCase` 比较器在扫描时跳过，打出与 `HookManager` 相同的告警，不调用 `Register`，也不反复重试。因此 `Chat.Params.js` 在宿主侧就被跳过。

内部代码引用 `HookRegistry` 或 `Seeing.Session.Hooks.HookPoints` 常量。目录文件名与这些常量的点名字符串一致。会话生命周期两边字符串相同（例如 `session.created`）。禁止在目录宿主里另造一套点名。

### 监听

沿用技能根监听的约束，去抖 400ms，突发写合并成一次同步：

- 根不存在时只监听最近已存在祖先的一层，且只回应通向 `hooks` 的下一段。祖先出现 `hooks` 后再改为监听该目录本身。不得把用户主目录或 `~/.seeing` 变成递归扫描源。
- 监听器只发「请重扫」。服务对目录做全量快照，再交给 `HookDirectoryCatalog`。
- 同步单飞。进行中到达的事件合并成下一次，本次结束后必须再跑一轮，不能丢掉。
- `FileSystemWatcher.Error`（缓冲区溢出）强制整目录重扫。

去抖只影响登记差分。脚本正文每次触发都重新加载，不等这次去抖。测试不走监听器，直接把文件列表交给目录服务。监听器本身的去抖不另写等待型测试。

## 2. 组件

代码放在 `src/spine/Seeing.Agent.Hooks.Directory/`，命名空间 `Seeing.Agent.Hooks.Directory`，并加入 `Seeing.Agent.slnx`。该程序集只引用 `Seeing.Agent.Abstractions` 与 `Seeing.Files`（原子写）。不引用 `Seeing.Agent.Core`。

`Seeing.Agent.Core` 增加对该程序集的项目引用。`AddSeeingCore` 里只加一行 `services.AddHostedService<HookDirectoryService>()`，并尽早登记，排在会在自己的 `StartAsync` 里触发钩子的托管服务之前。宿主不用改调用，也不用 `AddSeeingModule`。

| 类型 | 职责 |
|---|---|
| `HookDirectoryService` | `IHostedService`。只依赖 `IHookManager`、`ISeeingDirectories`、`IOptions<HookDirectoryOptions>` 和日志。`StartAsync` 做首次同步；`StopAsync` 只摘掉本次挂上的实例，再在 2 秒内结束 Node。禁止对钩子点调用 `Clear` |
| `HookDirectoryCatalog` | 纯差分：给定当前文件快照，算出要登记和要摘除的脚本与 DLL。差分键是规范化绝对路径。可单测，不碰文件系统监听 |
| `HookScriptBridge` | 每个脚本文件一个 `IHookHandler`。把载荷交给 Node，按第 6、7 节处理返回值 |
| `NodeHookClient` | 管理一个 Node 子进程。宿主侧对调用加锁，同一时刻只有一个请求。锁在 `finally` 里释放 |
| `HookDllLoader` | 影子复制、建加载上下文、反射找出处理器、在替换时摘掉上一批实例 |
| `HookPayloadProjection` | 第 6 节的投影与写回。无 I/O |

`HookDirectoryOptions.Enabled` 默认 `true`。为 `false` 时 `StartAsync` 直接返回，不扫描、不启动 Node。会 `Start` 通用主机的测试必须把 `ISeeingDirectories` 指到临时目录，或在 `AddSeeingCore` 之前把 `Enabled` 设为 `false`，避免执行开发者真目录里的脚本和 DLL。`StartAsync` 重复调用直接返回，避免双登记。

`StopAsync` 先摘掉自己登记的实例，再关闭 Node 的 stdin 并在 2 秒内等进程退出，超时则杀死。此时仍在飞的脚本调用按失败放行收场，不改 `mutable`。

可见性：`HookDirectoryService.StartAsync` 返回之后，目录里的文件才挂上。在此之前触发的钩子看不见它们。这包括 `InitializeSeeingAsync` 期间模块 `Activate` 里的 `tool.before_register`、`mcp.before_connect`、`mcp.before_initialize`，也包括注册顺序排在本服务前面的托管服务在自己的 `StartAsync` 里触发的钩子。这些点上的目录脚本只影响启动之后的再次注册、重连或下一次调用。

## 3. 脚本契约

脚本是 CommonJS。`hooks/.cache/package.json` 固定为 `{"type":"commonjs"}`，避免祖先 `"type":"module"` 让 `node hook-worker-*.js` 起不来。用户 `.js` 不能直接加载：最近的 `package.json` 会让它按 ESM 执行，`module.exports` 会报错。工人读出源码，用 `Module.wrap` 后 `vm.runInThisContext`，文件名保持该脚本的绝对路径。和它落在同一份 `package.json` 下的相对 `require` 走同一条包装；这份 `package.json` 之外的依赖仍用原来的 `require`。因此目录即使标了 ESM，`.js` 仍按 CommonJS 的 `module.exports` 执行。

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
- `continue: false` 且 `mutable` 合法：先写回 `mutable`，再让桥返回 `HookResult.Stop`（`Continue: false`，`Error: null`）。阻塞链在此停下，后面的处理器不再执行。调用方是否取消原操作见第 8 节。
- `module.exports` 不是函数、返回值不是对象、JSON 不合法：按第 7 节失败放行。

工作脚本以嵌入资源放在 `Seeing.Agent.Hooks.Directory` 程序集里，启动时写到 `hooks/.cache/hook-worker-<内容哈希>.js`。宿主执行 `node <worker>`，不经过 shell。

协议独占 stdout。工作脚本从 stdin 按行读 UTF-8 JSON，向 stdout 只写协议 JSON 行。进程启动时，以及每一次调用用户函数之前，把 `console.log` / `console.info` / `console.debug` / `console.warn` 和 `process.stdout.write` 转到 stderr。stderr 以 Debug 记入日志。

请求与响应用 `id` 配对。C# 侧 DTO 用 `[JsonPropertyName]` 固定为下面这些小写键。`SeeingJson.Readable` 没有 camelCase 命名策略，不能靠它改大小写。

```json
{"id":"1","script":"E:/.../tool.execute.before.js","event":{}}
```

```json
{"id":"1","ok":true,"continue":true,"mutable":{}}
```

```json
{"id":"1","ok":false,"error":"说明"}
```

宿主按行读 stdout。非 JSON 行记 Debug 后丢掉。`id` 与当前请求不一致的行丢掉，包括已经作废的 id。一直读到 `id` 匹配或本次调用超时。

每次调用前工作脚本删除该文件的 `require.cache` 再加载。文件正文变化不需要摘掉再登记处理器；下一次触发就是新正文。新建、删除，以及第一行 `// @priority` 的变化，才改变登记。两者同时发生时，差分更新优先级，正文仍由下一次加载读取。

## 4. Node 进程

- 只有当前快照里至少有一个 `.js` 时才探测和启动。没有脚本时不启动。
- 启动前用 `node -e "process.exit(0)"` 探测。失败、超时或不在 `PATH`：记一条 Warning，并记住「本次进程不再探测」。此后新增的 `.js` 也保持未登记，直到重启 Seeing。探测尚未失败过时，稍后才放进目录的第一批脚本仍会探测。DLL 不受影响。
- 调用超时为 5 秒（常量 `HookScriptTimeout`，不做配置项）。这 5 秒只计算这一次自己的写入和读取，不把在锁上排队的时间算进去。超时后杀死进程，这次调用失败放行，在 `finally` 里放锁。这一代的读循环离开 stdout 之后再 `Dispose` 该进程，避免留下僵尸和句柄；释放不占调用锁，也不改这一代的失败结果。已经在锁上等待的调用不被连坐跳过；轮到它时自行启动新进程，并使用自己的 5 秒。
- 进程意外退出：正在等待的那一次调用失败放行，下一调用再启动。
- 调用方 `CancellationToken` 取消时：作废当前 `id`，停止等待，不杀进程，不记成脚本失败，不改 `mutable`，桥返回 `HookResult.Success`。迟到的旧响应因 `id` 对不上而被丢掉。下一次调用仍可使用这个进程。取消不用来实现 5 秒超时。桥不向 `HookManager` 抛 `OperationCanceledException`：`ExecuteBlockingAsync` 会把任何异常收成 `Continue: false`。
- 取消发生时若用户脚本仍在运行，下一次调用可以写入新请求。工作脚本是一问一答：先写完上一次的响应（被宿主丢掉），再读下一次请求。下一次调用自己的 5 秒包含等上一次脚本结束的时间。上一次若是死循环，下一次会在自己的 5 秒后杀死进程并失败放行。
- `StopAsync` 的进程退出规则见第 2 节。
- 单测注入 `NodeHookClient` 的替身，不要求机器安装 Node。

脚本在子进程里运行，继承宿主的环境变量，能读能写该用户能碰的文件和网络。DLL 在宿主进程内运行，无参构造，没有 DI，与宿主同一权限。失败放行只覆盖脚本桥。

## 5. DLL

每个 DLL 使用一个不回收的 `AssemblyLoadContext`。加载的是影子副本，不是目录里的原文件。

1. 将 DLL 用 `Seeing.Files.AtomicFileWrite` 写到 `hooks/.cache/<内容 SHA256>.dll`。副本已存在时先核对内容哈希，一致才复用；对不上（含写到一半留下的残文件）则作废重写。
2. 用原 DLL 路径构造 `AssemblyDependencyResolver`（依赖仍从原 DLL 旁边和它的 `deps.json` 解析）。
3. 加载上下文解析程序集时：若默认上下文已经加载了同名程序集，使用那一份；否则用 resolver 的路径加载。这样 `IHookHandler` 与宿主是同一个类型。
4. 取出公开、非抽象、实现了 `IHookHandler` 或 `IMultiHookHandler`、且具有公开无参构造的类型，各 `Activator.CreateInstance` 一次。单点处理器调用 `IHookManager.Register`，多点处理器调用 `RegisterMulti`。一个类型同时实现两者时只按 `IMultiHookHandler` 登记一次。
5. 没有合格类型：记一次 Debug，跳过。复制失败或反射失败：记 Warning，跳过该文件。两种情况都不影响其他文件。

替换或删除原入口 DLL 时，摘掉该文件上次创建的处理器实例，再按新内容加载。旧的加载上下文不卸载。同内容且哈希核对通过的副本可复用。`.cache` 不参与扫描，也不在停止时清空。

只有入口程序集不锁原文件。依赖和原生库仍从源目录加载，Windows 上会锁住那些文件；要替换依赖必须重启进程。

DLL 处理器抛出的异常继续走 `HookManager` 的现有路径：阻塞钩子会因此中断。失败放行不覆盖 DLL。

## 6. JSON 投影与写回

送给脚本的 `input`、`mutable`、`result` 只保留能变成 JSON 的值：`null`、字符串、布尔、数字、`JsonElement`，以及由这些组成的字典、列表。`Exception` 投影成 `{ "type": "<FullName>", "message": "<Message>" }` 两个字符串，不带栈、不带 `Data`。其余对象（工具实例、取消令牌、`TimeSpan`、`TokenUsage`、`SessionData`、响应对象等）丢掉该键，Debug 记录键名。`CancellationToken` 不进入 JSON。

`ValueKind` 为 `Undefined` 的 `JsonElement`（`ToolManager` 在参数缺失时放入的 `default(JsonElement)`）投影成 JSON `null`。投影或序列化若仍抛错，按第 7 节失败放行，不把异常冒泡给 `HookManager`。

写回只发生在脚本返回的 `mutable` 是 JSON 对象、且其中每一个**原来就存在**的键都能按下表转换时。未知键忽略。袋里缺席的键不修改。先完成全部键的校验，再一次性写入；任一已存在键转换失败则一个键都不写，并按失败放行处理（即使同时写了 `continue: false` 也不打断）。

字符串字典是整袋替换：脚本少返回一个键，原键就没有。袋级别的「缺席不修改」不作用于字典内部。

| 调用前的值 | 脚本返回的 JSON | 写回 |
|---|---|---|
| `JsonElement`（含 `Undefined`） | 任意合法 JSON，包括 `null` | `JsonDocument` 解析后 `Clone` 出的 `JsonElement`。空参数被脚本写成 `null` 时，`ValueKind` 为 `Null` |
| `IDictionary<string, string>` | 值全是字符串的对象 | 新建 `Dictionary<string, string>(OrdinalIgnoreCase)`，内容等于返回对象 |
| `null` | 字符串 / 布尔 / null | 对应的 CLR 值 |
| `null` | 整数且在 `int` 范围内 | `int` |
| `null` | 其他数字 | `double`。`LlmService.TryConvert<int>` 仍按它今天的 `Convert.ToInt32` 处理小数，目录宿主不再取整 |
| `null` | 对象或数组 | `Clone` 后的 `JsonElement` |
| 字符串、布尔、整数、浮点数 | 同种类的 JSON（数字之间允许 `Convert` 能做的转换） | 转换后的 CLR 值 |

写回的 `JsonElement` 必须 `Clone`，不能指向已释放的 `JsonDocument`。

`llm.headers` 的 `headers` 因此仍是 `IDictionary<string, string>`，`MergeHeaders` 读得到。`llm.params` 里的 `maxTokens` 写成 `int`，`temperature` 写成 `double`，`LlmService.TryConvert` 读得到。`tool.execute.before` 的 `args` 原值是 `JsonElement`，写回仍是 `JsonElement`。`command.execute.before` 的 `proceed` 原值是 `bool`，脚本返回布尔时写回布尔。

## 7. 失败放行

下列情况桥记录 Warning（含脚本路径和钩子点）并返回 `HookResult.Success`，不修改 `mutable`：

- 脚本抛错，或工作脚本返回 `ok: false`
- 调用超时，或 Node 进程在等待期间退出
- 返回值不是对象，或 `mutable` 不是对象
- 投影、序列化，或 `mutable` 写回转换失败。这包括 `Undefined` 的 `JsonElement` 在投影时抛错

桥自己吞掉这些异常，不向 `HookManager` 抛出。`ExecuteBlockingAsync` 对处理器异常会返回 `Continue: false`；异常一旦冒泡，就会变成打断，和本节矛盾。

调用方取消的处理见第 4 节：返回 `Success`，不记 Warning，不杀进程。

## 8. 优先级与 `continue: false`

`HookManager` 按 `Priority` 升序排序，只在 `AddHandler` 里排序。改处理器的 `Priority` 属性不会重排，相同优先级的相对顺序不稳定。目录宿主要换顺序时必须先 `Remove` 再 `Register`。

- 未写优先级的脚本：同一钩子点内按相对路径用 `StringComparer.Ordinal` 排序（区分大小写，`A.js` 在 `b.js` 之前），从 `-1000` 起每个加 1。现有内置处理器的优先级是 10、50、100。未声明优先级的脚本不超过 1010 个时，它们都早于这些内置处理器。
- 每次快照都对该点全部未声明优先级的脚本重算编号。编号变化的实例先摘后挂。已有 `c.js` 再放入 `a.js` 时，两者仍按路径序，且都小于 10。
- 脚本第一行是 `// @priority <整数>` 时使用该整数，不参与自动编号。只改这一行时，下一次差分用新数字。同一显式优先级之间的顺序不保证。
- DLL 使用类型上的 `Priority`，目录宿主不改写。

一个钩子点上的脚本各自是独立处理器。某个脚本返回 `continue: false` 后，排序在它后面的脚本和内置处理器都不会再被这次阻塞触发调用。

`continue: false` 不等于取消原操作。当前调用方：

| 钩子点 | `Continue: false` 时 |
|---|---|
| `tool.execute.before` | 工具调用失败，错误为「工具调用被 Hook 中断」。写回后的 `args` 在 `Continue` 仍为 true 时进入后面的权限判断 |
| `tool.before_register` | 跳过这次注册 |
| `command.execute.before` | 命令失败。袋里的 `proceed == false` 也会中止，与 `continue` 无关 |
| `mcp.before_connect` | 拒绝这次连接 |
| `mcp.before_reconnect` | 拒绝这次重连 |
| `mcp.before_config_update` | 拒绝这次配置更新 |
| `mcp.tool.before_register` | 跳过这个 MCP 工具的注册 |
| `scheduler.job.before_execute` | 这次任务失败 |
| `llm.params`、`llm.headers` | 只跳过更低优先级的处理器。请求仍会发出，改过的袋仍会落地 |
| `tool.definition` | 只跳过更低优先级的处理器。定义仍会生成，改过的 `description` / `parameters` 仍会落地 |
| `shell.env` | 只跳过更低优先级的处理器。环境变量仍按袋里的 `env` 返回 |
| `mcp.before_initialize` | 只跳过更低优先级的处理器。初始化继续 |

`agent.before_invoke`、`tool.batch.before`、`permission.ask`、`shell.command.execute.before`、`scheduler.heartbeat.before` 在登记表里，当前没有调用方。目录仍可登记；将来的调用方若读取 `Continue`，才会把它变成操作失败。

目录钩子不经过 `IPermissionAuthorizer`。文件一旦进了 `~/.seeing/hooks`，之后的触发不再弹批准。`tool.execute.before` 改过的 `args` 会原样进入后面的权限判断，改参数不能绕过权限。`continue: false` 则在权限判断之前直接拒绝这次工具调用。

## 9. 测试

放在 `tests/spine/Seeing.Agent.Hooks.Directory.Tests/`。用目录快照和假的 Node 客户端驱动，不用 `Task.Delay` 等文件监听。

| 断言 | 锁住的行为 |
|---|---|
| 根文件与一层子目录 | 钩子点分别来自文件名和父目录名 |
| `node_modules`、`.cache`、点目录里的 DLL、两层嵌套、`.cjs` / `.mjs` | 不登记 |
| 临时用户目录与临时项目目录各有同名文件 | 只有用户目录进入 `IHookManager` |
| 符号链接把钩子文件指到 `hooks` 根外 | `PathBoundary` 判越界后跳过 |
| `continue: false` 且 `mutable` 合法 | 先写回，再让阻塞链停止，同点下一个处理器不执行 |
| 脚本抛错、超时、`ok: false`、转换失败，以及转换失败同时 `continue: false` | 返回 `Success`，`mutable` 与调用前相同，异常不冒泡，同点下一个处理器仍执行 |
| `args` 为 `default(JsonElement)` | 桥不抛；失败放行时袋内值不变，下一个处理器仍执行 |
| `args` 为 `JsonElement`，脚本返回对象 | 写回后仍是已 `Clone` 的 `JsonElement` |
| `headers` 为字符串字典，脚本少返回一个键 | 写回后是新字典且不含那个键，`MergeHeaders` 能枚举剩余键 |
| `maxTokens` 从 null 写成 JSON 整数 | 袋中的值是 `int` |
| stdout 上先出现一行日志或错误 `id`，再出现匹配响应 | 宿主丢掉前面的行，仍配上这次请求 |
| 一次调用超时后，已在锁上等待的下一次调用 | 下一次自行启动并在自己的 5 秒内完成，不因前一次超时被跳过 |
| 探测 Node 失败 | 脚本全部跳过，同一快照里的 DLL 仍登记 |
| DLL 无合格类型 | 跳过该文件，日志级别是 Debug |
| 替换 DLL 内容 | 旧实例被摘除，新实例已登记；加载的是 `.cache` 中的副本 |
| 加载入口 DLL 之后覆盖或删除原文件 | 成功。依赖仍从源目录解析 |
| 缓存名已存在但内容哈希不符 | 作废重写，不把残文件当成副本 |
| 删除脚本 | 对应处理器已从 `IHookManager` 移除，且没有调用 `Clear` |
| 已有 `c.js` 再放入 `a.js` | 两者优先级仍按 `Ordinal` 路径序，且都小于 10 |
| 第一行 `@priority`，以及只改这一行 | 下一次差分使用该整数，而不是 `-1000` 起的自动编号 |
| 旧点名 `chat.params.js` 与 `Chat.Params.js` | 都不登记 |
| 会抛异常的 DLL 处理器 | `TriggerBlockingAsync` 得到 `Continue: false` |
| `HookDirectoryOptions.Enabled = false` 后 `StartAsync` | 不扫描临时目录，不启动 Node |

## 10. 已知边界

- 每次替换 DLL 都留下一个加载上下文，直到进程退出。只有入口程序集不锁原文件；依赖和原生库仍锁在源目录，替换依赖要重启进程。
- Node 不在 `PATH` 时，装上 Node 后要重启 Seeing 才会再探测。
- 一条脚本死循环会占住唯一的 Node 调用，直到占用它的那一次 5 秒超时并杀死进程。锁上等待的调用被推迟到进程重启之后，各自再跑，不被连坐跳过。
- `HookDirectoryService.StartAsync` 返回之前触发的钩子看不到目录里的文件。启动那一次的工具注册和 MCP 连接因此不会跑到目录脚本。
- 脚本以当前用户权限在子进程运行，DLL 以当前用户权限在宿主进程运行。目录放在用户级是为了避免打开工作区就执行仓库里的代码。它不构成沙箱，也不经过权限批准。
- 同一次差分里，先摘后挂的窗口中，一次触发可能看到该点上新旧处理器的混合。目录宿主不用 `Clear` 去消除这个窗口。
