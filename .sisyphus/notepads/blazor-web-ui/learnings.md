-# Blazor WebUI learnings

- Created a minimal Blazor Server UI sample at samples/Seeing.Agent.WebUI to wire Seeing.Agent core DI and AntDesign.
- Fix: Resolve NU1605 version conflicts by updating central package versions.
- Key files: csproj targeting net10.0, Program.cs with AddSeeingAgent, AntDesign, AppState (Singleton) and SessionState (Scoped).
- Next steps: verify dotnet build and then wire a simple Index page with AntDesign components if needed.
- 状态更新：已添加 CPM 相关配置尝试，当前遇到 AntDesign 的稳定版本未对齐导致构建失败，需进一步确定合适的版本或正确的 CPM 配置。
- 已完成 NU1605 修复：将 Directory.Packages.props 中以下版本更新为兼容 .NET 10.0 的版本：
- - Microsoft.Extensions.DependencyInjection.Abstractions: 10.0.3
- - Microsoft.Extensions.Logging.Abstractions: 10.0.3
- - 额外新增 Microsoft.Extensions.Logging.Abstractions 的版本声明以统一传递依赖。
- 重新执行 dotnet restore 时，WebUI 项目及 Seeing.Agent.csproj 的依赖链均未再产生 NU1605 错误。
- 下一步建议：1) 使用可用的 AntDesign 版本（例如 2.x 的稳定分支）并确保 CPM 配置正确；2) 或保留最小依赖直接引用的方式以简化构建流程。
- 备注：当前构建在本机环境仍出现 OpenAiClient 的 ChatInputAudioFormat 未找到的编译错误，需要进一步升级 OpenAI 依赖或调整命名空间以完成完整构建。
## Sat Apr 11 20:59:31     2026 Wave 1 Task 1 Status

COMPLETED:
- Project scaffolding created: samples/Seeing.Agent.WebUI/
- DI registration configured in Program.cs
- AppState (Singleton) and SessionState (Scoped) created
- AntDesign and Markdig packages configured

BLOCKING ISSUE:
- Seeing.Agent.csproj has code error: OpenAI SDK 2.0.0 missing ChatInputAudioFormat type (CS0246)
- This is a pre-existing code issue, not Wave 1 scope

CPM CONFIGURATION:
- Directory.Build.props created to enable CPM
- Directory.Packages.props updated to .NET 10.0 versions
- WebUI project configured with explicit versions (not using CPM)

## Sat Apr 11 21:xx:xx     2026 Wave 1 Task 2 Status

COMPLETED:
- Created Models folder: samples/Seeing.Agent.WebUI/Models/
- Created MessageViewModel.cs with Id, Role, Content, Reasoning, Timestamp, ToolCalls, IsComplete
- Created SessionViewModel.cs with Id, Title, CreatedAt, UpdatedAt, Messages
- Created ToolCallViewModel.cs with Id, Name, Status, Result, Error
- Fixed App.razor: Added @using directives for Routing, Web, Shared namespaces; Changed RouteData to @routeData
- Fixed MainLayout.razor: Added AntDesign Layout/Sider/Content structure

KEY FIX:
- App.razor is in root folder, not Pages folder, so _Imports.razor doesn't apply to it
- Solution: Add usings directly to App.razor or create a root-level _Imports.razor
- RouteView's RouteData parameter needs @ prefix for the Context variable: `RouteData="@routeData"`

BUILD STATUS:
- 0 errors, 3 warnings (environment variable warnings, unrelated to code)
## Wave 1: State, Event, Persistence, Slots
- Created AppState.cs (singleton) and SessionState.cs (scoped) for cross-component and per-session data.
- Implemented EventStreamHandler with event types: StreamDeltaEvent, StreamCompleteEvent, ToolCallEvent, SubAgentEvent, ErrorEvent; updates to SessionState on ProcessEvent.
- Implemented JsonPersistenceService with SaveSessionAsync, LoadSessionAsync, ListSessionsAsync, DeleteSessionAsync, persisting to ~/.seeing/sessions/*.json.
- Implemented SlotRegistry to register RenderFragment slots; SlotHost.razor renders registered slots.
- Declared slot names: home_logo, home_prompt, session_prompt, sidebar_content, sidebar_footer, message_toolbar.
- Build verification: dotnet build samples/Seeing.Agent.WebUI should succeed with 0 errors.
## Sat Apr 11 21:21:02     2026 Wave 1 COMPLETE

**All Wave 1 tasks completed:**
- Task 1: Project Scaffolding ✓
- Task 2: Display Models ✓
- Task 3: State Management ✓
- Task 4: Event Stream Handler ✓
- Task 5: JSON Persistence ✓
- Task 6: Slot Extension ✓

**Created files:**
- samples/Seeing.Agent.WebUI/ (Blazor Server project)
- Models/ (MessageViewModel, SessionViewModel, ToolCallViewModel)
- State/ (AppState, SessionState)
- Services/ (EventStreamHandler, JsonPersistenceService)
- Shared/ (MainLayout, SlotHost)

**Build status:** dotnet build succeeds with 0 errors

- DI Fixes implemented for WebUI:
- AppState now uses a public constructor and no private Lazy singleton; DI will instantiate as a singleton when registered.
- ISessionManager registered as Singleton to resolve TodoWriteTool's dependency on a scoped service.
- Updated DI registrations in ServiceCollectionExtensions accordingly.

## Sat Apr 11 2026 Wave 2: Core UI Components COMPLETE

**All Wave 2 tasks completed:**
- Task 7: MainLayout with Sidebar ✓
- Task 8: MessageList Component ✓
- Task 9: MessageItem (renamed to ChatMessageItem) Component ✓
- Task 10: PromptInput Component ✓
- Task 11: Agent/Model Selector Components ✓

**Key learnings for AntDesign Blazor nightly (2.0.0-nightly-250526143149):**

### AntDesign Enum Usage
- **DO NOT** use string literals for enum parameters (e.g., `"text"`, `"small"`, `"light"`)
- **MUST** use proper enum syntax: `ButtonType.Text`, `ButtonSize.Small`, `SiderTheme.Light`
- Available enums: `ButtonType`, `ButtonSize`, `AvatarSize`, `SpinSize`, `SiderTheme`, `MenuTheme`, `MenuMode`, `BadgeStatus`, `DividerType`
- Example: `<Button Type="@ButtonType.Text" Size="@ButtonSize.Small">`

### Namespace Requirements
- `AntDesign.Components` namespace does NOT exist in nightly version
- All components are directly in `AntDesign` namespace
- Remove `@using AntDesign.Components` from all razor files

### Component Name Conflicts
- AntDesign has built-in `MessageItem` component
- Rename custom message components to avoid conflicts (e.g., `ChatMessageItem`)
- Use fully qualified namespace when referencing: `<Seeing.Agent.WebUI.Components.ChatMessageItem>`

### Event Callback Patterns
- `Select.OnSelectedItemChanged` expects `EventCallback<T>` not lambda
- Use `@bind-Value` with `ValueChanged` callback for simpler two-way binding
- Avoid: `OnSelectedItemChanged="@(e => OnChange((T)e))"` (type conversion issues)
- Prefer: Just use `@bind-Value="@SelectedValue"` with `[Parameter] EventCallback<string> SelectedValueChanged`

### Design System Compliance
- All CSS uses design tokens from `design-system.css`
- Variables: `--color-*`, `--space-*`, `--font-size-*`, `--radius-*`, `--shadow-*`
- Message role colors: `--color-user-bg`, `--color-assistant-bg`, `--color-system-bg`, `--color-tool-bg`
- Spacing: 4px base (`--space-1 = 4px`, `--space-2 = 8px`, etc.)

### Markdown Rendering
- Use Markdig pipeline with extensions: `UseAutoLinks()`, `UseTaskLists()`, `UsePipeTables()`
- Render as `MarkupString`: `@((MarkupString)RenderMarkdown(content))`

**Created/Updated files:**
- Shared/MainLayout.razor (AntDesign Layout with Sider)
- Components/MessageList.razor (message list with auto-scroll)
- Components/ChatMessageItem.razor (renamed from MessageItem, Markdown support)
- Components/PromptInput.razor (TextArea with Enter-to-submit)
- Components/AgentSelector.razor (Select with custom templates)
- Components/ModelSelector.razor (Select with Provider tags)
- wwwroot/css/message-item.css (added reasoning section styles)
 
**Build status:** dotnet build succeeds with 0 errors

## Sat Apr 11 2026 Wave 4: Session & Enhancement COMPLETE

**All Wave 4 tasks completed:**
- Task 16: Session Sidebar ✓
- Task 17: File Attachment Support ✓
- Task 18: Cancel Execution Support ✓
- Task 19: Session Persistence Integration ✓
- Task 20: Home Page with Welcome UI ✓

**Key learnings for Wave 4:**

### Blazor ContextMenu and foreach capture
- **DO NOT** use `@oncontextmenu.prevent="@((e) => Handler(e, session))"` directly with foreach variable
- Use `@oncontextmenu:preventDefault="true"` + `@oncontextmenu="@((e) => Handler(e, currentSession))"` pattern
- Capture foreach variable BEFORE using in lambda: `var currentSession = session;`
- Lambda in @oncontextmenu.prevent causes CS1660 error (cannot convert lambda to bool)

### AntDesign Modal OkText/CancelText
- String literals like "确定" and "取消" work but can cause encoding issues
- Use const string fields for button text: `private const string OkText = "确定";`
- Then use `OkText="@OkText"` in Modal component

### AntDesign Icon Types (Missing Icons)
- Many AntDesign icons don't exist in IconType.Outline:
  - Missing: `Bot`, `Branch`, `FileAudio`, `FileVideo`, `Danger` (ButtonType)
  - Alternatives: `Team`, `History`, `Sound`, `VideoCamera`, `CloseCircle`
- Always check available icons before using them

### AntDesign TextArea @ref Issue
- **DO NOT** use `@ref="@TextAreaRef"` on AntDesign TextArea component
- AntDesign TextArea is a component, not native HTML element
- Cannot be assigned to `ElementReference`
- Use wrapper div or native textarea for ElementReference needs

### AntDesign TextArea AutoSize
- **DO NOT** use tuple syntax: `AutoSize="(3, 8)"` (causes CS1503 error)
- Use simple boolean: `AutoSize="true"` for auto-sizing
- For min/max rows, use AutoSize property with proper type

### File Attachment Base64 Handling
- Use JSInterop for file handling: `FileReader.readAsDataURL()`
- Create FileInfoJsInterop class for passing data between JS and Blazor
- Use `[JSInvokable]` methods for receiving data from JS
- File size limit: 10MB default, configurable via parameter

### CancellationToken Management
- Store CancellationTokenSource in SessionState (Scoped service)
- Pass to AgentExecutor.ExecuteAsync for cancellation support
- Handle `OperationCanceledException` gracefully in UI
- Dispose CancellationTokenSource properly on completion/cancellation

### Session Persistence Pattern
- JsonPersistenceService should be Singleton (shared across sessions)
- Store sessions in `~/.seeing/sessions/*.json`
- Auto-save on StreamCompleteEvent via EventStreamHandler
- Load most recent session on startup
- Use JsonSerializerOptions with PropertyNamingPolicy.CamelCase

### Design System for Welcome Page
- Use gradient background: `linear-gradient(135deg, ...)`
- Feature cards use: `flex: 1`, hover border change, shadow
- Large prompt wrapper: 2px border, focus-within glow effect
- Centered layout with `min-height: 100vh`

### NavigationManager Usage
- Inject NavigationManager in pages for routing
- Use `NavigationManager.NavigateTo("/path")` for navigation
- Check for null before using: `NavigationManager?.NavigateTo("/")`

**Created files:**
- Components/SessionSidebar.razor (session list with search, context menu)
- Components/SessionContextMenu.razor (rename, delete, branch actions)
- Components/AttachmentPreview.razor (file/image preview with remove)
- Models/AttachmentViewModel.cs (file attachment model)
- Models/PromptSubmitEventArgs.cs (submit event args + JSInterop types)
- Pages/Home.razor (welcome page with large input)
- wwwroot/css/session-sidebar.css (sidebar styles)
- wwwroot/css/home-welcome.css (welcome page styles)

**Modified files:**
- State/SessionState.cs (added CancellationTokenSource management)
- Services/JsonPersistenceService.cs (enhanced for SessionState)
- Services/EventStreamHandler.cs (async processing + auto-save)
- Components/PromptInput.razor (file upload + cancel button)
- Pages/Index.razor (integrated persistence + cancellation)
- Program.cs (registered JsonPersistenceService, EventStreamHandler)
- Pages/_Host.cshtml (added CSS references)
- wwwroot/js/app.js (file handling functions)
- wwwroot/css/prompt-input.css (attachment styles)

**Build status:** dotnet build succeeds with 0 errors

## Sat Apr 11 2026 Wave 3: Tool & Permission Components COMPLETE

**All Wave 3 tasks completed:**
- Task 12: ToolCallCard Component ✓
- Task 13: ToolCallTimeline Component ✓
- Task 14: PermissionModal Component ✓
- Task 15: BlazorPermissionChannel Service ✓

**Key learnings for Wave 3:**

### AntDesign Card Component
- **DO NOT** use `<CardHeader>` and `<CardBody>` child components (not valid in AntDesign Blazor)
- **MUST** use `<Title>` RenderFragment for card header
- Example: `<Card><Title><div>...</div></Title>...content...</Card>`
- Card body content goes directly inside Card element

### AntDesign Tag Component
- **DO NOT** use `Size="@TagSize.Small"` (TagSize enum does not exist)
- Use inline Style for small tags: `Style="font-size: var(--font-size-xs);"`
- Available colors: "warning", "processing", "success", "error", "default"

### PermissionRequest Class Structure
- Actual `PermissionRequest` class (from ITool.cs) has:
  - `Permission` (string)
  - `Patterns` (List<string>)
  - `Metadata` (Dictionary<string, object>)
- **DO NOT** assume Resource, Message, RiskLevel, SessionId properties
- Use Metadata dictionary for custom data: `request.Metadata["risk_level"]`

### TaskCompletionSource Pattern
- Use `TaskCompletionSource<T>` for async UI blocking operations
- Create with `TaskCreationOptions.RunContinuationsAsynchronously` to avoid deadlock
- Set timeout with `.WaitAsync(TimeSpan.FromMinutes(5))` to prevent infinite wait
- Pattern: Create TCS -> Register in dictionary -> Fire event -> Wait -> Handle result -> Cleanup

### ToolCallViewModel Extensions
- Extended model with: Parameters, StartTime, EndTime, DurationMs, MessageId, Description
- Added utility methods: `CalculateDuration()`, `GetFormattedDuration()`
- Duration formatting: <1000ms: "Xms", <60s: "X.XXs", else: "X.Xmin"

### Design Token Usage (Verified)
- Status colors: Pending(--color-warning), Running(--color-primary), Success(--color-success), Failed(--color-error)
- All spacing uses `--space-*` system (no hardcoded pixels)
- Background colors use semantic tokens: `--color-success-bg`, `--color-error-bg`
- Font sizes use `--font-size-xs` for compact text

**Created files:**
- Components/ToolCallCard.razor (AntDesign Card with params/result/error display)
- Components/ToolCallTimeline.razor (Timeline with expandable details)
- Components/PermissionModal.razor (Modal with allow/deny/remember options)
- Models/PermissionRequestViewModel.cs (PermissionRequestType enum + view model)
- Services/BlazorPermissionChannel.cs (IPermissionChannel implementation with TCS)
- wwwroot/css/tool-components.css (shared styles for tool/permission components)

**Modified files:**
- Models/ToolCallViewModel.cs (extended with duration, timing, description)

**Build status:** dotnet build succeeds with 0 errors

## Sat Apr 11 2026 Wave 5: Integration COMPLETE

**All Wave 5 tasks completed:**
- Task 21: Session Page Integration ✓
- Task 22: End-to-End Message Flow ✓
- Task 23: Multi-Session Management ✓
- Task 24: Final Polish & Error Handling ✓

**Key learnings for Wave 5:**

### AgentExecutor Integration
- Created AgentExecutor service to integrate Seeing.Agent framework with WebUI
- Supports streaming responses via EventStreamHandler
- Handles cancellation via CancellationToken
- Placeholder for real LLM API integration (currently using simulated responses)

### Blazor Event Handling for Streaming
- Use `await InvokeAsync(StateHasChanged)` for UI updates from async events
- EventStreamHandler triggers OnStateChanged event for component subscription
- All events: StreamDeltaEvent, StreamCompleteEvent, ToolCallEvent, ToolResultEvent, SubAgentEvent, ErrorEvent

### Multi-Session Management Pattern
- AppState holds Sessions list (cached metadata)
- SessionState holds current session data (Scoped service)
- MainLayout manages CRUD via SessionSidebar callbacks
- Session.razor handles rename/branch/clear operations
- Navigation: `/session` (new), `/session/{SessionId}` (existing)

### AntDesign MessageService Usage
- Inject `IMessageService` for toast notifications
- Call `MessageService.Success()`, `MessageService.Info()`, `MessageService.Warning()`, `MessageService.Error()`
- Use after async operations complete (not during)

### Error Handling Service
- ErrorHandlingService centralizes error management
- GetUserFriendlyMessage() converts exceptions to user text
- Categories: Errors, SuccessMessages, InfoMessages
- OnStateChanged event for UI subscription

### Binding Parameter Conflict (RZ10010)
- **DO NOT** use both `@bind-X` and `XChanged` at the same time
- Use either: `@bind-X="@Value"` OR `X="@Value" XChanged="@OnChanged"`
- Example: `SelectedAgent="@AppState.SelectedAgent" SelectedAgentChanged="@OnAgentChanged"`

### FunctionToolSchema Type
- Located in `Seeing.Agent.Llm` namespace (from OpenAI SDK)
- GetToolSchemas() returns `List<FunctionToolSchema>`
- Import: `using Seeing.Agent.Llm;`

**Created files:**
- Pages/Session.razor (full session page with message flow)
- Services/AgentExecutor.cs (Agent execution with streaming)
- Services/ErrorHandlingService.cs (error handling service)
- Services/EventStreamHandler.cs (updated with more events)
- State/AppState.cs (expanded for multi-session)
- wwwroot/css/session-page.css (session page styles)

**Modified files:**
- Shared/MainLayout.razor (integrated SessionSidebar, message feedback)
- Pages/Index.razor (updated for session management)
- Pages/Home.razor (fixed NavigationManager injection)
- Pages/_Host.cshtml (added session-page.css)
- State/SessionState.cs (added execution methods)
- Program.cs (registered AgentExecutor, ErrorHandlingService)
- wwwroot/css/index.css (updated styles)

**Build status:** dotnet build succeeds with 0 errors
