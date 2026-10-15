# DeepSeek Provider Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `plugs/providers` 新增 DeepSeek LLM Provider 扩展包，经 DI 在 WebUI 默认启用；用户级自管 ApiKey；动态拉取模型；零改动 `Seeing.Agent` DeepSeek 专用代码。

**Architecture:** `DeepSeekProvider` 实现 `ILlmProvider` + `IConfigurableLlmProvider`；`ILlmClientFactory` 以 OpenAI 兼容配置创建 Client；`DeepSeekConfigStore` 读写 `~/.seeing/deepseek.json`；`DeepSeekProviderHostedService` 启动时 `IProviderRegistry.Register`。

**Tech Stack:** .NET 10 / CPM、`Seeing.Agent` 公开 LLM API、`Seeing.ConfigSchema`、xUnit v3 + FluentAssertions + Moq、Microsoft.Extensions.Hosting.Abstractions

**Spec:** `docs/superpowers/specs/2026-08-05-deepseek-provider-plugin-design.md`

## Global Constraints

- TFM: `net10.0`；启用 ImplicitUsings + Nullable
- 包版本走 `Directory.Packages.props`（CPM）；WebUI 本身禁用 CPM，但 DeepSeek 项目启用
- **禁止**修改 `src/Seeing.Agent/**` 增加 DeepSeek 固定节名、预置 Providers 或改 `OpenAiModelsClient` 可见性
- 配置仅写 `{UserSeeingDirectory}/deepseek.json`；永不写项目 `.seeing`；不写 `Providers[deepseek]`
- BaseUrl 固定 `https://api.deepseek.com/v1`；UI 仅 ApiKey
- Provider Id = `deepseek`；ownerExtensionId = `seeing.provider.deepseek`
- **不要** git commit（用户要求全程不提交）；跳过所有 commit 步骤

## File Structure

| 路径 | 职责 |
|------|------|
| `plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj` | 扩展库工程 |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekOptions.cs` | `{ ApiKey }` |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekConfigStore.cs` | 用户级 JSON 读写 |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekModelsClient.cs` | List Models HTTP |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekProvider.cs` | Provider + schema + Client/模型 |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekProviderHostedService.cs` | 启动注册到注册表 |
| `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekExtension.cs` | 可选 `IExtension` |
| `plugs/providers/Seeing.Provider.DeepSeek/ServiceCollectionExtensions.cs` | `AddDeepSeekProvider` |
| `tests/Seeing.Provider.DeepSeek.Tests/*` | 单元测试 |
| `Seeing.Agent.slnx` | 增加 `/plugs/providers/` 与测试项目 |
| `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` | ProjectReference |
| `samples/Seeing.Agent.WebUI/Program.cs` | `AddDeepSeekProvider()` |

---

### Task 1: 脚手架（工程 + slnx）

**Files:**
- Create: `plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj`
- Create: `tests/Seeing.Provider.DeepSeek.Tests/Seeing.Provider.DeepSeek.Tests.csproj`
- Modify: `Seeing.Agent.slnx`

**Interfaces:**
- Consumes: 无
- Produces: 可编译的空库 + 空测试工程

- [ ] **Step 1: 创建 Provider 项目文件**

`plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Provider.DeepSeek</RootNamespace>
    <AssemblyName>Seeing.Provider.DeepSeek</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\Seeing.Agent\Seeing.Agent.csproj" />
    <ProjectReference Include="..\..\..\src\Seeing.ConfigSchema\Seeing.ConfigSchema.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

注意：路径相对 `plugs/providers/Seeing.Provider.DeepSeek/` 上溯三级到仓库根再进 `src/`。若本机相对路径不对，用实际目录核对一次。

- [ ] **Step 2: 创建测试项目**

`tests/Seeing.Provider.DeepSeek.Tests/Seeing.Provider.DeepSeek.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Provider.DeepSeek.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Moq" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\plugs\providers\Seeing.Provider.DeepSeek\Seeing.Provider.DeepSeek.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 更新 slnx**

在 `Seeing.Agent.slnx` 增加：

```xml
  <Folder Name="/plugs/providers/">
    <Project Path="plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj" />
  </Folder>
```

并在 `/tests/` 文件夹内增加：

```xml
    <Project Path="tests/Seeing.Provider.DeepSeek.Tests/Seeing.Provider.DeepSeek.Tests.csproj" />
```

- [ ] **Step 4: 验证可还原/编译空工程**

Run:

```bash
dotnet build plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj
dotnet build tests/Seeing.Provider.DeepSeek.Tests/Seeing.Provider.DeepSeek.Tests.csproj
```

Expected: 成功（随后 Task 2 立即加源码）。

- [ ] **Step 5: Skip commit**（用户要求不提交）

---

### Task 2: DeepSeekOptions + ConfigStore（TDD）

**Files:**
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekOptions.cs`
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekConfigStore.cs`
- Create: `tests/Seeing.Provider.DeepSeek.Tests/DeepSeekConfigStoreTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceProvider?`（可选）、`ILogger<DeepSeekConfigStore>`
- Produces:
  - `class DeepSeekOptions { string? ApiKey { get; set; } }`
  - `class DeepSeekConfigStore`
    - `string ConfigFilePath { get; }`
    - `Task<DeepSeekOptions> LoadAsync(CancellationToken ct = default)`
    - `Task SaveAsync(DeepSeekOptions options, CancellationToken ct = default)`

- [ ] **Step 1: 写失败测试**

`tests/Seeing.Provider.DeepSeek.Tests/DeepSeekConfigStoreTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
            store.ConfigFilePath.Should().Be(Path.Combine(dir, "deepseek.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            await store.SaveAsync(new DeepSeekOptions { ApiKey = "sk-test" }, TestContext.Current.CancellationToken);
            File.Exists(store.ConfigFilePath).Should().BeTrue();
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-test");
            Directory.GetFiles(dir).Should().ContainSingle(f =>
                f.EndsWith("deepseek.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsEmptyApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "deepseek.json");
            await File.WriteAllTextAsync(path, "{ not-json", TestContext.Current.CancellationToken);
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekConfigStoreTests"`

Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现 Options + Store**

`DeepSeekOptions.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekOptions
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
```

`DeepSeekConfigStore.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<DeepSeekConfigStore> _logger;

    public DeepSeekConfigStore(IWorkspaceProvider? workspace, ILogger<DeepSeekConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    public DeepSeekConfigStore(string userSeeingDirectory, ILogger<DeepSeekConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ConfigFilePath => Path.Combine(_directory, "deepseek.json");

    public async Task<DeepSeekOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new DeepSeekOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<DeepSeekOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new DeepSeekOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 DeepSeek 配置失败，视为空配置: {Path}", path);
            return new DeepSeekOptions();
        }
    }

    public async Task SaveAsync(DeepSeekOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(_directory);
        var path = ConfigFilePath;
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekConfigStoreTests"`

Expected: PASS

- [ ] **Step 5: Skip commit**

---

### Task 3: DeepSeekModelsClient（TDD）

**Files:**
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekModelsClient.cs`
- Create: `tests/Seeing.Provider.DeepSeek.Tests/DeepSeekModelsClientTests.cs`

**Interfaces:**
- Consumes: `HttpMessageHandler`（可注入）
- Produces:
  - `class DeepSeekModelsClient`
    - `Task<IReadOnlyList<ModelConfig>> ListModelsAsync(string apiKey, CancellationToken ct = default)`
  - `public const string DefaultBaseUrl = "https://api.deepseek.com/v1";`

- [ ] **Step 1: 写失败测试（HttpMessageHandler stub）**

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekModelsClientTests
{
    [Fact]
    public async Task ListModelsAsync_ParsesOpenAiStylePayload()
    {
        var handler = new StubHandler(_ =>
        {
            var json = """{"data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var client = new DeepSeekModelsClient(handler, NullLogger<DeepSeekModelsClient>.Instance);

        var models = await client.ListModelsAsync("sk-test", TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        models[0].Id.Should().Be("deepseek-chat");
        models[0].Name.Should().Be("deepseek-chat");
        models[0].Provider.Should().Be("deepseek");
        models[1].Id.Should().Be("deepseek-reasoner");
    }

    [Fact]
    public async Task ListModelsAsync_HttpError_ReturnsEmpty()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new DeepSeekModelsClient(handler, NullLogger<DeepSeekModelsClient>.Instance);

        var models = await client.ListModelsAsync("bad", TestContext.Current.CancellationToken);
        models.Should().BeEmpty();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekModelsClientTests"`

Expected: FAIL

- [ ] **Step 3: 实现 ModelsClient**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekModelsClient
{
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DeepSeekModelsClient(ILogger<DeepSeekModelsClient> logger)
        : this(new HttpClientHandler(), logger)
    {
    }

    public DeepSeekModelsClient(HttpMessageHandler handler, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(DefaultBaseUrl.TrimEnd('/') + "/")
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<ModelConfig>> ListModelsAsync(
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Array.Empty<ModelConfig>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DeepSeek List Models 失败: {StatusCode}", (int)response.StatusCode);
                return Array.Empty<ModelConfig>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ListModelsResponse>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            if (payload?.Data is null || payload.Data.Count == 0)
                return Array.Empty<ModelConfig>();

            return payload.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ModelConfig
                {
                    Id = m.Id!,
                    Name = m.Id,
                    Provider = "deepseek"
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeepSeek List Models 异常");
            return Array.Empty<ModelConfig>();
        }
    }

    private sealed class ListModelsResponse
    {
        [JsonPropertyName("data")]
        public List<ModelInfo>? Data { get; set; }
    }

    private sealed class ModelInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekModelsClientTests"`

Expected: PASS

- [ ] **Step 5: Skip commit**

---

### Task 4: DeepSeekProvider（TDD）

**Files:**
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekProvider.cs`
- Create: `tests/Seeing.Provider.DeepSeek.Tests/DeepSeekProviderTests.cs`

**Interfaces:**
- Consumes: `DeepSeekConfigStore`、`ILlmClientFactory`、`IProviderRegistry`、`DeepSeekModelsClient`、`ILogger<DeepSeekProvider>`
- Produces:
  - `sealed class DeepSeekProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable`
  - `Id => "deepseek"`；`Name => "DeepSeek"`
  - `const string ExtensionId = "seeing.provider.deepseek"`
  - `Task WarmupAsync(CancellationToken ct = default)` — 从 store 加载 ApiKey 到内存
  - 模型 TTL：`TimeSpan.FromMinutes(5)`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.ConfigSchema;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekProviderTests
{
    [Fact]
    public void GetConfigSchema_ExposesApiKeySecret()
    {
        var sut = CreateSut(apiKey: null);
        var schema = sut.GetConfigSchema();
        schema.Should().NotBeNull();
        schema!.Should().ContainSingle(f =>
            f.Name == "ApiKey" && f.Type == ConfigFieldType.Secret && f.Required);
    }

    [Fact]
    public async Task GetModelsAsync_WithoutApiKey_ReturnsEmpty()
    {
        var sut = CreateSut(apiKey: null);
        await sut.WarmupAsync(TestContext.Current.CancellationToken);
        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveConfigAsync_PersistsAndRebuildsClient()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            var createdKeys = new List<string?>();
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
                .Returns((ProviderConfig cfg) =>
                {
                    createdKeys.Add(cfg.ApiKey);
                    cfg.BaseUrl.Should().Be(DeepSeekModelsClient.DefaultBaseUrl);
                    cfg.Type.Should().Be(ProviderType.OpenAI);
                    cfg.Id.Should().Be("deepseek");
                    return Mock.Of<ILlmClient>();
                });
            var registry = new Mock<IProviderRegistry>();
            var sut = new DeepSeekProvider(
                store,
                factory.Object,
                registry.Object,
                new DeepSeekModelsClient(NullLogger<DeepSeekModelsClient>.Instance),
                NullLogger<DeepSeekProvider>.Instance);

            await sut.SaveConfigAsync(
                new Dictionary<string, object?> { ["ApiKey"] = "sk-new" },
                ConfigLevel.Project,
                TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-new");
            _ = sut.GetClient();
            createdKeys.Should().Contain("sk-new");
            registry.Verify(r => r.Register(sut, DeepSeekProvider.ExtensionId), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeepSeekProvider CreateSut(string? apiKey)
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
        if (!string.IsNullOrEmpty(apiKey))
            store.SaveAsync(new DeepSeekOptions { ApiKey = apiKey }).GetAwaiter().GetResult();

        return new DeepSeekProvider(
            store,
            Mock.Of<ILlmClientFactory>(),
            Mock.Of<IProviderRegistry>(),
            new DeepSeekModelsClient(NullLogger<DeepSeekModelsClient>.Instance),
            NullLogger<DeepSeekProvider>.Instance);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekProviderTests"`

Expected: FAIL

- [ ] **Step 3: 实现 DeepSeekProvider**

实现文件须包含：

1. `WarmupAsync`：`_apiKey = (await _store.LoadAsync(ct)).ApiKey;`
2. `GetClient()`：在锁内懒创建；`ProviderConfig` 使用 `Type=OpenAI`、`BaseUrl=DeepSeekModelsClient.DefaultBaseUrl`、`Id=deepseek`、`ApiKey=_apiKey`
3. `InvalidateClient()`：释放旧 client（`IAsyncDisposable`/`IDisposable`）并置 null
4. `GetModelsAsync`：无 key → 空；TTL 5 分钟缓存；否则 `_modelsClient.ListModelsAsync`
5. `GetConfigSchema`：`OptionsSchemaBuilder.FromType(typeof(DeepSeekOptions))`
6. `LoadConfigAsync` / `SaveConfigAsync`：读写 `ApiKey`；Save **忽略** `ConfigLevel`；Save 后清缓存、InvalidateClient、`_registry.Register(this, ExtensionId)`
7. `TestConnectionAsync`：无 key → false；否则委托 client
8. `DisposeAsync`：释放 client

参考骨架：

```csharp
public sealed class DeepSeekProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    public const string ExtensionId = "seeing.provider.deepseek";
    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly DeepSeekConfigStore _store;
    private readonly ILlmClientFactory _factory;
    private readonly IProviderRegistry _registry;
    private readonly DeepSeekModelsClient _modelsClient;
    private readonly ILogger<DeepSeekProvider> _logger;
    private readonly object _gate = new();
    private string? _apiKey;
    private ILlmClient? _client;
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;
    private int _disposed;

    public override string Id => "deepseek";
    public override string? Name => "DeepSeek";

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var options = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate) { _apiKey = options.ApiKey; }
    }

    // ... GetClient / GetModelsAsync / schema / Save / Dispose 按上文要点补全
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekProviderTests"`

Expected: PASS

- [ ] **Step 5: Skip commit**

---

### Task 5: DI + HostedService + 可选 Extension

**Files:**
- Create: `plugs/providers/Seeing.Provider.DeepSeek/ServiceCollectionExtensions.cs`
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekProviderHostedService.cs`
- Create: `plugs/providers/Seeing.Provider.DeepSeek/DeepSeekExtension.cs`
- Create: `tests/Seeing.Provider.DeepSeek.Tests/DeepSeekServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 2–4 类型；`WarmupAsync`
- Produces:
  - `AddDeepSeekProvider(this IServiceCollection services)`
  - `DeepSeekProviderHostedService : IHostedService`
  - `DeepSeekExtension : IExtension`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddDeepSeekProvider_RegistersIntoProviderRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ProviderRegistry();
        services.AddSingleton<IProviderRegistry>(registry);
        services.AddSingleton(Mock.Of<ILlmClientFactory>());
        services.AddDeepSeekProvider();

        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().OfType<DeepSeekProviderHostedService>().Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        registry.GetProvider("deepseek").Should().BeOfType<DeepSeekProvider>();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekServiceCollectionExtensionsTests"`

Expected: FAIL

- [ ] **Step 3: 实现 DI / HostedService / Extension**

`ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.DeepSeek;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeepSeekProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            new DeepSeekConfigStore(
                sp.GetService<IWorkspaceProvider>(),
                sp.GetRequiredService<ILogger<DeepSeekConfigStore>>()));
        services.TryAddSingleton<DeepSeekModelsClient>();
        services.TryAddSingleton<DeepSeekProvider>();
        services.AddHostedService<DeepSeekProviderHostedService>();
        return services;
    }
}
```

`DeepSeekProviderHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekProviderHostedService : IHostedService
{
    private readonly DeepSeekProvider _provider;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<DeepSeekProviderHostedService> _logger;

    public DeepSeekProviderHostedService(
        DeepSeekProvider provider,
        IProviderRegistry registry,
        ILogger<DeepSeekProviderHostedService> logger)
    {
        _provider = provider;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _provider.WarmupAsync(cancellationToken).ConfigureAwait(false);
        _registry.Register(_provider, DeepSeekProvider.ExtensionId);
        _logger.LogInformation("已注册 DeepSeek Provider");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.Unregister(_provider.Id);
        return Task.CompletedTask;
    }
}
```

`DeepSeekExtension.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Llm;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekExtension : IExtension
{
    public string? Id => DeepSeekProvider.ExtensionId;
    public string Version => "1.0.0";
    public string Name => "DeepSeek Provider";
    public string Description => "DeepSeek OpenAI-compatible LLM provider";
    public string Target => "server";

    private DeepSeekProvider? _provider;

    public Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        _provider = context.Services.GetService<DeepSeekProvider>();
        return Task.CompletedTask;
    }

    public IEnumerable<ILlmProvider> GetProviders()
    {
        if (_provider is not null)
            yield return _provider;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests --filter "FullyQualifiedName~DeepSeekServiceCollectionExtensionsTests"`

Expected: PASS

- [ ] **Step 5: Skip commit**

---

### Task 6: WebUI 默认启用

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`

**Interfaces:**
- Consumes: `AddDeepSeekProvider()`
- Produces: WebUI 启动后注册表含 `deepseek`

- [ ] **Step 1: 添加 ProjectReference**

在 `Seeing.Agent.WebUI.csproj` 增加：

```xml
    <ProjectReference Include="../../plugs/providers/Seeing.Provider.DeepSeek/Seeing.Provider.DeepSeek.csproj" />
```

- [ ] **Step 2: Program.cs 注册**

```csharp
using Seeing.Provider.DeepSeek;
```

在 `builder.Services.AddMemoryServices();` 附近：

```csharp
builder.Services.AddDeepSeekProvider();
```

- [ ] **Step 3: 编译 WebUI**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`

Expected: 成功

- [ ] **Step 4: Skip commit**

---

### Task 7: 全量测试与验收核对

**Files:** 无新文件（Models 页已有 schema 轨；仅联调失败时做最小修补）

- [ ] **Step 1: 跑 DeepSeek 测试**

Run: `dotnet test tests/Seeing.Provider.DeepSeek.Tests`

Expected: 全部 PASS

- [ ] **Step 2: 确认核心无 DeepSeek 改动**

Run:

```bash
git diff --name-only -- src/Seeing.Agent
```

Expected: 本计划不引入 `src/Seeing.Agent` 变更

- [ ] **Step 3: 手工验收清单（WebUI）**

1. `dotnet run --project samples/Seeing.Agent.WebUI`
2. Models 页出现 **deepseek**
3. 保存 ApiKey → 仅 `%USERPROFILE%\.seeing\deepseek.json` 出现/更新
4. 模型列表可刷新
5. 选 deepseek 模型可对话（需真实 Key）

- [ ] **Step 4: Skip commit**

---

## Spec coverage checklist（自检）

| Spec 要求 | Task |
|-----------|------|
| `plugs/providers` 项目 + slnx 文件夹 | Task 1 |
| 自管 `deepseek.json` / 忽略 ConfigLevel | Task 2, 4 |
| 动态 `/v1/models` + TTL | Task 3, 4 |
| `ILlmClientFactory` OpenAI 包装 / 固定 BaseUrl | Task 4 |
| `IConfigurableLlmProvider` schema | Task 4 |
| Save 后 Register 触发刷新 | Task 4 |
| `AddDeepSeekProvider` + HostedService | Task 5 |
| 可选 IExtension | Task 5 |
| WebUI 默认启用 | Task 6 |
| 零 Seeing.Agent DeepSeek 改动 | Task 7 |
| 测试项目 | Task 2–5, 7 |

## Placeholder / 一致性自检

- 无 TBD；BaseUrl / ExtensionId / 文件路径与 spec 一致。
- `WarmupAsync` 在 Task 4 定义、Task 5 消费。
