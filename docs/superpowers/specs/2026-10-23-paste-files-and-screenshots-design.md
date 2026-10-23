# 统一文件输入与截图（粘贴 / 拖拽 / 选择器）设计

日期：2026-10-23
状态：已批准（2026-10-23）

## 1. 背景与问题

### 1.1 WebUI 现状

- `PromptInput` 只能通过文件选择器或拖拽添加附件；`wwwroot/js/app.js` 里已有一个 `setupPasteHandler`，但它只处理 `image/*`，普通文件被忽略。
- 拖拽同样把文件转成 Base64 后走 JSInterop，受同一个 32 KB 限制；文件选择器的非图片文件则进入 `ChatAttachment` 的 `file` 块。
- 该处理器把整张图片转成 Base64，一次 `invokeMethodAsync` 传给 Blazor Server。Blazor Server 默认 `MaximumReceiveMessageSize` 为 32 KB，普通截图远超该值，调用会静默失败。因此用户看到的是“粘贴没反应”，而不是“只支持图片”。
- 监听挂在 `document` 上，组件销毁时不注销；同页多个 `PromptInput` 会互相串附件。
- 非图片附件最终进入 `ChatAttachment` 的 `file` 内容块，但 OpenAI Chat 路径会丢弃 `file` 块（仅保留 text/image），Anthropic 只映射 PDF。普通文件作为附件送到模型基本无效。

### 1.2 TUI 现状

- 已支持 `@path` 前缀和 `/attach <path>` 两种附件方式，但没有读取系统剪贴板的代码。
- 终端会先处理 Ctrl+V：文本和资源管理器文件由终端转成粘贴文本，图片则不会以文本进入程序。因此“Ctrl+V 截图”目前不可能工作。
- TUI 已启用 bracketed paste（`RawInputReader` 启动时发送 `ESC[?2004h`），并且已经解析 `ESC[200~` / `ESC[201~`，但 `InputKeyDecoder` 把粘贴起止事件解码为 `None`，`TuiInputEditorState.PasteActive` 从未被设置。

### 1.3 关键事实

1. **浏览器拿不到本地绝对路径**：即使 WebUI 运行在本机，`File` 对象也只有名字、大小、MIME 和内容。因此 Web 端“有路径就用路径”无法成立，只能上传。
2. **Windows Terminal 稳定版（本机 1.24.12741.0）在剪贴板只有图片时会发送空的 bracketed paste**（`ESC[200~` + `ESC[201~`）。这给了 TUI 一个不依赖命令、不改终端配置的 Ctrl+V 图片入口。
3. **Windows Terminal 1.25+ 开启 kitty 键盘协议后可能不再发送空粘贴**，只泄漏按键释放事件；这是已知边界，见 §8。
4. `.seeing/` 已被根 `.gitignore` 忽略，适合放上传文件。

## 2. 目标 / 非目标 / 反目标

### 2.1 目标

1. **WebUI 三个入口统一**：粘贴、拖拽、文件选择器使用同一套规则。
   - 图片/截图 → 附件（`ChatAttachment` 图片内容块），走现有预览组件。
   - 普通文件 → 上传到统一工作区目录，生成“路径引用”附件 chip；提交时把路径拼进消息正文，不作为 `ChatAttachment` 发送。
   - 三个入口都不再因 32 KB 限制失败。
2. **TUI Ctrl+V**：不输入命令。
   - 文本 → 正常插入。
   - 截图（剪贴板只有图片）→ 图片附件。
   - 复制的文件：图片文件 → 图片附件；非图片文件 → 本地路径（不复制）；多文件全部处理。
   - 项目根内路径用相对路径，项目根外用绝对路径。
   - TUI 既有的 `@path` 和 `/attach` 同步统一：图片进附件，非图片进路径，不再把普通文件转成 Base64 附件。
3. **统一上传布局**：所有上传（Web 的粘贴/拖拽/选择器，以及未来任何需要上传的入口）落到
   `<session effective root>/.seeing/uploads/<sessionId>/`，共用同一命名规则与 prompt 路径格式。
4. **复用附件预览**：Web 三个入口的图片和普通文件都进入 `AttachmentPreview`；普通文件显示为“路径引用”，不渲染二进制预览。
5. **Windows 优先**：TUI 剪贴板读取先实现 Windows；其他平台保留接口并显式提示暂不支持图片，文本粘贴不受影响。

### 2.2 非目标

- 不实现 Linux/macOS 的剪贴板图片读取（接口预留）。
- 不做 OCR、PDF/Office 正文提取。
- 不把 TUI 的本地普通文件复制进工作区；有真实路径就直接用路径。
- 不加 `/paste` 命令；TUI 的入口就是 Ctrl+V。
- 不改变 `ChatAttachment` / LLM Provider 的映射规则。
- 不清理已发送消息引用的上传文件；非图片上传会长期保留（路径在历史里）。

### 2.3 反目标

- 通过 Blazor JSInterop 传文件字节（无论 Base64 还是分块）；这会重新引入 32 KB 或 circuit 压力问题。
- 把普通文件塞进 `ChatAttachment`；OpenAI 路径会直接丢弃。
- 多个上传目录/多套命名规则。
- 使用全局键盘钩子或注册全局 Ctrl+V 热键；会侵入其他应用且有安全软件风险。

## 3. 约束

| 约束 | 影响 |
|------|------|
| Blazor Server 默认单条消息 32 KB | 粘贴/拖拽的文件内容必须走 HTTP 上传；选择器走 InputFile 分块流；JSInterop 只传元数据 |
| 浏览器无本地路径 | Web 必须上传；路径是服务端可解析的路径 |
| 终端先处理 Ctrl+V | TUI 依赖“空 bracketed paste”检测；部分终端会转发 0x16，需同时兼容 |
| `IFileSystem` seam | 宿主/工具禁止直接 `File.*`；二进制上传需要补能力 |
| OpenAI Chat 丢弃 `file` 块 | 普通文件走路径，不走附件 |
| 现有图片附件上限 10 MB | 图片继续沿用；普通文件上传上限另定 50 MB |
| TUI 跨平台 | 剪贴板读取做接口，Windows 先实现，其他平台降级 |
| `.seeing/` 已 gitignore | 上传统一放 `.seeing/uploads`，不污染仓库 |

## 4. 方案对比

### 4.1 文件归属

**A. 按“有无真实路径”分流（选中）**

- TUI：有路径 → 直接使用本地路径（项目根内相对、项目根外绝对）；没有路径（截图）→ 附件。
- Web：浏览器没有路径 → 粘贴、拖拽、文件选择器三个入口都上传到统一目录，再把路径写进消息。
- 图片无论有没有路径都作为附件（模型才能看到）。

优点：符合用户“能访问路径就用路径”的要求；避免复制导致的副本过期；普通文件不占用模型上下文。

缺点：Web 端无法使用本机路径，必须上传；TUI 项目根外路径在 `RestrictToWorkspace=true` 时仍受权限门闸约束。

**B. 所有文件都复制/上传到工作区（未选中）**

TUI 也把资源管理器文件复制到 `.seeing/uploads`，统一给相对路径。

优点：两端行为完全一致；开启工作区硬边界时最稳。

缺点：普通文件多一份副本；源文件修改后副本过期；不符合“有路径直接用路径”。仅在 Web 端（无路径）采用上传。

**C. 所有文件都作为 Base64 附件（未选中）**

实现最简单，但普通文件在 OpenAI 路径被丢弃，且大文件占内存/上下文，直接违背目标。

### 4.2 Web 传输方式

**粘贴/拖拽走 HTTP multipart 上传（选中）**

- JS 用 `fetch` 把 `File` 以 `FormData` 提交到服务端；服务端限长缓冲后写入工作区；只把元数据通过 JSInterop 回给 C#。
- 不受 32 KB 限制；大文件只经过一次 HTTP，不占 circuit 消息。
- 文件选择器不能走 `fetch`（`InputFile` 已经把流送到服务端），但复用同一个落盘服务、命名规则和路径引用模型。

**分块 Base64 走 JSInterop（未选中）**

- 需要 JS 分片、C# 重组、并发与取消处理；仍然占用 circuit 带宽和内存。

**调大全局 `MaximumReceiveMessageSize`（未选中）**

- 一行可止血，但影响所有 Blazor 消息，放大内存 DoS 风险，且 Base64 体积仍大；只适合临时验证，不适合作为方案。

**全流式落盘（未选中，本期）**

- 需要新增 `OpenWrite`/`WriteStreamAsync` 契约、临时文件 + 原子改名、逐段 multipart 读取；工作量和测试面明显增加。本期接受 50 MB/文件的限长缓冲写入，峰值内存约 2×单文件大小；后续有更大文件需求再补流式 API。

### 4.3 TUI Ctrl+V 触发方式

**空 bracketed paste（选中）**

- Windows Terminal 在图片-only 剪贴板时发送空的粘贴起止序列；TUI 检测到“粘贴开始/结束且没有文本”，读取系统剪贴板图片。
- 用户不需要命令、不需要改终端配置；文本/文件粘贴仍由终端正常完成。

**`/paste` 命令（未选中）**

用户明确要求 Ctrl+V，不接受输入命令；命令只适合作为极少数终端的兜底，不作为主路径。

**全局键盘钩子/全局热键（未选中）**

可以绕过终端截获 Ctrl+V，但侵入其他应用、可能触发安全软件，且焦点判断复杂；拒绝。

### 4.4 Web 普通文件的界面呈现

**附件 chip + 提交时拼路径（选中）**

- 普通文件上传后生成“路径引用”chip；输入框保持干净。
- 提交时把路径拼进正文；删除 chip 即取消引用，不会出现“chip 删了、正文还在”的同步问题。

**粘贴时立刻把路径插入输入框（未选中）**

可见性更直接，但删除 chip 需要同步删除正文中的路径；多文件、光标位置、撤销都更复杂。

## 5. 详细设计

### 5.1 统一上传布局

新增共享纯路径策略类 `PastedFileLayout`，放在
`src/support/Seeing.Agent.Tools.Support/FilePaste/PastedFileLayout.cs`
（支持层只依赖 Abstractions，WebUI/TUI 都可直接引用）。

```csharp
namespace Seeing.Agent.Tools.Support;

public static class PastedFileLayout
{
    public const string RootDirectory = ".seeing/uploads";
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxFilesPerPaste = 10;
    public const int MaxUploadRequestBytes = 100 * 1024 * 1024;

    public static string BuildSessionDirectory(string sessionRoot, string sessionId);
    public static string BuildStoredFileName(string originalName, DateTimeOffset now, string suffix);
    public static string BuildAbsolutePath(string sessionRoot, string sessionId, string storedFileName);
    public static bool TryGetProjectRelativePath(string projectRoot, string absolutePath, out string relativePath);
    public static string ToPromptPath(string projectRoot, string absolutePath);
    public static string FormatPromptBlock(IReadOnlyList<string> promptPaths);
    public static string AppendPromptPaths(string text, IReadOnlyList<string> promptPaths);
}
```

规则：

- 存储根：**会话有效根**（`IFileWorkspaceFactory.ResolveAsync(sessionId).RootPath`，即会话 `WorkingDirectory` ?? 项目根）。
  - 目录：`<sessionRoot>/.seeing/uploads/<safeSessionId>/`。
  - `safeSessionId` 只保留 `[A-Za-z0-9_-]`，其他字符替换为 `_`；为空时用 `default`。
- prompt 路径基准：**项目根**（`IWorkspaceProvider.GetProjectRoot()`）。
  - 原因：文件工具用 `Path.GetFullPath` 解析相对路径，进程 cwd 被 `WorkspaceProvider` 设为项目根；若按会话根写相对路径而会话根不同，模型 `read` 会找不到文件。
  - 规则：文件在项目根内 → 项目根相对路径；不在项目根内 → 绝对路径。
  - 统一正斜杠、反引号包裹；一个文件一行；追加时与已有文本之间保证一个换行。
  - 例：`` `.seeing/uploads/<sessionId>/report-20261023-153012-a1b2.pdf` ``；会话根在项目根外时可能是 `` `D:/other/.seeing/uploads/<sessionId>/...` ``。
- 文件名：`{safeBaseName}-{yyyyMMdd-HHmmss}-{4hex}{ext}`。
  - `safeBaseName` 去掉路径分隔符、控制字符、`<>:"/\|?*`，去首尾空白与点，最长 80 字符；为空时用 `pasted`。
  - 保留原扩展名；截图固定 `screenshot-{yyyyMMdd-HHmmss}-{4hex}.png`。
  - 4 位随机后缀避免同秒同名覆盖；如仍存在，追加 `-1`、`-2`。
- TUI 本地路径：
  - 项目根内：`TryGetProjectRelativePath` 成功则用相对路径；
  - 项目根外：使用绝对路径（同样反引号包裹）。
- 生命周期：
  - 非图片上传：保留，因为历史消息正文引用它；不做自动清理。
  - Web 图片上传：服务端写入统一目录 → C# 读成 Base64 附件 → 立即删除临时文件。
  - 删除 chip：尽力删除对应上传文件（仅未发送前）。
  - 发送后不删除；`.seeing/` 已忽略。

### 5.2 WebUI 数据流（粘贴 / 拖拽 / 文件选择器统一）

```
[粘贴] Ctrl+V  ─┐
[拖拽] drop     ─┼→ 收集 File 列表
[选择器] InputFile（C# 分块流）─┘
  → 图片：AttachmentViewModel(BinaryAttachment) → AttachmentPreview
  → 普通文件：
       粘贴/拖拽 → fetch POST /api/uploads?sessionId=... (multipart)
       选择器   → C# 直接调用 UploadedFileService.StoreAsync
     → 写入统一目录 → 元数据
     → AttachmentViewModel(PathReference, PromptPath)
  → 提交时 SessionWindow 分流：
       图片 → ChatAttachment
       路径引用 → 拼进 InboxMessage.Body
```

三个入口共用 `UploadedFileService`、`PastedFileLayout`、`AttachmentViewModel` 和 `AttachmentPreview`；区别只在文件字节如何到达服务端：粘贴/拖拽走 HTTP，选择器走 InputFile 分块流。

#### 5.2.1 共享落盘服务 `UploadedFileService`

WebUI 新增 Scoped 服务（`samples/Seeing.Agent.WebUI/Services/UploadedFileService.cs`），端点、`PromptInput` 和选择器共用：

```csharp
public sealed record StoredUpload(
    AttachmentDeliveryKind Kind,
    string FileName,
    string SessionRelativePath, // 相对会话有效根，用于 C# 定位
    string PromptPath,          // 项目根相对或绝对，用于正文
    long Size,
    string MimeType);

// StoreAsync：校验 -> 生成统一路径 -> IFileWorkspace.WriteBytesAsync
Task<StoredUpload> StoreAsync(
    string sessionId, string originalName, string? contentType,
    ReadOnlyMemory<byte> content, CancellationToken ct);

// 路径引用：按会话根定位、按项目根计算 PromptPath，生成 chip 模型
Task<AttachmentViewModel> CreatePathReferenceAsync(
    string sessionId, UploadedFileItem item, CancellationToken ct);

// 图片：读回字节、删除临时文件、生成 data: URL 附件
Task<AttachmentViewModel> CreateImageAttachmentAsync(
    string sessionId, UploadedFileItem item, CancellationToken ct);

// 删除未发送的路径引用文件
Task DeleteAsync(string sessionId, string sessionRelativePath, CancellationToken ct);
```

- `UploadedFileItem` 是端点返回给前端的元数据（`FileName`、`SessionRelativePath`、`Size`、`MimeType`、`Kind`），不含绝对路径。
- **回调路径不可信**：`HandleUploadedFiles` 的 `SessionRelativePath` 来自浏览器，必须经 `ResolveUploadPath(sessionId, relativePath)`：
  - 拒绝 rooted 路径、`..`、控制字符；
  - `Path.GetFullPath(Path.Combine(workspace.RootPath, relativePath))` 必须位于 `<sessionRoot>/.seeing/uploads/<safeSessionId>/` 之下；
  - 返回绝对路径。
- **锚定会话根**：`IFileWorkspace` 的 `ReadBytesAsync` / `DeleteAsync` 内部用 `Path.GetFullPath(path)`，基准是进程 cwd（项目根），不是 `RootPath`。因此服务必须始终传 `ResolveUploadPath` 得到的**绝对路径**，不能把 `SessionRelativePath` 直接传进去；否则会话根 ≠ 项目根时会读/删错文件。
- `StoreAsync` 内部通过 `IFileWorkspaceFactory.ResolveAsync(sessionId)` 取**会话有效根**写入；`finally` 中 `Release(sessionId)`，避免引用计数泄漏。
- `CreatePathReferenceAsync` 用 `IWorkspaceProvider.GetProjectRoot()` + `PastedFileLayout.ToPromptPath` 计算 `PromptPath`：项目根内相对，项目根外绝对。
- 不向浏览器暴露绝对路径。
- 图片临时文件在 `CreateImageAttachmentAsync` 读完后删除；普通文件保留。

#### 5.2.2 上传端点

新增 `POST /api/uploads`（具体路由名由实施决定），接受 multipart：

- 参数：`sessionId`（query 或 form）。
- 校验：
  - `sessionId` 非空且 `ISessionManager.Get(sessionId) != null`；
  - 通过 `IFileWorkspaceFactory.ResolveAsync(sessionId)` 取会话有效根；
  - 文件数 ≤ 10；单文件大小按类型限制；请求总量 ≤ 100 MB。
  - 显式设置端点请求体上限（Kestrel 默认 30 MB，否则 50 MB 文件会直接 413）。
- MIME：
  - 优先 `IFormFile.ContentType`；
  - 空或 `application/octet-stream` 时用扩展名映射（`FileContentSupport.GetMimeType`）；
  - `image/*` 视为图片。
- 写入：`IFormFile.OpenReadStream()` → 限长 `MemoryStream` → `UploadedFileService.StoreAsync`（内部 `WriteBytesAsync`，持写锁、经工作区门闸）。
- 返回（每个文件一项，不含绝对路径）：

```json
{
  "items": [
    {
      "kind": "image",
      "fileName": "image.png",
      "sessionRelativePath": ".seeing/uploads/<sessionId>/image-20261023-153012-a1b2.png",
      "size": 123456,
      "mimeType": "image/png"
    },
    {
      "kind": "file",
      "fileName": "report.pdf",
      "sessionRelativePath": ".seeing/uploads/<sessionId>/report-20261023-153012-b2c3.pdf",
      "size": 234567,
      "mimeType": "application/pdf"
    }
  ]
}
```

#### 5.2.3 JS：粘贴与拖拽共用上传

- 粘贴处理器绑定到 prompt 容器（`ContainerRef`），不再绑定 `document`；容器带 `data-session-id`。
- 拖拽处理器 `setupDropZone` 继续绑定容器，但不再在 JS 里 `fileToBase64` + `invokeMethodAsync`，改为调用同一个 `uploadFiles`。
- `uploadFiles(files)`：
  - 从 `clipboardData.files` / `dataTransfer.files` 取文件；粘贴为空时回退 `clipboardData.items` 中 `kind === 'file'`。
  - 没有文件时：粘贴直接返回（保留文本粘贴）；拖拽直接返回。
  - 有文件时 `preventDefault()`，客户端预检数量/大小。
  - `fetch` POST `FormData` → 一次 `invokeMethodAsync('HandleUploadedFiles', items)`。
  - 失败时 `invokeMethodAsync('HandleUploadFailed', message)`。
- 同容器只注册一次；组件销毁时移除监听并释放 `DotNetObjectReference`。

#### 5.2.4 文件选择器（C# 路径）

`PromptInput.OnInputFileChange` 不再把所有文件都塞进 `ChatAttachment`：

- `e.GetMultipleFiles(MaxAttachments)`；逐个处理。
- 图片（`ContentType` 为 `image/*`，或扩展名判定为图片）：
  - 读取流（≤ 10 MB）→ `data:{mime};base64,...` → `BinaryAttachment`。
- 普通文件：
  - 读取流（≤ 50 MB）→ `UploadedFileService.StoreAsync` → `PathReference` chip。
- 失败：加入 `Failed` 状态的附件并显示错误；不中断其他文件。
- 不改变 `InputFile` 的 `Accept` 与多选行为。

#### 5.2.5 `AttachmentViewModel` 扩展

```csharp
public enum AttachmentDeliveryKind
{
    BinaryAttachment = 0, // 图片；提交时进 ChatAttachment
    PathReference = 1     // 普通文件；提交时拼进正文
}
```

新增：

- `AttachmentDeliveryKind Kind { get; set; }`
- `string? PromptPath { get; set; }`（提交时写入正文的路径，项目根相对或绝对）
- `string? SessionRelativePath { get; set; }`（用于删除/定位，不展示）
- 计算属性 `bool IsPathReference => Kind == PathReference;`

原有 `Base64Content`、`Status`、`ErrorMessage` 保留。

#### 5.2.6 `AttachmentPreview` 扩展

- 图片：维持现有缩略图。
- 路径引用：
  - 不渲染图片；
  - 按扩展名显示图标；
  - 名称下方显示大小；
  - 加一个小标记“路径”；
  - `Tooltip` 显示完整 `PromptPath`；
  - 删除按钮行为不变，由 `PromptInput` 负责删除上传文件。
- `AddAttachment` 的大小校验对路径引用放宽到 50 MB（或路径引用不参与该组件内部校验；实际上传时已校验）。

#### 5.2.7 `PromptInput` 回调

`[JSInvokable] HandleUploadedFiles(UploadedFileItem[] items)`（粘贴/拖拽）：

- 图片：
  - 通过 `UploadedFileService.CreateImageAttachmentAsync` 读取服务端文件；
  - 校验 ≤ 10 MB；
  - 生成 `data:{mime};base64,...`；
  - 删除临时上传文件；
  - 加入 `Attachments`，`Kind = BinaryAttachment`。
- 普通文件：
  - 通过 `UploadedFileService.CreatePathReferenceAsync` 生成 chip；
  - `PromptPath` 由服务端按项目根计算，`SessionRelativePath` 保留用于删除；
  - 不读内容、不生成 Base64。
- 选择器路径：`StoreAsync` 已返回 `StoredUpload.PromptPath`，直接构造同样的 `AttachmentViewModel`，不经过 JS。
- 数量超过 `MaxAttachments` 的项忽略并提示。
- 失败项显示错误（内联 `Alert` 或 Ant `Message`），不丢失已成功项。
- 不直接修改 `InputText`；路径在提交时拼入正文。
- 删除路径 chip 时调用 `UploadedFileService.DeleteAsync`（best-effort）。

#### 5.2.8 提交分流（`SessionWindow.OnSubmitMessage`）

```csharp
var imageAttachments = args.Attachments
    .Where(a => a.Kind == AttachmentDeliveryKind.BinaryAttachment
             && a.Status == AttachmentUploadStatus.Completed)
    .Select(ToChatAttachment)
    .ToList();

var pathRefs = args.Attachments
    .Where(a => a.Kind == AttachmentDeliveryKind.PathReference
             && a.Status == AttachmentUploadStatus.Completed)
    .Select(a => a.PromptPath!)
    .ToList();

var body = PastedFileLayout.AppendPromptPaths(args.Text, pathRefs);
```

- `body` 写入 `InboxMessage.Body` / `ChatInput.Text`。
- `pathRefs` 不进入 `ChatAttachment`。
- `args.Text` 保持原样，提交被拒绝时 `PromptInput` 恢复输入与附件不会重复追加路径。
- 若 `body` 为空但仍有路径引用，按有内容处理。

#### 5.2.9 生命周期

- `PromptInput` 实现 `IAsyncDisposable`：移除 JS 粘贴/拖拽监听、释放 `DotNetObjectReference`；JS 调用失败时静默（circuit 可能已断开）。
- 上传中的错误显示在输入区上方；不阻塞后续操作。
- 未发送的路径上传文件在删除 chip 时删除；直接关闭页面可能留下孤儿文件（已知边界，`.seeing/` 忽略）。
- 选择器与粘贴/拖拽共用同一套 chip/提交逻辑，避免三套行为。

### 5.3 TUI 设计

#### 5.3.1 输入链路改造

当前：

- `RawInputReader` 发出 `TuiRawPasteStart` / `TuiRawPasteEnd`；
- `InputKeyDecoder` 两者都返回 `None`；
- `TuiInputEditorState.PasteActive` 从未赋值。

改为：

- `TuiInputAction` 增加：
  - `PasteStart`
  - `PasteEnd`
  - `PasteFromClipboard`（供终端直接转发 Ctrl+V 字节时使用）
- `InputKeyDecoder`：
  - `TuiRawPasteStart` → `PasteStart`
  - `TuiRawPasteEnd` → `PasteEnd`
  - `TuiRawKey` 且 Ctrl+V（0x16）→ `PasteFromClipboard`
- `TuiChatEngine`：
  - 维护 `_pasteBuffer`（`StringBuilder`）与 `_pasteActive`。
  - `PasteStart`：清空 buffer，置 `_pasteActive = true`，`_editor.SetPasteActive(true)`；不调用 `Apply`。
  - `PasteActive` 期间的 `InsertText` / `InsertNewline`：写入 buffer，不直接插入编辑器。
  - `PasteEnd`：`_pasteActive = false`，`_editor.SetPasteActive(false)`，调用 `HandleClipboardPasteAsync(buffer.ToString(), ct)`。
  - `PasteFromClipboard`（0x16）：调用 `HandleClipboardPasteAsync(null, ct)`；由处理器主动读剪贴板文本/文件/图片，不能只读图片，否则会破坏文本粘贴。
  - `Cancel`：清空 buffer 与 paste 状态，保持原有取消语义。

这样普通文本粘贴会等粘贴结束再一次性插入，行为等价；文件/图片粘贴可以在结束时统一决定。

#### 5.3.2 `HandleClipboardPasteAsync`

输入：`string? terminalPastedText`（空粘贴或 0x16 时为 `null`/空串）。

顺序：

1. 读取剪贴板文件列表（Windows 原生，快）。
2. **有文件**：以文件列表为准，忽略 `terminalPastedText`（终端可能只给了第一个路径或被截断）。
   - 按 MIME/扩展名分流：
     - 图片文件：`TuiAttachmentResolver.LoadAsync(path)` 读为附件；> 10 MB 时提示并跳过（不转为路径，保持“图片=附件”规则）。
     - 非图片文件：生成 prompt 路径；项目根内相对、项目根外绝对。
   - 图片加入待发附件；路径追加到编辑器。
   - 混合文件分别处理。
3. **无文件、`terminalPastedText` 非空**：按普通文本插入编辑器（当前行为）。
4. **无文件、`terminalPastedText` 为空**：读取剪贴板文本。
   - 有文本 → 插入编辑器。这样 0x16 终端不会退化成“插入一个 v”或“什么都粘贴不了”。
   - 无文本 → 读取剪贴板图片。
     - 成功：`screenshot-{yyyyMMdd-HHmmss}-{4hex}.png` 作为 image 附件；提示“已粘贴图片”。
     - 失败/无图片：静默（空剪贴板）或提示读取失败（异常）。
5. 多文件、纯附件提交沿用现有逻辑：文本为空但有待发附件时仍可提交。

读取剪贴板是同步操作：文件列表走原生 P/Invoke，通常毫秒级；图片回退 PowerShell 可能数百毫秒。本期接受这次短暂阻塞（最坏 3 秒超时），读取前先 `ForceRender` 显示“读取剪贴板…”，避免用户以为卡死；不引入后台读取与按键竞态。若后续实测体验不可接受，再把图片读取移到后台并在完成时插入。

路径追加：

- 使用 `PastedFileLayout` 生成反引号路径块；
- 在 `TuiInputEditorState` 增加 `AppendText(string)`（在光标处插入并推进光标），保持多行/光标行为；
- 路径块与已有文本之间保证换行。

#### 5.3.3 剪贴板读取抽象

```csharp
public interface IClipboardReader
{
    bool IsSupported { get; }
    IReadOnlyList<ClipboardFile> ReadFiles();
    string? ReadText();
    Task<ClipboardImage?> ReadImageAsync(CancellationToken ct = default);
}

public sealed record ClipboardFile(string Path, string MimeType, bool IsImage, long Size);
public sealed record ClipboardImage(byte[] PngBytes, string FileName, long Size);
```

Windows 实现：

- **文件列表**：P/Invoke `OpenClipboard` / `GetClipboardData(CF_HDROP)` / `DragQueryFileW`；复制路径后立即关闭剪贴板。
- **文本**：P/Invoke `CF_UNICODETEXT`；用于 `PasteFromClipboard`（0x16）和空粘贴时补读文本。
- **图片**：
  1. 优先读取注册格式 `"PNG"` 的 `HGLOBAL`，直接得到 PNG 字节；
  2. 否则回退 PowerShell：
     - `powershell.exe -NoProfile -STA -NonInteractive -EncodedCommand ...`
     - 脚本用 `Add-Type -AssemblyName System.Windows.Forms` + `[System.Windows.Forms.Clipboard]::GetImage()`；
     - 保存到 `%TEMP%/seeing-paste-<guid>.png`，输出路径；
     - C# 读取后删除临时文件；超时 3 秒。
  - 这样避免给 TUI 增加 WinForms/System.Drawing 包依赖；截图常见路径（PNG 格式）走快速原生分支。
- 不支持平台：`UnsupportedClipboardReader`，`IsSupported=false`；`ReadFiles`/`ReadImageAsync` 返回空，`ReadText` 返回 `null`。0x16 场景不做任何插入（不能退回插入字面 `v`）；文本粘贴仍依赖终端自身的 bracketed paste。图片/文件相关操作提示“当前平台暂不支持剪贴板附件”。

#### 5.3.4 终端边界

- Windows Terminal 1.24（本机）：图片-only 剪贴板发送空 bracketed paste → 走 §5.3.2 第 4 步。
- 复制普通文件：Windows Terminal 发送带引号的路径文本；`HandleClipboardPasteAsync` 读到文件列表后以列表为准，支持多选。
- Windows Terminal 1.25+ / kitty 键盘协议：可能没有空粘贴，也没有 0x16；截图 Ctrl+V 会失效。见 §8。
- conhost / 其他终端：若既不转发 0x16 也不发空粘贴，则无法支持图片粘贴；文本和文件路径不受影响。

#### 5.3.5 `@path` 与 `/attach` 统一

现状：`TuiAttachmentResolver.TryParseInline` 解析 `@path`，`HandleAttachAsync` 解析 `/attach`，两者最终都走 `StageAttachmentAsync` → `LoadAsync`，把所有文件读成 Base64 的 `TuiAttachment`。

改为同一套分类逻辑：

- 路径解析语法不变（`@path` 连续多个、`@"含 空格"`、`/attach` 多参数）。
- 每个路径先按 MIME/扩展名判定：
  - `image/*`：`LoadAsync` 读成 image 附件；> 10 MB 提示并跳过。
  - 非图片：不读内容、不生成 `TuiAttachment`；按项目根相对/绝对生成 prompt 路径。
- `@path`（`SubmitAsync` 内）：图片进入 `_pendingAttachments`；非图片路径追加到本次提交的正文，再提交。
- `/attach`（交互命令）：图片进入 `_pendingAttachments` 并在输入行显示；非图片直接用 `AppendText` 把路径插入编辑器，提示“已插入文件路径”。
- `/detach`：只移除待发图片附件；非图片路径已经作为输入文本存在，用户直接编辑/清空输入行，不再由 `/detach` 管理。
- 文件不存在或图片超限：提示并跳过该项，不中断其余文件。
- 纯图片附件仍允许空文本提交；非图片路径会自然构成非空正文。

这样 TUI 的四个入口（Ctrl+V、`@path`、`/attach`、图片附件）语义一致：**图片进附件，普通文件进路径**。

### 5.4 文件能力补充

上传需要二进制写入，而现有 `IFileWorkspace.WriteAsync` 只接受字符串，`IFileSystem` 没有字节写入。

新增：

```csharp
// IFileSystem
Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct = default);

// IFileWorkspace
Task WriteBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct = default);
```

- `LocalFileSystem.WriteAllBytesAsync` 调 `File.WriteAllBytesAsync`。
- `FileWorkspace.WriteBytesAsync`：
  - `EnsureAllowed(fullPath)`；
  - 创建父目录；
  - 持 `IPathWriteLock` 写锁；
  - 写字节；
  - 不做可编辑性检查（上传文件可能是二进制）。
- 现有 `IWorkspacePathGate` 与 `IPathWriteLock` 继续作为唯一门闸。
- 文件变更由工作区 OS watcher 感知；不额外发事件。

### 5.5 安全

- 端点校验 `sessionId` 存在；只写当前会话工作区的 `.seeing/uploads/<sessionId>`。
- 文件名取 `Path.GetFileName` 后清洗；最终 `Path.GetFullPath` 必须仍在 session 目录内。
- **回调（粘贴/拖拽）的 `SessionRelativePath` 视为不可信输入**：`UploadedFileService` 必须重新校验并解析为绝对路径，拒绝 rooted/`..`；读取和删除只允许发生在 `<sessionRoot>/.seeing/uploads/<safeSessionId>/` 下。
- `IFileWorkspace` 的读/删调用一律传绝对路径，避免相对路径按进程 cwd 解析到错误位置。
- 不向浏览器返回绝对路径。
- 限制文件数、单文件大小、请求总大小；超限返回 4xx。
- 上传内容只作为数据文件；不执行。
- TUI 本地路径不复制；agent 读取时仍走既有权限/工作区门闸。

### 5.6 限制与清理

| 项 | 值 |
|----|----|
| 图片附件 | 10 MB（沿用现有 `MaxFileSize`） |
| 普通文件上传 | 50 MB/个 |
| 单次上传文件数（粘贴/拖拽/选择器） | 10 |
| 单次 HTTP 请求体 | 100 MB |
| Web 图片临时文件 | 读回附件后立即删除 |
| Web 普通文件上传 | 保留（历史正文引用） |
| TUI 本地非图片文件 | 不读取内容、不限大小 |
| TUI 图片附件 | 10 MB |

## 6. 测试与验收

### 6.1 自动测试

- `PastedFileLayout`：命名清洗、会话目录、项目根相对/绝对路径、prompt 块、穿越拒绝、同名后缀。
- Web 端点（`WebApplicationFactory` 或服务级测试）：
  - 非图片上传返回 `sessionRelativePath` 且文件落在会话根统一目录；
  - 图片上传返回 `kind=image`；
  - 超限、session 不存在、非法文件名、路径穿越被拒；
  - 会话根在项目根外时，`PromptPath` 为绝对路径；在项目根内时为相对路径。
- Web 提交分流（纯函数抽取）：图片 → `ChatAttachment`，路径引用 → 正文，二者混合。
- Web 三入口一致性（纯函数/服务级）：
  - 粘贴、拖拽、选择器的文件都经同一 `UploadedFileService` 规则；
  - 选择器普通文件不再生成 `ChatAttachment`；
  - 拖拽不再调用 Base64 JSInterop。
  - `HandleUploadedFiles` 的 `SessionRelativePath` 被篡改（rooted、`..`、越出 uploads 目录）时，读/删均被拒绝。
  - 会话根在项目根外时，图片粘贴的读回与 chip 删除仍落在会话根（绝对路径锚定正确）。
- TUI 粘贴规划（纯函数抽取，注入 fake `IClipboardReader`）：
  - 空粘贴 + 图片 → 附件；
  - 空粘贴 + 文件 → 路径；
  - 文本粘贴 → 文本；
  - 0x16 + 文本 → 文本（不退化成字面 `v`）；
  - `@path`：图片 → 附件，非图片 → 路径；
  - `/attach`：图片 → 附件，非图片 → 路径；`/detach` 只移除图片；
  - 文件 + 文本（终端路径）→ 以文件列表为准；
  - 图片文件 → 附件；非图片文件 → 路径；混合；
  - 项目根内相对、项目根外绝对；
  - 多文件；超限图片。
- TUI 输入解码：`PasteStart` / `PasteEnd` / `PasteFromClipboard` 映射。
- `IFileSystem.WriteAllBytesAsync` / `IFileWorkspace.WriteBytesAsync` 单测（含门闸、写锁、父目录创建）。
- Playwright（WebUI）：
  - 粘贴 > 32 KB 图片 → 附件 chip；
  - 拖拽 > 32 KB 图片 → 附件 chip；
  - 粘贴/拖拽普通文件 → 路径 chip；
  - 选择器普通文件 → 路径 chip；
  - 删除路径 chip → 上传文件被删除。
- 回归：现有 `Seeing.Agent.Tui.Tests`、`Seeing.Agent.WebUI.Tests`、`Seeing.Agent.Tests`（按 AGENTS.md 用 VSTest 直跑）。

### 6.2 手工验收

WebUI（本机与远程各一次）：

1. 截图 → Ctrl+V → 出现图片预览 chip；发送后模型能看到图片。
2. 复制一个 > 32 KB 的截图 → Ctrl+V 仍成功（验证不再受 32 KB 限制）。
3. 复制 `.txt` / `.pdf` → Ctrl+V → 出现“路径”chip；发送后正文含项目根相对路径（或会话根在项目根外时的绝对路径）。
4. 一次粘贴多个文件 → 全部出现 chip；发送后正文含全部路径。
5. 删除路径 chip → 文件被删除、正文不包含该路径。
6. 在输入框内粘贴纯文本 → 文本正常插入，不出现附件。
7. 拖拽图片/普通文件 → 与粘贴一致。
8. 文件选择器选择图片/普通文件 → 与粘贴一致；普通文件不再作为 `ChatAttachment`。

TUI（Windows Terminal 1.24）：

1. 截图 → Ctrl+V → 待发附件出现 `screenshot-...png`；发送后模型能看到图片。
2. 资源管理器复制普通文件 → Ctrl+V → 输入框出现反引号路径；不产生附件。
3. 资源管理器复制图片文件 → Ctrl+V → 图片附件（不是路径）。
4. 复制多个文件 → Ctrl+V → 全部路径/图片按规则处理。
5. 复制文本 → Ctrl+V → 正常插入。
6. 输入框已有文本时粘贴文件 → 路径追加在光标处，文本不丢。
7. `@path file.txt` → 正文出现路径；`@path image.png` → 图片附件。
8. `/attach file.txt` → 正文出现路径；`/attach image.png` → 图片附件；`/detach` 不影响路径文本。

## 7. 实施拆分（建议）

1. 共享层：`PastedFileLayout` + 二进制写能力 + 单测。
2. Web 后端：上传端点 + `UploadedFileService` + 端点测试。
3. Web 前端：JS 粘贴/拖拽上传改造、选择器 C# 落盘、`PromptInput` 回调、`AttachmentViewModel`/`AttachmentPreview`、`SessionWindow` 分流、Playwright 验收。
4. TUI：输入动作与粘贴缓冲、剪贴板读取器、粘贴规划、`@path` / `/attach` 分类、引擎接线、单测。
5. 文档：更新 TUI README 的 Ctrl+V/附件说明；必要时在架构文档登记上传目录约定。

## 8. 风险与已知边界

- **WT 1.25+ kitty 键盘**：可能不再发空 bracketed paste。需要在该版本上实测；若命中，后续增加 kitty 按键释放检测或一个非命令的备用键。本期以 1.24 验收。
- **非 Windows 平台**：仅接口预留；图片/文件粘贴提示不支持，文本不受影响。0x16 场景因无法读文本而不插入任何内容，不能退回插入字面 `v`。
- **PowerShell 图片回退**：首次调用有启动延迟（约数百毫秒）；PNG 格式快速路径可覆盖多数截图。PowerShell 不可用时提示失败。
- **浏览器差异**：Chromium/Edge 支持从资源管理器粘贴/拖拽文件；Firefox/Safari 可能只支持图片或部分类型。失败时明确提示“请选择文件”。
- **行为变更**：三个入口统一后，选择器和拖拽的非图片文件也不再作为 `ChatAttachment`，因此 PDF 不再作为 Anthropic `document` 附件；统一走路径。TUI 的 `@path` / `/attach` 同样改为“图片附件、非图片路径”，`/detach` 只管理图片附件，非图片路径作为输入文本由用户直接编辑。若后续需要保留 PDF 文档能力，需要单独设计（例如按 MIME 例外 + Provider 能力判断）。
- **PDF/Office 路径**：普通文件给路径后，现有 `read` 工具对 PDF/Office 只返回附件元信息，不提取正文；模型仍可能读不到内容。需要后续文档提取能力。
- **Web 上传内存**：本期采用 50 MB/文件、100 MB/请求的限长缓冲写入；峰值内存约 2×单文件大小。更大文件需求需要后续补流式写 API。
- **上传孤儿文件**：未发送就关闭页面/删除失败会留下文件；`.seeing/` 已忽略，暂无自动清理。
- **路径中的反引号/控制字符**：上传文件名会清洗；TUI 项目根外路径若含反引号，prompt 反引号包裹可能歧义；实施时对本地路径做最小转义或拒绝控制字符。
- **会话根与项目根不同**：上传落在会话根，prompt 路径按项目根相对化；不在项目根内时写绝对路径。这是为了匹配文件工具的相对路径基准，不是遗漏。
- **多会话同名**：目录按 session 隔离；同一会话内用时间戳+随机后缀避免覆盖。
- **TUI 读取阻塞**：图片回退 PowerShell 最坏阻塞输入循环 3 秒；本期接受，后续可改后台读取。
- **`RestrictToWorkspace=true`**：TUI 项目根外路径仍会被权限门闸拦截；这是用户选择“直接用路径”的预期边界，必要时权限流程会 Ask。

## 9. 留给实施者的决定

- 端点具体路由名、错误码与错误文案。
- 上传 UI 的“上传中”呈现方式（内联提示/按钮态）。
- MIME 魔数嗅探是否补充（当前以 Content-Type + 扩展名为主）。
- PowerShell 回退脚本的编码细节（超时已定为 3 秒）。
- 文件名时间戳使用本地时区还是 UTC（建议本地，便于用户识别）。
- 测试中 fake `IClipboardReader` / 文件系统夹具的组织方式。
- Kestrel 端点请求体上限的具体配置位置。
- 是否将大小限制做成配置项（本期常量即可）。
