# SystemOne 能力模块实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `Seeing.Agent.SystemOne` 能力包与测试项目，提供 System One（TypeSafe/Jev）typed-question 客户端契约、TypeSafe 实现、工厂/Provider/Registry/Service、`systemone.json` + `SYSTEMONE_*` 环境变量配置与模块装配。

**Architecture:** 契约层（`Seeing.Agent.Abstractions.SystemOne`，零实现：DTO/接口/常量/异常/解析器）→ 客户端层（自建 `HttpClient` + `SystemOneHttpHelper` 退避重试 + `TypeSafeSystemOneClient`/工厂）→ 运行时层（配置解析、`SystemOneProviderManager` 注册表与热重载、`SystemOneService` 门面、`SystemOneModule` + `AddSystemOne`）。能力包仅依赖 Abstractions（+ DI/Logging 抽象），零 Core 引用；模块 Id `systemone`，provider type `typesafe`。

**Spec:** `docs/superpowers/specs/2026-09-23-systemone-module-design.md`（权威；实现与该规格一致）

**Tech Stack:** .NET 10.0 / C#、`System.Net.Http`、`System.Text.Json`、xunit.v3 4.0.0、FluentAssertions 8.10.0、Moq 4.20.72、中央包管理（CPM）、VSTest 运行器（**禁用 `dotnet test`**：本仓库 MTP 零测试）。

---

## 文件结构总览

| 路径 | 责任 | 任务 |
|------|------|------|
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneModels.cs` | DTO：Request/Question/Response/Answer/Usage/Model/ModelsResponse + question 类型常量 | Task 2 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneProviderConfig.cs` | `SystemOneProviderTypes` / `SystemOneProviderConfig` / `SystemOneDefaults` | Task 2 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneException.cs` | 领域异常 | Task 2 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneClient.cs` | `ISystemOneClient` + `ISystemOneClientFactory` | Task 3 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneProvider.cs` | `ISystemOneProvider` + `ISystemOneProviderRegistry` | Task 3 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneService.cs` | 业务门面接口 | Task 3 |
| `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneClientFactoryResolver.cs` | 按 type 解析工厂（`Find`/`Require`） | Task 3 |
| `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpClientFactory.cs` | 创建带连接池/超时的 `HttpClient` | Task 4 |
| `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpHelper.cs` | POST/GET + 鉴权 + JSON + 429/529 退避重试 | Task 5 |
| `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClient.cs` | TypeSafe 协议实现（模型补全 / 连通性） | Task 6 |
| `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClientFactory.cs` | 按 type 路由创建客户端 | Task 6 |
| `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneEnvironmentConfig.cs` | `SYSTEMONE_*` 环境变量解析 | Task 7 |
| `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneConfigStore.cs` | `systemone.json` 配置节存取 | Task 8 |
| `src/capabilities/Seeing.Agent.SystemOne/PredefinedSystemOneProviders.cs` | 内置 typesafe 默认配置工厂 | Task 8 |
| `src/capabilities/Seeing.Agent.SystemOne/Runtime/ConfiguredSystemOneProvider.cs` | 配置驱动 provider（延迟客户端 + Dispose） | Task 9 |
| `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneProviderManager.cs` | 注册表 / 默认 provider / 热重载 | Task 10 |
| `src/capabilities/Seeing.Agent.SystemOne/Runtime/SystemOneService.cs` | 门面 + 默认 provider 选择 | Task 11 |
| `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneReloadHandler.cs` | 配置重载处理器 | Task 12 |
| `src/capabilities/Seeing.Agent.SystemOne/SystemOneModule.cs` | `ISeeingModule`（Id=systemone） | Task 12 |
| `src/capabilities/Seeing.Agent.SystemOne/Extensions/SystemOneServiceExtensions.cs` | `AddSystemOne(registry)` 入口 | Task 12 |
| `tests/capabilities/Seeing.Agent.SystemOne.Tests/**` | 单元测试 | Task 1-12 |
| `Seeing.Agent.slnx` | 解决方案登记 | Task 1 |

**统一运行方式（每步测试统一用此，禁用 `dotnet test`）：**
```bash
dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests
dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
```

---

## 任务组 A：脚手架 + 契约层（Tasks 1-3）

> 依赖：无。交付：可构建的空能力包/测试项目 + 契约层全部类型。

### Task 1: 脚手架（能力包 + 测试项目 + slnx 登记）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Seeing.Agent.SystemOne.csproj`
- Create: `src/capabilities/Seeing.Agent.SystemOne/SystemOnePackageMarker.cs`
- Create: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj`
- Create: `tests/capabilities/Seeing.Agent.SystemOne.Tests/ScaffoldSmokeTests.cs`
- Modify: `Seeing.Agent.slnx`

- [ ] **Step 1: 创建能力包 csproj**

`src/capabilities/Seeing.Agent.SystemOne/Seeing.Agent.SystemOne.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.SystemOne</RootNamespace>
    <Description>System One（TypeSafe/Jev）typed-question 客户端与工厂模块</Description>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.SystemOne.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建占位源文件**

`src/capabilities/Seeing.Agent.SystemOne/SystemOnePackageMarker.cs`：

```csharp
namespace Seeing.Agent.SystemOne;

/// <summary>
/// 能力包占位标记：保证工程有可编译源文件，供脚手架阶段验证构建。
/// 任务组 C 引入真实源文件（<c>SystemOneModule</c> 等）后由 Task 12 删除本文件。
/// </summary>
internal static class SystemOnePackageMarker
{
}
```

- [ ] **Step 3: 创建测试项目 csproj**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.SystemOne.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Moq" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\src\capabilities\Seeing.Agent.SystemOne\Seeing.Agent.SystemOne.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: 创建脚手架冒烟测试**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/ScaffoldSmokeTests.cs`：

```csharp
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void CapabilityAssembly_IsReferenced()
    {
        var assembly = Assembly.Load("Seeing.Agent.SystemOne");

        assembly.GetName().Name.Should().Be("Seeing.Agent.SystemOne");
    }
}
```

- [ ] **Step 5: 登记到 `Seeing.Agent.slnx`**

在 `/src/capabilities/` 文件夹内，将 `Seeing.Agent.SystemOne` 插入到 `Seeing.Agent.Skills` 之后、`Seeing.Agent.TokenBudget` 之前：

```xml
    <Project Path="src/capabilities/Seeing.Agent.Skills/Seeing.Agent.Skills.csproj" />
    <Project Path="src/capabilities/Seeing.Agent.SystemOne/Seeing.Agent.SystemOne.csproj" />
    <Project Path="src/capabilities/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj" />
```

在 `/tests/capabilities/` 文件夹内，将测试包插入到 `Seeing.Agent.Skills.Tests` 之后、`Seeing.Agent.Tools.Basic.Tests` 之前：

```xml
    <Project Path="tests/capabilities/Seeing.Agent.Skills.Tests/Seeing.Agent.Skills.Tests.csproj" />
    <Project Path="tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj" />
    <Project Path="tests/capabilities/Seeing.Agent.Tools.Basic.Tests/Seeing.Agent.Tools.Basic.Tests.csproj" />
```

- [ ] **Step 6: 构建测试项目（含能力包）**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded`，0 Error。

- [ ] **Step 7: 构建整个解决方案验证登记正确**

Run: `dotnet build Seeing.Agent.slnx`
Expected: `Build succeeded`，0 Error；输出包含 `Seeing.Agent.SystemOne.csproj` 与 `Seeing.Agent.SystemOne.Tests.csproj`。

- [ ] **Step 8: 运行冒烟测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~ScaffoldSmokeTests"`
Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0`。

- [ ] **Step 9: 提交**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Seeing.Agent.SystemOne.csproj src/capabilities/Seeing.Agent.SystemOne/SystemOnePackageMarker.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj tests/capabilities/Seeing.Agent.SystemOne.Tests/ScaffoldSmokeTests.cs Seeing.Agent.slnx
git commit -m "chore(systemone): 新增 SystemOne 能力包与测试项目脚手架"
```

---

### Task 2: 契约层 DTO 与常量（TDD）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneProviderConfig.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneModels.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneException.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneSerializationTests.cs`

- [ ] **Step 1: 编写失败测试**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneSerializationTests.cs`：

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneSerializationTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Serialize_StateAsString_WritesRawString()
    {
        var request = new SystemOneRequest { State = "hello", Model = "jev-latest" };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
        doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
    }

    [Fact]
    public void Serialize_StateAsObject_WritesObject()
    {
        var request = new SystemOneRequest { State = new { text = "hi", level = 3 } };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("state");
        state.GetProperty("text").GetString().Should().Be("hi");
        state.GetProperty("level").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Serialize_StateAsArray_WritesArray()
    {
        var request = new SystemOneRequest { State = new object[] { "a", 1 } };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("state");
        state.ValueKind.Should().Be(JsonValueKind.Array);
        state.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_ThreeQuestionTypes_PreserveTypeAndCriteria()
    {
        var request = new SystemOneRequest
        {
            Model = "jev-latest",
            State = "x",
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["isHappy"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Noul,
                    Instructions = "Is the user happy?"
                },
                ["mood"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Choice,
                    Criteria = new Dictionary<string, string> { ["a"] = "Happy", ["b"] = "Sad" }
                },
                ["rating"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Score,
                    Criteria = new object[] { "low", "high" }
                }
            }
        };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var questions = doc.RootElement.GetProperty("questions");
        questions.GetProperty("isHappy").GetProperty("type").GetString().Should().Be("noul");
        questions.GetProperty("isHappy").GetProperty("instructions").GetString().Should().Be("Is the user happy?");
        questions.GetProperty("mood").GetProperty("type").GetString().Should().Be("choice");
        questions.GetProperty("mood").GetProperty("criteria").GetProperty("a").GetString().Should().Be("Happy");
        questions.GetProperty("rating").GetProperty("type").GetString().Should().Be("score");
        questions.GetProperty("rating").GetProperty("criteria").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Deserialize_NoulAnswer_ReadsProbabilityConfidenceAndUsage()
    {
        const string json = """
        {
          "model": "jev-latest",
          "answers": {
            "isHappy": { "type": "noul", "noul": 0.83, "confidence": 0.91 }
          },
          "usage": { "input_tokens": 12, "output_tokens": 3 }
        }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        response.Should().NotBeNull();
        response!.Model.Should().Be("jev-latest");
        var answer = response.Answers["isHappy"];
        answer.Type.Should().Be("noul");
        answer.Noul.Should().BeApproximately(0.83, 1e-9);
        answer.Confidence.Should().BeApproximately(0.91, 1e-9);
        response.Usage!.InputTokens.Should().Be(12);
        response.Usage.OutputTokens.Should().Be(3);
    }

    [Fact]
    public void Deserialize_ChoiceAnswer_ReadsChoiceLegendAndProbabilities()
    {
        const string json = """
        {
          "answers": {
            "mood": {
              "type": "choice",
              "choice": "a",
              "legend": { "a": "Happy", "b": "Sad" },
              "probabilities": { "a": 0.7, "b": 0.3 }
            }
          }
        }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        var answer = response!.Answers["mood"];
        answer.Type.Should().Be("choice");
        answer.Choice.Should().Be("a");
        answer.Legend!["a"].Should().Be("Happy");
        answer.Probabilities!["a"].Should().BeApproximately(0.7, 1e-9);
        answer.Probabilities["b"].Should().BeApproximately(0.3, 1e-9);
    }

    [Fact]
    public void Deserialize_ScoreAnswer_ReadsScore()
    {
        const string json = """
        { "answers": { "rating": { "type": "score", "score": 4.2 } } }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        var answer = response!.Answers["rating"];
        answer.Type.Should().Be("score");
        answer.Score.Should().BeApproximately(4.2, 1e-9);
    }

    [Fact]
    public void Deserialize_ProviderConfig_MapsJsonPropertyNames()
    {
        const string json = """
        {
          "id": "typesafe",
          "type": "typesafe",
          "name": "TypeSafe Jev",
          "baseURL": "https://example.test",
          "apiKey": "ts-123",
          "model": "jev-latest",
          "timeout": 15000,
          "max_retries": 3,
          "retry_base_delay": 250,
          "retry_max_delay": 4000
        }
        """;

        var config = JsonSerializer.Deserialize<SystemOneProviderConfig>(json, Opts);

        config.Should().NotBeNull();
        config!.Id.Should().Be("typesafe");
        config.Type.Should().Be("typesafe");
        config.Name.Should().Be("TypeSafe Jev");
        config.BaseUrl.Should().Be("https://example.test");
        config.ApiKey.Should().Be("ts-123");
        config.Model.Should().Be("jev-latest");
        config.Timeout.Should().Be(15000);
        config.MaxRetries.Should().Be(3);
        config.RetryBaseDelayMs.Should().Be(250);
        config.RetryMaxDelayMs.Should().Be(4000);
    }

    [Fact]
    public void ProviderConfig_Defaults_MatchContract()
    {
        var config = new SystemOneProviderConfig();

        config.Id.Should().Be("typesafe");
        config.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        config.Model.Should().Be("jev-latest");
        config.Timeout.Should().Be(10000);
        config.MaxRetries.Should().Be(2);
        config.RetryBaseDelayMs.Should().Be(500);
        config.RetryMaxDelayMs.Should().Be(5000);
    }

    [Theory]
    [InlineData("TypeSafe", "typesafe")]
    [InlineData("  TYPESAFE ", "typesafe")]
    [InlineData("other", "other")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ReturnsCanonicalLowercase(string? input, string expected)
    {
        SystemOneProviderTypes.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Defaults_MatchContract()
    {
        SystemOneDefaults.DefaultBaseUrl.Should().Be("https://api.typesafe.ai");
        SystemOneDefaults.DefaultModel.Should().Be("jev-latest");
        SystemOneDefaults.EvaluatePath.Should().Be("/v1/systemone");
        SystemOneDefaults.ModelsPath.Should().Be("/v1/models");
        SystemOneDefaults.DefaultTimeoutMs.Should().Be(10000);
    }

    [Fact]
    public void Deserialize_ModelsResponse_ReadsModels()
    {
        const string json = """
        { "models": [ { "name": "jev-latest", "description": "default", "release_date": "2026-01-01" } ] }
        """;

        var response = JsonSerializer.Deserialize<SystemOneModelsResponse>(json, Opts);

        response!.Models.Should().HaveCount(1);
        response.Models[0].Name.Should().Be("jev-latest");
        response.Models[0].Description.Should().Be("default");
        response.Models[0].ReleaseDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void Exception_PreservesStatusCodeBodyMessageAndInner()
    {
        var inner = new IOException("boom");

        var exception = new SystemOneException(429, "{\"error\":\"rate\"}", "限流", inner);

        exception.StatusCode.Should().Be(429);
        exception.ResponseBody.Should().Be("{\"error\":\"rate\"}");
        exception.Message.Should().Be("限流");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
```

- [ ] **Step 2: 运行构建，确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build FAILED`，`CS0246`（找不到 `SystemOneRequest` / `SystemOneProviderConfig` / `SystemOneDefaults` 等）。

- [ ] **Step 3: 实现 `SystemOneProviderConfig.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneProviderConfig.cs`：

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne provider 类型标识常量（小写，用作路由键）</summary>
public static class SystemOneProviderTypes
{
    public const string TypeSafe = "typesafe";

    /// <summary>归一为规范小写；未知类型原样 Trim</summary>
    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        var trimmed = type.Trim();
        if (string.Equals(trimmed, TypeSafe, StringComparison.OrdinalIgnoreCase))
            return TypeSafe;
        return trimmed;
    }
}

/// <summary>SystemOne provider 连接配置（systemone.json 的值）</summary>
public class SystemOneProviderConfig
{
    [JsonPropertyName("id")]           public string Id { get; set; } = "typesafe";
    [JsonPropertyName("type")]         public string Type { get; set; } = SystemOneProviderTypes.TypeSafe;
    [JsonPropertyName("name")]         public string? Name { get; set; }
    [JsonPropertyName("baseURL")]      public string? BaseUrl { get; set; }   // 默认 https://api.typesafe.ai
    [JsonPropertyName("apiKey")]       public string? ApiKey { get; set; }    // 环境变量优先覆盖
    [JsonPropertyName("model")]        public string Model { get; set; } = "jev-latest";
    [JsonPropertyName("timeout")]      public int Timeout { get; set; } = 10000;      // 毫秒，默认 10s
    [JsonPropertyName("max_retries")]  public int MaxRetries { get; set; } = 2;
    [JsonPropertyName("retry_base_delay")] public int RetryBaseDelayMs { get; set; } = 500;
    [JsonPropertyName("retry_max_delay")]  public int RetryMaxDelayMs { get; set; } = 5000;
    [JsonPropertyName("headers")]      public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>SystemOne 常量</summary>
public static class SystemOneDefaults
{
    public const string DefaultBaseUrl = "https://api.typesafe.ai";
    public const string DefaultModel = "jev-latest";
    public const string EvaluatePath = "/v1/systemone";
    public const string ModelsPath = "/v1/models";
    public const int DefaultTimeoutMs = 10000;
}
```

- [ ] **Step 4: 实现 `SystemOneModels.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneModels.cs`：

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>question 类型常量</summary>
public static class SystemOneQuestionTypes
{
    public const string Noul = "noul";
    public const string Choice = "choice";
    public const string Score = "score";
}

/// <summary>评估请求：state + model + questions</summary>
public sealed record SystemOneRequest
{
    /// <summary>string | object | array（运行期类型序列化）</summary>
    [JsonPropertyName("state")]     public object? State { get; init; }
    /// <summary>留空时由客户端用 provider 配置的 Model 补全</summary>
    [JsonPropertyName("model")]     public string Model { get; init; } = string.Empty;
    [JsonPropertyName("questions")] public Dictionary<string, SystemOneQuestion> Questions { get; init; } = new();
}

/// <summary>单个 typed question</summary>
public sealed record SystemOneQuestion
{
    [JsonPropertyName("type")]         public string Type { get; init; } = string.Empty;
    [JsonPropertyName("instructions")] public object? Instructions { get; init; }
    [JsonPropertyName("criteria")]     public object? Criteria { get; init; }
}

/// <summary>评估响应</summary>
public sealed record SystemOneResponse
{
    [JsonPropertyName("model")]   public string? Model { get; init; }
    [JsonPropertyName("answers")] public Dictionary<string, SystemOneAnswer> Answers { get; init; } = new();
    [JsonPropertyName("usage")]   public SystemOneUsage? Usage { get; init; }
}

/// <summary>单个应答（按 type 取对应字段）</summary>
public sealed record SystemOneAnswer
{
    [JsonPropertyName("type")]          public string? Type { get; init; }
    [JsonPropertyName("noul")]          public double? Noul { get; init; }
    [JsonPropertyName("choice")]        public string? Choice { get; init; }
    [JsonPropertyName("score")]         public double? Score { get; init; }
    [JsonPropertyName("legend")]        public Dictionary<string, string>? Legend { get; init; }
    [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; init; }
    [JsonPropertyName("confidence")]    public double? Confidence { get; init; }
}

/// <summary>token 用量</summary>
public sealed record SystemOneUsage
{
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
}

/// <summary>模型条目</summary>
public sealed record SystemOneModel
{
    [JsonPropertyName("name")]         public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")]  public string? Description { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
}

/// <summary>GET /v1/models 响应包装</summary>
public sealed record SystemOneModelsResponse
{
    [JsonPropertyName("models")] public List<SystemOneModel> Models { get; init; } = new();
}
```

- [ ] **Step 5: 实现 `SystemOneException.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneException.cs`：

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne API 调用失败（最终重试耗尽后抛出）</summary>
public sealed class SystemOneException : Exception
{
    /// <summary>HTTP 状态码；纯网络/超时失败（无响应）时为 0</summary>
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public SystemOneException(int statusCode, string? responseBody, string message, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; ResponseBody = responseBody; }
}
```

- [ ] **Step 6: 构建，确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded`，0 Warning / 0 Error。

- [ ] **Step 7: 运行序列化测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneSerializationTests"`
Expected: `Passed!  - Failed: 0`，全部用例通过。

- [ ] **Step 8: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneProviderConfig.cs src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneModels.cs src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneException.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneSerializationTests.cs
git commit -m "feat(systemone): 新增 SystemOne 契约 DTO/配置/异常与序列化测试"
```

---

### Task 3: 契约层接口与工厂解析器（TDD）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneClient.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneProvider.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneService.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneClientFactoryResolver.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneClientFactoryResolverTests.cs`

- [ ] **Step 1: 编写失败测试**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneClientFactoryResolverTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneClientFactoryResolverTests
{
    private sealed class FakeClient : ISystemOneClient
    {
        public string ProviderId => "fake";
        public string ProviderType => "fake";

        public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeFactory : ISystemOneClientFactory
    {
        private readonly ISystemOneClient _client;

        public FakeFactory(params string[] types)
            : this(new FakeClient(), types)
        {
        }

        public FakeFactory(ISystemOneClient client, params string[] types)
        {
            _client = client;
            SupportedTypes = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlySet<string> SupportedTypes { get; }

        public bool SupportsType(string type) => SupportedTypes.Contains(type);

        public ISystemOneClient Create(SystemOneProviderConfig config) => _client;
    }

    [Fact]
    public void Find_ReturnsFirstMatchingFactory()
    {
        var first = new FakeFactory("typesafe");
        var second = new FakeFactory("typesafe");
        var factories = new ISystemOneClientFactory[] { first, second };

        var result = SystemOneClientFactoryResolver.Find(factories, "typesafe");

        result.Should().BeSameAs(first);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNoFactorySupportsType()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        SystemOneClientFactoryResolver.Find(factories, "other").Should().BeNull();
    }

    [Fact]
    public void Find_ReturnsNull_WhenTypeIsWhitespace()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        SystemOneClientFactoryResolver.Find(factories, "   ").Should().BeNull();
    }

    [Fact]
    public void Require_ReturnsMatchingFactory_CaseInsensitive()
    {
        var factory = new FakeFactory("typesafe");
        var factories = new ISystemOneClientFactory[] { factory };

        var result = SystemOneClientFactoryResolver.Require(factories, "TypeSafe");

        result.Should().BeSameAs(factory);
    }

    [Fact]
    public void Require_ThrowsNotSupportedException_ListingSupportedTypes()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        var act = () => SystemOneClientFactoryResolver.Require(factories, "unknown");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*unknown*")
            .WithMessage("*已支持*typesafe*");
    }

    [Fact]
    public void Require_ThrowsArgumentException_WhenTypeIsWhitespace()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        var act = () => SystemOneClientFactoryResolver.Require(factories, "   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Require_ThrowsArgumentNullException_WhenFactoriesNull()
    {
        var act = () => SystemOneClientFactoryResolver.Require(null!, "typesafe");

        act.Should().Throw<ArgumentNullException>();
    }
}
```

- [ ] **Step 2: 运行构建，确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build FAILED`，`CS0246`（找不到 `ISystemOneClient` / `ISystemOneClientFactory` / `SystemOneClientFactoryResolver`）。

- [ ] **Step 3: 实现 `ISystemOneClient.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneClient.cs`：

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne 协议客户端</summary>
public interface ISystemOneClient
{
    string ProviderId { get; }
    string ProviderType { get; }

    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>协议工厂：按 provider 类型创建客户端</summary>
public interface ISystemOneClientFactory
{
    IReadOnlySet<string> SupportedTypes { get; }
    bool SupportsType(string type);
    ISystemOneClient Create(SystemOneProviderConfig config);
}
```

- [ ] **Step 4: 实现 `ISystemOneProvider.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneProvider.cs`：

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>配置驱动的 provider（连接实例）</summary>
public interface ISystemOneProvider
{
    string Id { get; }
    string? Name { get; }
    string Type { get; }
    int MaxRetries { get; }

    ISystemOneClient GetClient();
    Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>provider 集合管理（注册/注销/变更通知）</summary>
public interface ISystemOneProviderRegistry
{
    IReadOnlyList<ISystemOneProvider> Providers { get; }
    ISystemOneProvider? DefaultProvider { get; }

    void Register(ISystemOneProvider provider, string? ownerExtensionId = null);
    bool Unregister(string providerId);
    int UnregisterByOwner(string ownerExtensionId);

    event EventHandler? ProvidersChanged;
}
```

- [ ] **Step 5: 实现 `ISystemOneService.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneService.cs`：

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>业务门面：按默认 provider 转发 SystemOne 调用</summary>
public interface ISystemOneService
{
    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(string? providerId = null, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 6: 实现 `SystemOneClientFactoryResolver.cs`**

`src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneClientFactoryResolver.cs`：

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>
/// 从已登记的 <see cref="ISystemOneClientFactory"/> 集合中按类型解析工厂。
/// </summary>
public static class SystemOneClientFactoryResolver
{
    /// <summary>
    /// 解析支持指定类型的首个工厂（同类型多工厂时 first wins）；未找到返回 <c>null</c>。
    /// </summary>
    public static ISystemOneClientFactory? Find(
        IEnumerable<ISystemOneClientFactory> factories,
        string type)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(type))
            return null;

        return factories.FirstOrDefault(f => f.SupportsType(type));
    }

    /// <summary>
    /// 解析支持指定类型的首个工厂；未找到时抛 <see cref="NotSupportedException"/>，
    /// 消息中列出当前已支持的类型。
    /// </summary>
    public static ISystemOneClientFactory Require(
        IEnumerable<ISystemOneClientFactory> factories,
        string type)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("SystemOne provider type is required.", nameof(type));

        var factory = Find(factories, type);
        if (factory is null)
        {
            var supported = string.Join(
                ", ",
                factories.SelectMany(f => f.SupportedTypes).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new NotSupportedException(
                $"不支持的 SystemOne Provider 类型: {type}（已支持: {supported}）");
        }

        return factory;
    }
}
```

- [ ] **Step 7: 构建，确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded`，0 Warning / 0 Error。

- [ ] **Step 8: 运行解析器测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneClientFactoryResolverTests"`
Expected: `Passed!  - Failed: 0`，7 个用例全部通过。

- [ ] **Step 9: 全量回归**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll`
Expected: `Passed!  - Failed: 0`。

- [ ] **Step 10: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneClient.cs src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneProvider.cs src/abstractions/Seeing.Agent.Abstractions/SystemOne/ISystemOneService.cs src/abstractions/Seeing.Agent.Abstractions/SystemOne/SystemOneClientFactoryResolver.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneClientFactoryResolverTests.cs
git commit -m "feat(systemone): 新增 SystemOne 契约接口与工厂解析器"
```

---

## 任务组 B：客户端层（Tasks 4-6）

> 依赖：Task 1（项目/`InternalsVisibleTo`）、Task 2-3（契约类型）。交付：`SystemOneHttpClientFactory`、`SystemOneHttpHelper`、`TypeSafeSystemOneClient` + 工厂及测试。

### Task 4: SystemOneHttpClientFactory（超时 + 连接池）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpClientFactory.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpClientFactoryTests.cs`

- [ ] **Step 1: 编写失败测试**

创建 `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpClientFactoryTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class SystemOneHttpClientFactoryTests
{
    [Fact]
    public void Create_WithConfiguredTimeout_SetsHttpClientTimeout()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig { Timeout = 3000 });

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(3000));
    }

    [Fact]
    public void Create_WithDefaultConfig_UsesDefaultTimeout()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig());

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(SystemOneDefaults.DefaultTimeoutMs));
    }

    [Fact]
    public void Create_WithNonPositiveTimeout_FallsBackToDefault()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig { Timeout = 0 });

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(SystemOneDefaults.DefaultTimeoutMs));
    }

    [Fact]
    public void CreateHandler_ConfiguresPooledConnectionLifetimeAndProxy()
    {
        using var handler = SystemOneHttpClientFactory.CreateHandler(new SystemOneProviderConfig());

        handler.PooledConnectionIdleTimeout.Should().Be(TimeSpan.FromMinutes(2));
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(5));
        handler.UseProxy.Should().BeFalse();
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: 找不到类型或命名空间名称 "SystemOneHttpClientFactory"`。

- [ ] **Step 3: 编写最小实现**

创建 `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpClientFactory.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>
/// 为单个 SystemOne provider 创建带连接池配置的 HttpClient。
/// </summary>
public static class SystemOneHttpClientFactory
{
    /// <summary>创建 HttpClient；Timeout 取 config.Timeout，非正数时回落到 DefaultTimeoutMs。</summary>
    public static HttpClient Create(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new HttpClient(CreateHandler(config), disposeHandler: true)
        {
            // Timeout 必须在首次请求前设置一次（HttpClient 发出请求后不可再改）
            Timeout = TimeSpan.FromMilliseconds(
                config.Timeout > 0 ? config.Timeout : SystemOneDefaults.DefaultTimeoutMs)
        };
    }

    /// <summary>创建 SocketsHttpHandler（连接池空闲/生命周期上限；本期不使用代理）。</summary>
    internal static SocketsHttpHandler CreateHandler(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = false
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneHttpClientFactoryTests"`
Expected: 构建 0 Error；`Failed: 0, Passed: 4, Total: 4`。

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpClientFactory.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpClientFactoryTests.cs
git commit -m "feat(systemone): add SystemOne HttpClient factory with timeout and pooling"
```

---

### Task 5: SystemOneHttpHelper（鉴权 + JSON + 429/529 退避重试）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpHelper.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpHelperTests.cs`

- [ ] **Step 1: 编写失败测试**

创建 `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpHelperTests.cs`：

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class SystemOneHttpHelperTests
{
    private const string ModelsJson = """{"models":[{"name":"jev-latest"}]}""";

    [Fact]
    public async Task PostAsync_SendsBearerTokenAcceptAndCustomHeaders()
    {
        var handler = new CapturingHandler("""{"model":"jev-latest","answers":{}}""");
        using var http = new HttpClient(handler);
        var config = Config();
        config.Headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" };

        await SystemOneHttpHelper.PostAsync<SystemOneRequest, SystemOneResponse>(
            http,
            config,
            SystemOneDefaults.EvaluatePath,
            new SystemOneRequest { State = "hello", Model = "m" },
            TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-key");
        handler.LastRequest.Headers.GetValues("Accept").Should().ContainSingle().Which.Should().Be("application/json");
        handler.LastRequest.Headers.GetValues("X-Tenant").Should().ContainSingle().Which.Should().Be("acme");
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://systemone.test/v1/systemone"));
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_On429Then200_RetriesAndReturnsBody()
    {
        var handler = new SequenceHandler(Json("rate limited", HttpStatusCode.TooManyRequests), Json(ModelsJson));
        using var http = new HttpClient(handler);

        var result = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        result.Models.Should().ContainSingle().Which.Name.Should().Be("jev-latest");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_On529Then200_RetriesAndReturnsBody()
    {
        var handler = new SequenceHandler(Json("overloaded", (HttpStatusCode)529), Json(ModelsJson));
        using var http = new HttpClient(handler);

        var result = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        result.Models.Should().ContainSingle();
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_On429BeyondMaxRetries_ThrowsSystemOneException()
    {
        var handler = new SequenceHandler(
            Json("rate limited", HttpStatusCode.TooManyRequests),
            Json("rate limited", HttpStatusCode.TooManyRequests),
            Json("rate limited", HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(maxRetries: 2), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(429);
        ex.Which.ResponseBody.Should().Be("rate limited");
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_On401_ThrowsImmediatelyWithoutRetry()
    {
        var handler = new SequenceHandler(Json("unauthorized", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(), SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<SystemOneException>();
        ex.Which.StatusCode.Should().Be(401);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_On429_RespectsRetryAfterHeader()
    {
        var retryAfter = TimeSpan.FromMilliseconds(80);
        var tooMany = Json("rate limited", HttpStatusCode.TooManyRequests);
        tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        var handler = new SequenceHandler(tooMany, Json(ModelsJson));
        using var http = new HttpClient(handler);
        // 基础退避压到 1ms，确保测得延迟来自 Retry-After
        var config = Config(retryBaseDelayMs: 1, retryMaxDelayMs: 1);

        var stopwatch = Stopwatch.StartNew();
        await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, config, SystemOneDefaults.ModelsPath, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(60));
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_WhenCallerCancels_DoesNotRetryAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CancelingHandler(cts);
        using var http = new HttpClient(handler);

        var act = () => SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            http, Config(maxRetries: 3), SystemOneDefaults.ModelsPath, cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
        handler.RequestCount.Should().Be(1);
    }

    private static SystemOneProviderConfig Config(
        int maxRetries = 2, int retryBaseDelayMs = 1, int retryMaxDelayMs = 1)
        => new()
        {
            Id = "typesafe",
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            MaxRetries = maxRetries,
            RetryBaseDelayMs = retryBaseDelayMs,
            RetryMaxDelayMs = retryMaxDelayMs
        };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = _responses.Count > 0 ? _responses.Dequeue() : Json("{}");
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string responseBody) => _responseBody = responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cts;

        public CancelingHandler(CancellationTokenSource cts) => _cts = cts;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            _cts.Cancel();
            throw new TaskCanceledException("调用方已取消");
        }
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0103: 当前上下文中不存在名称 "SystemOneHttpHelper"`。

- [ ] **Step 3: 编写最小实现**

创建 `src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpHelper.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>
/// SystemOne HTTP 请求辅助：统一鉴权头、JSON 序列化、错误码处理与 429/529 退避重试。
/// </summary>
internal static class SystemOneHttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>POST JSON 请求并反序列化响应。</summary>
    public static async Task<TResp> PostAsync<TReq, TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        TReq body,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        return await SendAsync<TResp>(http, config, path, HttpMethod.Post, json, ct).ConfigureAwait(false);
    }

    /// <summary>GET 请求并反序列化响应。</summary>
    public static async Task<TResp> GetAsync<TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        CancellationToken ct)
        => await SendAsync<TResp>(http, config, path, HttpMethod.Get, jsonBody: null, ct).ConfigureAwait(false);

    private static async Task<TResp> SendAsync<TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        HttpMethod method,
        string? jsonBody,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        var baseUrl = (config.BaseUrl ?? SystemOneDefaults.DefaultBaseUrl).TrimEnd('/');
        var requestUri = baseUrl + path;
        var maxRetries = config.MaxRetries > 0 ? config.MaxRetries : 0;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, requestUri);
                if (jsonBody is not null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                ApplyHeaders(request, config);

                using var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                var statusCode = (int)response.StatusCode;
                var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Deserialize<TResp>(responseBody, JsonOpts)
                        ?? throw new SystemOneException(statusCode, responseBody, "SystemOne 响应反序列化为空");
                }

                if (!IsRetryableStatus(response.StatusCode) || attempt >= maxRetries)
                {
                    throw new SystemOneException(
                        statusCode,
                        responseBody,
                        $"SystemOne 请求失败: HTTP {statusCode} {response.ReasonPhrase}");
                }

                // 429/529：优先遵循 Retry-After，否则指数退避
                await Task.Delay(ComputeDelay(attempt, config, TryGetRetryAfter(response)), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryableNetworkError(ex, ct) && attempt < maxRetries)
            {
                await Task.Delay(ComputeDelay(attempt, config, retryAfter: null), ct).ConfigureAwait(false);
            }
            catch (SystemOneException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new SystemOneException(0, null, $"SystemOne 网络请求失败: {ex.Message}", ex);
            }
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, SystemOneProviderConfig config)
    {
        if (!string.IsNullOrEmpty(config.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (config.Headers is null)
            return;

        foreach (var (key, value) in config.Headers)
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => (int)status is 429 or 529;

    private static bool IsRetryableNetworkError(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException;

    private static TimeSpan ComputeDelay(int attempt, SystemOneProviderConfig config, TimeSpan? retryAfter)
    {
        var baseDelayMs = config.RetryBaseDelayMs > 0 ? config.RetryBaseDelayMs : 500;
        var maxDelayMs = config.RetryMaxDelayMs > 0 ? config.RetryMaxDelayMs : 5000;

        var exponent = Math.Min(attempt, 16);
        var delayMs = Math.Min(baseDelayMs * Math.Pow(2, exponent), maxDelayMs);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        if (retryAfter is { } ra && ra > delay)
            delay = ra;

        return delay;
    }

    private static TimeSpan? TryGetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneHttpHelperTests"`
Expected: 构建 0 Error；`Failed: 0, Passed: 7, Total: 7`。

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Clients/SystemOneHttpHelper.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/SystemOneHttpHelperTests.cs
git commit -m "feat(systemone): add SystemOne HTTP helper with auth, JSON and 429/529 backoff retry"
```

---

### Task 6: TypeSafeSystemOneClient + TypeSafeSystemOneClientFactory

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClient.cs`
- Create: `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClientFactory.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientTests.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientFactoryTests.cs`

- [ ] **Step 1: 编写客户端失败测试**

创建 `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientTests.cs`：

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class TypeSafeSystemOneClientTests
{
    [Fact]
    public async Task EvaluateAsync_WhenRequestModelIsEmpty_FillsConfigModel()
    {
        var handler = new CapturingHandler("""{"model":"config-model","answers":{}}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        await client.EvaluateAsync(
            new SystemOneRequest { State = "state", Model = string.Empty },
            TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/systemone"));
        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("config-model");
    }

    [Fact]
    public async Task EvaluateAsync_WhenRequestModelProvided_DoesNotOverride()
    {
        var handler = new CapturingHandler("""{"model":"explicit-model","answers":{}}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        await client.EvaluateAsync(
            new SystemOneRequest { State = "state", Model = "explicit-model" },
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("explicit-model");
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsParsedModels()
    {
        var handler = new CapturingHandler("""{"models":[{"name":"jev-latest"},{"name":"jev-mini"}]}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().HaveCount(2);
        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://systemone.test/v1/models"));
    }

    [Fact]
    public async Task TestConnectionAsync_WhenRequestFails_ReturnsFalse()
    {
        var handler = new FailingHandler(HttpStatusCode.Unauthorized);
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var result = await client.TestConnectionAsync(TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenRequestSucceeds_ReturnsTrue()
    {
        var handler = new CapturingHandler("""{"models":[]}""");
        using var client = new TypeSafeSystemOneClient(
            Config("config-model"), new HttpClient(handler), NullLogger<TypeSafeSystemOneClient>.Instance);

        var result = await client.TestConnectionAsync(TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    private static SystemOneProviderConfig Config(string model)
        => new()
        {
            Id = "typesafe",
            Type = SystemOneProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Model = model,
            MaxRetries = 0,
            RetryBaseDelayMs = 1,
            RetryMaxDelayMs = 1
        };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string responseBody) => _responseBody = responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public FailingHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("unauthorized", Encoding.UTF8, "application/json")
            });
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: 找不到类型或命名空间名称 "TypeSafeSystemOneClient"`。

- [ ] **Step 3: 编写客户端最小实现**

创建 `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClient.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>TypeSafe（Jev）System One 协议客户端。</summary>
public sealed class TypeSafeSystemOneClient : ISystemOneClient, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TypeSafeSystemOneClient> _logger;

    /// <summary>构造 TypeSafe 客户端；HttpClient 由本类负责释放。</summary>
    public TypeSafeSystemOneClient(
        SystemOneProviderConfig config,
        HttpClient http,
        ILogger<TypeSafeSystemOneClient> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    /// <summary>provider id。</summary>
    public string ProviderId => _config.Id;

    /// <summary>provider 类型（固定 typesafe）。</summary>
    public string ProviderType => SystemOneProviderTypes.TypeSafe;

    /// <summary>发送 typed questions 并返回结构化应答。</summary>
    public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken ct)
    {
        // 模型补全：request.Model 为空时用 provider 配置的 Model（使 SYSTEMONE_MODEL 生效）
        var effective = string.IsNullOrWhiteSpace(request.Model)
            ? request with { Model = _config.Model }
            : request;

        _logger.LogDebug("SystemOne 评估: provider={ProviderId}, model={Model}", _config.Id, effective.Model);

        return SystemOneHttpHelper.PostAsync<SystemOneRequest, SystemOneResponse>(
            _http, _config, SystemOneDefaults.EvaluatePath, effective, ct);
    }

    /// <summary>列出可用模型/别名。</summary>
    public async Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken ct)
    {
        var resp = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            _http, _config, SystemOneDefaults.ModelsPath, ct).ConfigureAwait(false);
        return resp.Models;
    }

    /// <summary>连通性探测：能列出模型即视为连通；异常吞掉返回 false。</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
                _http, _config, SystemOneDefaults.ModelsPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (SystemOneException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>释放底层 HttpClient。</summary>
    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: 运行客户端测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~TypeSafeSystemOneClientTests"`
Expected: 构建 0 Error；`Failed: 0, Passed: 5, Total: 5`。

- [ ] **Step 5: 编写工厂失败测试**

创建 `tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientFactoryTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class TypeSafeSystemOneClientFactoryTests
{
    [Theory]
    [InlineData("typesafe")]
    [InlineData("TypeSafe")]
    [InlineData("TYPESAFE")]
    public void SupportsType_IsCaseInsensitive(string type)
    {
        var factory = CreateFactory();

        factory.SupportsType(type).Should().BeTrue();
    }

    [Fact]
    public void SupportsType_WithUnknownType_ReturnsFalse()
    {
        var factory = CreateFactory();

        factory.SupportsType("other").Should().BeFalse();
    }

    [Fact]
    public void SupportedTypes_ContainsTypeSafe()
    {
        var factory = CreateFactory();

        factory.SupportedTypes.Should().Contain(SystemOneProviderTypes.TypeSafe);
    }

    [Fact]
    public void Create_ReturnsTypeSafeClient()
    {
        var factory = CreateFactory();
        var config = new SystemOneProviderConfig
        {
            Id = "typesafe",
            Type = SystemOneProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Model = "jev-latest"
        };

        using var client = (TypeSafeSystemOneClient)factory.Create(config);

        client.Should().BeOfType<TypeSafeSystemOneClient>();
        client.ProviderId.Should().Be("typesafe");
        client.ProviderType.Should().Be(SystemOneProviderTypes.TypeSafe);
    }

    private static TypeSafeSystemOneClientFactory CreateFactory()
        => new(NullLoggerFactory.Instance);
}
```

- [ ] **Step 6: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: 找不到类型或命名空间名称 "TypeSafeSystemOneClientFactory"`。

- [ ] **Step 7: 编写工厂最小实现**

创建 `src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClientFactory.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>TypeSafe provider 客户端工厂（按 type 路由，大小写不敏感）。</summary>
public sealed class TypeSafeSystemOneClientFactory : ISystemOneClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>构造工厂。</summary>
    public TypeSafeSystemOneClientFactory(ILoggerFactory loggerFactory)
        => _loggerFactory = loggerFactory;

    /// <summary>支持的 provider 类型集合。</summary>
    public IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemOneProviderTypes.TypeSafe };

    /// <summary>判断是否支持给定 provider 类型（大小写不敏感）。</summary>
    public bool SupportsType(string type) => SupportedTypes.Contains(type);

    /// <summary>创建 TypeSafe 客户端（HttpClient 由客户端持有并释放）。</summary>
    public ISystemOneClient Create(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var http = SystemOneHttpClientFactory.Create(config);
        return new TypeSafeSystemOneClient(config, http, _loggerFactory.CreateLogger<TypeSafeSystemOneClient>());
    }
}
```

- [ ] **Step 8: 运行工厂测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~TypeSafeSystemOneClientFactoryTests"`
Expected: 构建 0 Error；`Failed: 0, Passed: 6, Total: 6`。

- [ ] **Step 9: 运行本组全部测试**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneHttpClientFactoryTests|FullyQualifiedName~SystemOneHttpHelperTests|FullyQualifiedName~TypeSafeSystemOneClientTests|FullyQualifiedName~TypeSafeSystemOneClientFactoryTests"`
Expected: `Failed: 0, Passed: 22, Total: 22`（4 + 7 + 5 + 6）。

- [ ] **Step 10: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClient.cs src/capabilities/Seeing.Agent.SystemOne/Clients/TypeSafeSystemOneClientFactory.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Clients/TypeSafeSystemOneClientFactoryTests.cs
git commit -m "feat(systemone): add TypeSafe SystemOne client and client factory"
```

---

## 任务组 C：配置 / 运行时 / 模块（Tasks 7-12）

## 前置条件与约定

- **前置（Task 1–6 已完成）**：`src/abstractions/Seeing.Agent.Abstractions/SystemOne/`（`SystemOneProviderConfig`、`SystemOneModels`、`SystemOneException`、`ISystemOneClient`/`ISystemOneClientFactory`、`ISystemOneProvider`/`ISystemOneProviderRegistry`、`ISystemOneService`、`SystemOneClientFactoryResolver`）与 `src/capabilities/Seeing.Agent.SystemOne/Clients/`（`SystemOneHttpClientFactory`、`SystemOneHttpHelper`、`TypeSafeSystemOneClient`、`TypeSafeSystemOneClientFactory`）以及测试工程 `tests/capabilities/Seeing.Agent.SystemOne.Tests/`（含 spec §9.1b 的 csproj）均已存在。
- **命名空间约定**（沿用本仓库同类能力包 Acp/Memory/Scheduler 的文件夹约定）：`Configuration/` → `Seeing.Agent.SystemOne.Configuration`；`Runtime/` → `Seeing.Agent.SystemOne.Runtime`；`Clients/` → `Seeing.Agent.SystemOne.Clients`；`Extensions/` → `Seeing.Agent.SystemOne.Extensions`；包根 `SystemOneModule.cs`、`PredefinedSystemOneProviders.cs` → `Seeing.Agent.SystemOne`。所有测试类统一用扁平命名空间 `Seeing.Agent.SystemOne.Tests`。
- **运行方式（AGENTS.md 约定，禁止 `dotnet test`）**：
  ```bash
  dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests
  dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~<测试类名>"
  ```
- 每个 Task 结束提交一次，提交信息用 `feat(systemone): ...`。

---

### Task 7: `SystemOneEnvironmentConfig`（环境变量解析）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneEnvironmentConfig.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneEnvironmentConfigTests.cs`
- Test (helper): `tests/capabilities/Seeing.Agent.SystemOne.Tests/EnvVarScope.cs`
- Test (assembly): `tests/capabilities/Seeing.Agent.SystemOne.Tests/TestAssembly.cs`

- [ ] **Step 1: 写失败测试与测试辅助（环境变量保存/恢复 + 关闭并行）**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/TestAssembly.cs`：

```csharp
using Xunit;

[assembly: Parallelization(Mode = ParallelMode.None)]
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/EnvVarScope.cs`：

```csharp
using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Tests;

/// <summary>临时设置环境变量并在 Dispose 时恢复原值（含删除）。</summary>
internal sealed class EnvVarScope : IDisposable
{
    private readonly List<(string Name, string? Original)> _saved = new();

    public EnvVarScope(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, original) in _saved)
            Environment.SetEnvironmentVariable(name, original);
        _saved.Clear();
    }
}

/// <summary>一次性设置四个 SYSTEMONE_* 变量（未传的显式清空，避免测试间污染）。</summary>
internal static class SystemOneTestEnv
{
    public static EnvVarScope Scope(
        string? apiKey = null,
        string? baseUrl = null,
        string? model = null,
        string? provider = null)
        => new(
            (SystemOneEnvironmentConfig.ApiKeyVar, apiKey),
            (SystemOneEnvironmentConfig.BaseUrlVar, baseUrl),
            (SystemOneEnvironmentConfig.ModelVar, model),
            (SystemOneEnvironmentConfig.ProviderVar, provider));
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneEnvironmentConfigTests.cs`：

```csharp
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneEnvironmentConfigTests
{
    [Fact]
    public void Resolve_非空环境变量_应Trim后返回()
    {
        using var _ = SystemOneTestEnv.Scope(
            apiKey: "  ts-key  ", baseUrl: "  https://x.ai/  ",
            model: "  jev-x  ", provider: "  typesafe  ");

        SystemOneEnvironmentConfig.ResolveApiKey().Should().Be("ts-key");
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().Be("https://x.ai/");
        SystemOneEnvironmentConfig.ResolveModel().Should().Be("jev-x");
        SystemOneEnvironmentConfig.ResolveProvider().Should().Be("typesafe");
    }

    [Fact]
    public void Resolve_空白环境变量_应返回Null()
    {
        using var _ = SystemOneTestEnv.Scope(apiKey: "   ", baseUrl: "", model: "\t", provider: " ");

        SystemOneEnvironmentConfig.ResolveApiKey().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveModel().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveProvider().Should().BeNull();
    }

    [Fact]
    public void Resolve_未设置环境变量_应返回Null()
    {
        using var _ = SystemOneTestEnv.Scope();

        SystemOneEnvironmentConfig.ResolveApiKey().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveBaseUrl().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveModel().Should().BeNull();
        SystemOneEnvironmentConfig.ResolveProvider().Should().BeNull();
    }

    [Fact]
    public void 变量名常量_应为约定值()
    {
        SystemOneEnvironmentConfig.ApiKeyVar.Should().Be("SYSTEMONE_API_KEY");
        SystemOneEnvironmentConfig.BaseUrlVar.Should().Be("SYSTEMONE_BASE_URL");
        SystemOneEnvironmentConfig.ModelVar.Should().Be("SYSTEMONE_MODEL");
        SystemOneEnvironmentConfig.ProviderVar.Should().Be("SYSTEMONE_PROVIDER");
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'SystemOneEnvironmentConfig' could not be found`（实现尚不存在）。

- [ ] **Step 3: 最小实现**

`src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneEnvironmentConfig.cs`：

```csharp
namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne 环境变量配置：常量 + 静态读取（仿 BootOverrideSource）。
/// <para>空白值一律视为未设置（返回 <c>null</c>）。</para>
/// </summary>
public static class SystemOneEnvironmentConfig
{
    /// <summary>API Key 环境变量名。</summary>
    public const string ApiKeyVar = "SYSTEMONE_API_KEY";

    /// <summary>Base URL 环境变量名。</summary>
    public const string BaseUrlVar = "SYSTEMONE_BASE_URL";

    /// <summary>模型环境变量名。</summary>
    public const string ModelVar = "SYSTEMONE_MODEL";

    /// <summary>默认 provider 环境变量名。</summary>
    public const string ProviderVar = "SYSTEMONE_PROVIDER";

    /// <summary>解析 API Key；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveApiKey() => Read(ApiKeyVar);

    /// <summary>解析 Base URL；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveBaseUrl() => Read(BaseUrlVar);

    /// <summary>解析模型；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveModel() => Read(ModelVar);

    /// <summary>解析默认 provider id；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveProvider() => Read(ProviderVar);

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneEnvironmentConfigTests"`
Expected: `Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4`

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneEnvironmentConfig.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneEnvironmentConfigTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/EnvVarScope.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/TestAssembly.cs
git commit -m "feat(systemone): 添加 SYSTEMONE_* 环境变量解析与测试"
```

---

### Task 8: `SystemOneConfigStore` + `PredefinedSystemOneProviders`

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneConfigStore.cs`
- Create: `src/capabilities/Seeing.Agent.SystemOne/PredefinedSystemOneProviders.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneConfigStoreTests.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/PredefinedSystemOneProvidersTests.cs`
- Test (helper): `tests/capabilities/Seeing.Agent.SystemOne.Tests/TestConfigSectionStore.cs`

- [ ] **Step 1: 写失败测试与内存配置存储 fake**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/TestConfigSectionStore.cs`：

```csharp
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Tests;

/// <summary>内存版 <see cref="IConfigSectionStore"/>；缺失节返回 null 以模拟真实兜底路径。</summary>
internal sealed class TestConfigSectionStore : IConfigSectionStore
{
    private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);

    public TestConfigSectionStore()
    {
    }

    public TestConfigSectionStore(Dictionary<string, SystemOneProviderConfig> systemOne)
        => _sections[SystemOneConfigStore.SectionName] = systemOne;

    public T GetSection<T>(string sectionName) where T : class, new()
        => _sections.TryGetValue(sectionName, out var value) && value is T typed ? typed : default!;

    public Task SaveSectionAsync<T>(
        string sectionName,
        T value,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
        where T : class
    {
        _sections[sectionName] = value!;
        return Task.CompletedTask;
    }

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
    {
        add { }
        remove { }
    }
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneConfigStoreTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneConfigStoreTests
{
    [Fact]
    public void 常量_应为约定值()
    {
        SystemOneConfigStore.SectionName.Should().Be("SystemOne");
        SystemOneConfigStore.FileName.Should().Be("systemone.json");
    }

    [Fact]
    public void SectionMeta_应声明UserOnly与字典类型()
    {
        var meta = SystemOneConfigStore.SectionMeta;

        meta.Key.Should().Be(SystemOneConfigStore.SectionName);
        meta.FileName.Should().Be(SystemOneConfigStore.FileName);
        meta.Scope.Should().Be(ConfigScope.UserOnly);
        meta.SectionType.Should().Be(typeof(Dictionary<string, SystemOneProviderConfig>));
    }

    [Fact]
    public void Read_存在配置_应返回同一字典()
    {
        var expected = new Dictionary<string, SystemOneProviderConfig>
        {
            ["typesafe"] = new()
            {
                Id = "typesafe",
                Type = SystemOneProviderTypes.TypeSafe,
                ApiKey = "k"
            }
        };
        var sut = new SystemOneConfigStore(new TestConfigSectionStore(expected));

        sut.Read().Should().BeSameAs(expected);
    }

    [Fact]
    public void Read_缺失配置_应返回空字典()
    {
        var sut = new SystemOneConfigStore(new TestConfigSectionStore());

        var result = sut.Read();

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void 构造_空存储_应抛ArgumentNullException()
    {
        var act = () => new SystemOneConfigStore(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/PredefinedSystemOneProvidersTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class PredefinedSystemOneProvidersTests
{
    [Fact]
    public void CreateDefaultTypeSafe_应返回内置默认值()
    {
        var config = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        config.Id.Should().Be("typesafe");
        config.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        config.Name.Should().Be("TypeSafe Jev");
        config.BaseUrl.Should().Be(SystemOneDefaults.DefaultBaseUrl);
        config.Model.Should().Be(SystemOneDefaults.DefaultModel);
        config.Timeout.Should().Be(SystemOneDefaults.DefaultTimeoutMs);
        config.MaxRetries.Should().Be(2);
        config.ApiKey.Should().BeNull();
    }

    [Fact]
    public void CreateDefaultTypeSafe_每次应返回独立实例()
    {
        var first = PredefinedSystemOneProviders.CreateDefaultTypeSafe();
        var second = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        first.Should().NotBeSameAs(second);
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'SystemOneConfigStore' could not be found` 及 `'PredefinedSystemOneProviders' could not be found`。

- [ ] **Step 3: 最小实现**

`src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneConfigStore.cs`：

```csharp
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne 配置节存取：基于注入的 <see cref="IConfigSectionStore"/> 读取
/// <c>systemone.json</c>（顶层 <c>Dictionary&lt;string, SystemOneProviderConfig&gt;</c>）。
/// </summary>
public sealed class SystemOneConfigStore
{
    /// <summary>配置节键名。</summary>
    public const string SectionName = "SystemOne";

    /// <summary>配置文件名称（用户级顶层文件）。</summary>
    public const string FileName = "systemone.json";

    private readonly IConfigSectionStore _store;

    /// <summary>构造配置节存取器。</summary>
    public SystemOneConfigStore(IConfigSectionStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>配置节元信息（Key/文件名/UserOnly/值类型）。</summary>
    public static ConfigSectionMeta SectionMeta { get; } = new(
        SectionName,
        FileName,
        ConfigScope.UserOnly,
        typeof(Dictionary<string, SystemOneProviderConfig>));

    /// <summary>读取 provider 配置字典；缺失时返回空字典（不返回 null）。</summary>
    public Dictionary<string, SystemOneProviderConfig> Read()
        => _store.GetSection<Dictionary<string, SystemOneProviderConfig>>(SectionName)
           ?? new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
}
```

`src/capabilities/Seeing.Agent.SystemOne/PredefinedSystemOneProviders.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne;

/// <summary>内置 SystemOne provider 的默认配置工厂（纯数据，无副作用）。</summary>
public static class PredefinedSystemOneProviders
{
    /// <summary>内置 typesafe provider 的默认配置（未含环境变量覆盖）。</summary>
    public static SystemOneProviderConfig CreateDefaultTypeSafe() => new()
    {
        Id = "typesafe",
        Type = SystemOneProviderTypes.TypeSafe,
        Name = "TypeSafe Jev",
        BaseUrl = SystemOneDefaults.DefaultBaseUrl,
        Model = SystemOneDefaults.DefaultModel,
        Timeout = SystemOneDefaults.DefaultTimeoutMs,
        MaxRetries = 2
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneConfigStoreTests"`
Expected: `Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~PredefinedSystemOneProvidersTests"`
Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2`

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneConfigStore.cs src/capabilities/Seeing.Agent.SystemOne/PredefinedSystemOneProviders.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneConfigStoreTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/PredefinedSystemOneProvidersTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/TestConfigSectionStore.cs
git commit -m "feat(systemone): 添加配置节存取与内置 typesafe 默认配置工厂"
```

---

### Task 9: `ConfiguredSystemOneProvider`（延迟创建客户端 + Dispose）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Runtime/ConfiguredSystemOneProvider.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/ConfiguredSystemOneProviderTests.cs`
- Test (helper): `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs`

- [ ] **Step 1: 写失败测试与测试替身**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Tests;

internal sealed class StubSystemOneClient : ISystemOneClient, IDisposable
{
    public string ProviderId { get; set; } = "stub";
    public string ProviderType { get; set; } = SystemOneProviderTypes.TypeSafe;
    public int DisposeCount { get; private set; }
    public Func<SystemOneRequest, CancellationToken, Task<SystemOneResponse>>? OnEvaluate { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<SystemOneModel>>>? OnListModels { get; set; }

    public Task<SystemOneResponse> EvaluateAsync(
        SystemOneRequest request, CancellationToken cancellationToken = default)
        => OnEvaluate?.Invoke(request, cancellationToken) ?? Task.FromResult(new SystemOneResponse());

    public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default)
        => OnListModels?.Invoke(cancellationToken)
           ?? Task.FromResult<IReadOnlyList<SystemOneModel>>(Array.Empty<SystemOneModel>());

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void Dispose() => DisposeCount++;
}

internal sealed class StubSystemOneClientFactory : ISystemOneClientFactory
{
    public IReadOnlySet<string> SupportedTypes { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemOneProviderTypes.TypeSafe };

    public List<SystemOneProviderConfig> CreatedConfigs { get; } = new();
    public Func<SystemOneProviderConfig, ISystemOneClient>? ClientFactory { get; set; }
    public int CreateCount { get; private set; }

    public bool SupportsType(string type) => SupportedTypes.Contains(type);

    public ISystemOneClient Create(SystemOneProviderConfig config)
    {
        CreateCount++;
        CreatedConfigs.Add(config);
        return ClientFactory?.Invoke(config)
            ?? new StubSystemOneClient { ProviderId = config.Id, ProviderType = config.Type };
    }
}

internal sealed class StubSystemOneProvider : ISystemOneProvider, IDisposable
{
    public string Id { get; set; } = "stub";
    public string? Name { get; set; }
    public string Type { get; set; } = SystemOneProviderTypes.TypeSafe;
    public int MaxRetries { get; set; } = 2;
    public StubSystemOneClient Client { get; } = new();
    public bool Disposed { get; private set; }

    public ISystemOneClient GetClient() => Client;

    public Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Client.ListModelsAsync(cancellationToken);

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Client.TestConnectionAsync(cancellationToken);

    public void Dispose() => Disposed = true;
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/ConfiguredSystemOneProviderTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Runtime;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class ConfiguredSystemOneProviderTests
{
    private static SystemOneProviderConfig Config() => new()
    {
        Id = "typesafe",
        Name = "TS",
        Type = SystemOneProviderTypes.TypeSafe,
        ApiKey = "k",
        MaxRetries = 3
    };

    [Fact]
    public void 属性_应透传配置()
    {
        var factory = new StubSystemOneClientFactory();
        using var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        sut.Id.Should().Be("typesafe");
        sut.Name.Should().Be("TS");
        sut.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        sut.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void GetClient_应延迟创建且复用同一实例()
    {
        var factory = new StubSystemOneClientFactory();
        using var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        factory.CreateCount.Should().Be(0);

        var first = sut.GetClient();
        var second = sut.GetClient();

        factory.CreateCount.Should().Be(1);
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Dispose_已创建客户端_应释放客户端()
    {
        var client = new StubSystemOneClient();
        var factory = new StubSystemOneClientFactory { ClientFactory = _ => client };
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);
        _ = sut.GetClient();

        sut.Dispose();

        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_未创建客户端_不应触发工厂()
    {
        var factory = new StubSystemOneClientFactory();
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        var act = () => sut.Dispose();

        act.Should().NotThrow();
        factory.CreateCount.Should().Be(0);
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'ConfiguredSystemOneProvider' could not be found`。

- [ ] **Step 3: 最小实现**

`src/capabilities/Seeing.Agent.SystemOne/Runtime/ConfiguredSystemOneProvider.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Runtime;

/// <summary>
/// 配置驱动的 SystemOne provider：持有配置与工厂，延迟创建（且复用）底层客户端。
/// </summary>
public sealed class ConfiguredSystemOneProvider : ISystemOneProvider, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly ISystemOneClientFactory _factory;
    private readonly Lazy<ISystemOneClient> _client;

    /// <summary>使用配置与客户端工厂构造 provider（客户端延迟创建）。</summary>
    public ConfiguredSystemOneProvider(
        SystemOneProviderConfig config,
        ISystemOneClientFactory factory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _factory = factory;
        _client = new Lazy<ISystemOneClient>(
            () =>
            {
                logger.LogDebug("创建 SystemOne 客户端: {ProviderId} ({Type})", _config.Id, _config.Type);
                return _factory.Create(_config);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Id => _config.Id;

    /// <inheritdoc />
    public string? Name => _config.Name;

    /// <inheritdoc />
    public string Type => _config.Type;

    /// <inheritdoc />
    public int MaxRetries => _config.MaxRetries;

    /// <inheritdoc />
    public ISystemOneClient GetClient() => _client.Value;

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken ct = default)
        => GetClient().ListModelsAsync(ct);

    /// <inheritdoc />
    public Task<bool> TestConnectionAsync(CancellationToken ct = default)
        => GetClient().TestConnectionAsync(ct);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_client.IsValueCreated && _client.Value is IDisposable disposable)
            disposable.Dispose();
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~ConfiguredSystemOneProviderTests"`
Expected: `Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4`

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Runtime/ConfiguredSystemOneProvider.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/ConfiguredSystemOneProviderTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs
git commit -m "feat(systemone): 添加配置驱动 provider（延迟创建客户端 + Dispose）"
```

---

### Task 10: `SystemOneProviderManager`（注册表 / 默认 provider / 热重载）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneProviderManager.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneProviderManagerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneProviderManagerTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneProviderManagerTests
{
    private static SystemOneProviderManager CreateManager(
        Dictionary<string, SystemOneProviderConfig>? configs = null,
        IEnumerable<ISystemOneClientFactory>? factories = null)
    {
        var store = new SystemOneConfigStore(new TestConfigSectionStore(
            configs ?? new Dictionary<string, SystemOneProviderConfig>()));
        return new SystemOneProviderManager(
            store,
            factories ?? new ISystemOneClientFactory[] { new StubSystemOneClientFactory() },
            NullLogger<SystemOneProviderManager>.Instance);
    }

    [Fact]
    public void Register_同Id去重且释放被替换实例()
    {
        using var manager = CreateManager();
        var first = new StubSystemOneProvider { Id = "x" };
        var second = new StubSystemOneProvider { Id = "x" };

        manager.Register(first);
        manager.Register(second);

        manager.Providers.Should().ContainSingle().Which.Should().BeSameAs(second);
        first.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Unregister_移除并释放_返回True()
    {
        using var manager = CreateManager();
        var provider = new StubSystemOneProvider { Id = "x" };
        manager.Register(provider);

        manager.Unregister("x").Should().BeTrue();
        manager.Providers.Should().BeEmpty();
        provider.Disposed.Should().BeTrue();
        manager.Unregister("x").Should().BeFalse();
    }

    [Fact]
    public void UnregisterByOwner_仅移除该Owner()
    {
        using var manager = CreateManager();
        var a = new StubSystemOneProvider { Id = "a" };
        var b = new StubSystemOneProvider { Id = "b" };
        manager.Register(a, ownerExtensionId: "ext-a");
        manager.Register(b, ownerExtensionId: "ext-b");

        manager.UnregisterByOwner("ext-a").Should().Be(1);

        manager.Providers.Select(p => p.Id).Should().Equal("b");
        a.Disposed.Should().BeTrue();
        b.Disposed.Should().BeFalse();
    }

    [Fact]
    public void ProvidersChanged_应在注册与注销时触发()
    {
        using var manager = CreateManager();
        var count = 0;
        manager.ProvidersChanged += (_, _) => count++;

        manager.Register(new StubSystemOneProvider { Id = "x" });
        manager.Unregister("x");

        count.Should().Be(2);
    }

    [Fact]
    public void Reload_无ApiKey_不注册Provider()
    {
        using var env = SystemOneTestEnv.Scope();
        using var manager = CreateManager();

        manager.Reload();

        manager.Providers.Should().BeEmpty();
        manager.DefaultProvider.Should().BeNull();
    }

    [Fact]
    public void Reload_未知类型_跳过并告警()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["weird"] = new() { Id = "weird", Type = "weird", ApiKey = "k" }
        });

        manager.Reload();

        manager.Providers.Select(p => p.Id).Should().Equal("typesafe");
    }

    [Fact]
    public void Reload_环境变量_覆盖内置TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(
            apiKey: "env-key", baseUrl: "https://env.example", model: "env-model");
        var factory = new StubSystemOneClientFactory();
        using var manager = CreateManager(factories: new ISystemOneClientFactory[] { factory });

        manager.Reload();

        var provider = manager.Providers.Should().ContainSingle().Subject;
        provider.Id.Should().Be("typesafe");
        _ = provider.GetClient();

        var captured = factory.CreatedConfigs.Should().ContainSingle().Subject;
        captured.ApiKey.Should().Be("env-key");
        captured.BaseUrl.Should().Be("https://env.example");
        captured.Model.Should().Be("env-model");
    }

    [Fact]
    public void Reload_保留外部Register的Provider()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "external" };
        manager.Register(external);

        manager.Reload();

        manager.Providers.Select(p => p.Id).Should().Contain(new[] { "external", "typesafe" });
        manager.Providers.Should().ContainSingle(p => p.Id == "external").Which.Should().BeSameAs(external);
        external.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Reload_仅重建本模块Owner条目()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "external" };
        manager.Register(external);

        manager.Reload();
        var firstTypesafe = manager.Providers.Single(p => p.Id == "typesafe");
        manager.Reload();
        var secondTypesafe = manager.Providers.Single(p => p.Id == "typesafe");

        secondTypesafe.Should().NotBeSameAs(firstTypesafe);
        manager.Providers.Single(p => p.Id == "external").Should().BeSameAs(external);
        manager.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Reload_外部同Id优先_跳过内置()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "typesafe" };
        manager.Register(external);

        manager.Reload();

        manager.Providers.Should().ContainSingle().Which.Should().BeSameAs(external);
        external.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_释放全部Provider()
    {
        var manager = CreateManager();
        var provider = new StubSystemOneProvider { Id = "x" };
        manager.Register(provider);

        manager.Dispose();

        provider.Disposed.Should().BeTrue();
        manager.Providers.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'SystemOneProviderManager' could not be found`。

- [ ] **Step 3: 最小实现**

`src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneProviderManager.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Runtime;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne provider 管理器：从配置 + 环境变量重建本模块自有的 provider 集合，
/// 同时保留外部 <see cref="Register"/> 进来的 provider；线程安全，实现 <see cref="IDisposable"/>。
/// </summary>
public sealed class SystemOneProviderManager : ISystemOneProviderRegistry, IDisposable
{
    /// <summary>本模块自身 owner 标识（Deactivate 按此注销）。</summary>
    private const string OwnerId = "systemone";

    private readonly SystemOneConfigStore _configStore;
    private readonly IReadOnlyList<ISystemOneClientFactory> _factories;
    private readonly ILogger<SystemOneProviderManager> _logger;
    private readonly List<(ISystemOneProvider Provider, string Owner)> _entries = new();
    private readonly object _gate = new();

    private ISystemOneProvider? _defaultProvider;
    private bool _disposed;

    /// <summary>注入配置存取器、客户端工厂集合与日志。</summary>
    public SystemOneProviderManager(
        SystemOneConfigStore configStore,
        IEnumerable<ISystemOneClientFactory> factories,
        ILogger<SystemOneProviderManager> logger)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _factories = (factories ?? throw new ArgumentNullException(nameof(factories))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<ISystemOneProvider> Providers
    {
        get
        {
            lock (_gate)
                return _entries.Select(e => e.Provider).ToArray();
        }
    }

    /// <inheritdoc />
    public ISystemOneProvider? DefaultProvider
    {
        get
        {
            lock (_gate)
                return _defaultProvider;
        }
    }

    /// <inheritdoc />
    public event EventHandler? ProvidersChanged;

    /// <summary>
    /// 从配置 + 环境变量重建“本模块自有的” provider 集合（幂等）。
    /// <para>只替换 owner == <c>systemone</c> 的条目；外部 Register 的 provider 原样保留。</para>
    /// </summary>
    public void Reload()
    {
        Dictionary<string, SystemOneProviderConfig> fileEntries;
        try
        {
            fileEntries = _configStore.Read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 SystemOne 配置失败，仅使用内置默认 provider");
            fileEntries = new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
        }

        // 以字典键为权威 id（与 providers.json 一致），统一归一化 type
        var work = new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, config) in fileEntries)
        {
            if (config is null)
                continue;

            var id = string.IsNullOrWhiteSpace(key) ? config.Id : key;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            config.Id = id;
            config.Type = SystemOneProviderTypes.Normalize(config.Type);
            work[id] = config;
        }

        // 内置 typesafe 兜底，随后统一套用环境变量覆盖
        if (!work.ContainsKey("typesafe"))
            work["typesafe"] = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        var typeSafe = work["typesafe"];
        typeSafe.Type = SystemOneProviderTypes.Normalize(
            string.IsNullOrWhiteSpace(typeSafe.Type) ? SystemOneProviderTypes.TypeSafe : typeSafe.Type);

        var envBaseUrl = SystemOneEnvironmentConfig.ResolveBaseUrl();
        if (!string.IsNullOrWhiteSpace(envBaseUrl))
            typeSafe.BaseUrl = envBaseUrl;

        var envApiKey = SystemOneEnvironmentConfig.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(envApiKey))
            typeSafe.ApiKey = envApiKey;

        var envModel = SystemOneEnvironmentConfig.ResolveModel();
        if (!string.IsNullOrWhiteSpace(envModel))
            typeSafe.Model = envModel;

        var rebuilt = new List<ISystemOneProvider>();
        foreach (var (_, config) in work)
        {
            var factory = SystemOneClientFactoryResolver.Find(_factories, config.Type);
            if (factory is null)
            {
                _logger.LogWarning(
                    "跳过未知 SystemOne provider 类型: {ProviderId} ({Type})", config.Id, config.Type);
                continue;
            }

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                _logger.LogWarning("SystemOne provider {ProviderId} 缺少 ApiKey，已跳过注册", config.Id);
                continue;
            }

            rebuilt.Add(new ConfiguredSystemOneProvider(config, factory, _logger));
            _logger.LogDebug("已加载 SystemOne provider: {ProviderId} ({Type})", config.Id, config.Type);
        }

        List<ISystemOneProvider> replaced;
        var collided = new List<ISystemOneProvider>();
        lock (_gate)
        {
            replaced = _entries.Where(e => e.Owner == OwnerId).Select(e => e.Provider).ToList();
            _entries.RemoveAll(e => e.Owner == OwnerId);

            foreach (var provider in rebuilt)
            {
                var collision = _entries.Any(e =>
                    string.Equals(e.Provider.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
                if (collision)
                {
                    _logger.LogInformation(
                        "SystemOne provider {ProviderId} 已由外部注册，跳过配置驱动加载", provider.Id);
                    collided.Add(provider);
                    continue;
                }

                _entries.Add((provider, OwnerId));
            }

            _defaultProvider = ResolveDefaultProviderLocked();
        }

        foreach (var provider in replaced)
            DisposeProviderSafe(provider);
        foreach (var provider in collided)
            DisposeProviderSafe(provider);

        RaiseProvidersChanged();
    }

    /// <inheritdoc />
    public void Register(ISystemOneProvider provider, string? ownerExtensionId = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var owner = ownerExtensionId ?? string.Empty;
        ISystemOneProvider? replaced = null;

        lock (_gate)
        {
            var index = _entries.FindIndex(e =>
                string.Equals(e.Provider.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                replaced = _entries[index].Provider;
                _entries[index] = (provider, owner);
            }
            else
            {
                _entries.Add((provider, owner));
            }

            _defaultProvider = ResolveDefaultProviderLocked();
        }

        DisposeProviderSafe(replaced);
        RaiseProvidersChanged();
    }

    /// <inheritdoc />
    public bool Unregister(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        ISystemOneProvider? removed;
        lock (_gate)
        {
            var index = _entries.FindIndex(e =>
                string.Equals(e.Provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            removed = _entries[index].Provider;
            _entries.RemoveAt(index);
            _defaultProvider = ResolveDefaultProviderLocked();
        }

        DisposeProviderSafe(removed);
        RaiseProvidersChanged();
        return true;
    }

    /// <inheritdoc />
    public int UnregisterByOwner(string ownerExtensionId)
    {
        ArgumentNullException.ThrowIfNull(ownerExtensionId);

        List<ISystemOneProvider> removed;
        lock (_gate)
        {
            removed = _entries
                .Where(e => string.Equals(e.Owner, ownerExtensionId, StringComparison.Ordinal))
                .Select(e => e.Provider)
                .ToList();

            if (removed.Count == 0)
                return 0;

            _entries.RemoveAll(e => string.Equals(e.Owner, ownerExtensionId, StringComparison.Ordinal));
            _defaultProvider = ResolveDefaultProviderLocked();
        }

        foreach (var provider in removed)
            DisposeProviderSafe(provider);

        RaiseProvidersChanged();
        return removed.Count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<ISystemOneProvider> all;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            all = _entries.Select(e => e.Provider).ToList();
            _entries.Clear();
            _defaultProvider = null;
        }

        foreach (var provider in all)
            DisposeProviderSafe(provider);
    }

    /// <summary>默认 provider：env <c>SYSTEMONE_PROVIDER</c> &gt; id <c>typesafe</c> &gt; 首个 &gt; null。</summary>
    private ISystemOneProvider? ResolveDefaultProviderLocked()
    {
        var envId = SystemOneEnvironmentConfig.ResolveProvider();
        if (!string.IsNullOrWhiteSpace(envId))
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Provider.Id, envId, StringComparison.OrdinalIgnoreCase))
                    return entry.Provider;
            }
        }

        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Provider.Id, "typesafe", StringComparison.OrdinalIgnoreCase))
                return entry.Provider;
        }

        return _entries.Count > 0 ? _entries[0].Provider : null;
    }

    private void RaiseProvidersChanged() => ProvidersChanged?.Invoke(this, EventArgs.Empty);

    private void DisposeProviderSafe(ISystemOneProvider? provider)
    {
        if (provider is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 SystemOne provider {ProviderId} 失败", provider.Id);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneProviderManagerTests"`
Expected: `Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11`

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneProviderManager.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneProviderManagerTests.cs
git commit -m "feat(systemone): 添加 provider 注册表与热重载（保留外部注册，环境变量覆盖内置）"
```

---

### Task 11: `SystemOneService`（门面 + 默认 provider 选择）

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Runtime/SystemOneService.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneServiceTests.cs`
- Modify: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs`（新增 `FakeSystemOneProviderRegistry`）

- [ ] **Step 1: 写失败测试并新增注册表 fake**

在 `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs` 末尾追加：

```csharp
internal sealed class FakeSystemOneProviderRegistry : ISystemOneProviderRegistry
{
    public List<ISystemOneProvider> Items { get; } = new();

    public ISystemOneProvider? DefaultProvider { get; set; }

    public IReadOnlyList<ISystemOneProvider> Providers => Items;

    public void Register(ISystemOneProvider provider, string? ownerExtensionId = null)
        => Items.Add(provider);

    public bool Unregister(string providerId)
        => Items.RemoveAll(p => p.Id == providerId) > 0;

    public int UnregisterByOwner(string ownerExtensionId) => 0;

    public event EventHandler? ProvidersChanged
    {
        add { }
        remove { }
    }
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneServiceTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Runtime;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneServiceTests
{
    [Fact]
    public async Task EvaluateAsync_使用默认Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var provider = new StubSystemOneProvider { Id = "typesafe" };
        registry.DefaultProvider = provider;
        registry.Items.Add(provider);
        var calls = 0;
        provider.Client.OnEvaluate = (_, _) =>
        {
            calls++;
            return Task.FromResult(new SystemOneResponse());
        };
        var sut = new SystemOneService(registry);

        await sut.EvaluateAsync(new SystemOneRequest(), TestContext.Current.CancellationToken);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_无默认Provider_抛InvalidOperationException()
    {
        var sut = new SystemOneService(new FakeSystemOneProviderRegistry());

        var act = async () => await sut.EvaluateAsync(new SystemOneRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListModelsAsync_指定Provider_使用该Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var providerA = new StubSystemOneProvider { Id = "a" };
        var providerB = new StubSystemOneProvider { Id = "b" };
        registry.DefaultProvider = providerA;
        registry.Items.Add(providerA);
        registry.Items.Add(providerB);

        var bModels = new List<SystemOneModel> { new() { Name = "bm" } };
        providerB.Client.OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(bModels);
        var sut = new SystemOneService(registry);

        var result = await sut.ListModelsAsync("b", TestContext.Current.CancellationToken);

        result.Should().BeSameAs(bModels);
    }

    [Fact]
    public async Task ListModelsAsync_未指定_使用默认Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var provider = new StubSystemOneProvider { Id = "typesafe" };
        registry.DefaultProvider = provider;
        registry.Items.Add(provider);
        var models = new List<SystemOneModel> { new() { Name = "m" } };
        provider.Client.OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(models);
        var sut = new SystemOneService(registry);

        var result = await sut.ListModelsAsync(null, TestContext.Current.CancellationToken);

        result.Should().BeSameAs(models);
    }

    [Fact]
    public async Task ListModelsAsync_指定不存在_抛InvalidOperationException()
    {
        var sut = new SystemOneService(new FakeSystemOneProviderRegistry());

        var act = async () => await sut.ListModelsAsync("missing", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 默认Provider选择_Env优先于TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k", provider: "custom");
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["custom"] = new() { Id = "custom", Type = SystemOneProviderTypes.TypeSafe, ApiKey = "k" }
        });

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("custom");
    }

    [Fact]
    public async Task 默认Provider选择_回退TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>());

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("typesafe");
    }

    [Fact]
    public async Task 默认Provider选择_无TypeSafe时取首个()
    {
        using var env = SystemOneTestEnv.Scope();
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["alpha"] = new() { Id = "alpha", Type = SystemOneProviderTypes.TypeSafe, ApiKey = "k" }
        });

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("alpha");
    }

    private static SystemOneProviderManager CreateManager(
        Dictionary<string, SystemOneProviderConfig> configs)
    {
        var store = new SystemOneConfigStore(new TestConfigSectionStore(configs));
        var factory = new StubSystemOneClientFactory
        {
            ClientFactory = config => new StubSystemOneClient
            {
                ProviderId = config.Id,
                OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(
                    new List<SystemOneModel> { new() { Name = config.Id } })
            }
        };
        var manager = new SystemOneProviderManager(
            store,
            new ISystemOneClientFactory[] { factory },
            NullLogger<SystemOneProviderManager>.Instance);
        manager.Reload();
        return manager;
    }
}
```

- [ ] **Step 2: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'SystemOneService' could not be found`。

- [ ] **Step 3: 最小实现**

`src/capabilities/Seeing.Agent.SystemOne/Runtime/SystemOneService.cs`：

```csharp
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Runtime;

/// <summary>SystemOne 业务门面：按默认 provider 转发调用。</summary>
public sealed class SystemOneService : ISystemOneService
{
    private readonly ISystemOneProviderRegistry _registry;

    /// <summary>注入 provider 注册表。</summary>
    public SystemOneService(ISystemOneProviderRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public Task<SystemOneResponse> EvaluateAsync(
        SystemOneRequest request, CancellationToken cancellationToken = default)
        => RequireProvider(_registry.DefaultProvider).GetClient().EvaluateAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
        => (providerId is null
                ? RequireProvider(_registry.DefaultProvider)
                : RequireProvider(_registry.Providers.FirstOrDefault(p => p.Id == providerId)))
            .GetModelsAsync(cancellationToken);

    private static ISystemOneProvider RequireProvider(ISystemOneProvider? provider)
        => provider ?? throw new InvalidOperationException(
            "未配置可用的 SystemOne provider（检查 SYSTEMONE_API_KEY 与 systemone.json）");
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneServiceTests"`
Expected: `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8`

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Runtime/SystemOneService.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneServiceTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneTestDoubles.cs
git commit -m "feat(systemone): 添加 SystemOneService 门面与默认 provider 选择测试"
```

---

### Task 12: `SystemOneReloadHandler` + `SystemOneModule` + `AddSystemOne` + DI 集成测试

**Files:**
- Create: `src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneReloadHandler.cs`
- Create: `src/capabilities/Seeing.Agent.SystemOne/SystemOneModule.cs`
- Create: `src/capabilities/Seeing.Agent.SystemOne/Extensions/SystemOneServiceExtensions.cs`
- Modify: `tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleTests.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneReloadHandlerTests.cs`
- Test: `tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleDiTests.cs`

- [ ] **Step 1: 修改测试工程（DI 集成测试前置）**

DI 集成测试需要 `ServiceCollection`（`Microsoft.Extensions.DependencyInjection` 具体包）、`ConfigSectionRegistry`/`AddSeeingCore`（`Seeing.Agent.Core`）。整份替换 `tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj` 为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.SystemOne.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Moq" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\src\capabilities\Seeing.Agent.SystemOne\Seeing.Agent.SystemOne.csproj" />
    <ProjectReference Include="..\..\..\src\spine\Seeing.Agent.Core\Seeing.Agent.Core.csproj" />
  </ItemGroup>

</Project>
```

> 说明：`Microsoft.Extensions.DependencyInjection` 提供 `ServiceCollection`；`Seeing.Agent.Core` 提供 `ConfigSectionRegistry` 与 `AddSeeingCore`，用于验证「`AddSystemOne` 须在 `AddSeeingCore` 之前」的真实组合。二者版本均由中央包管理控制，不写版本号。

- [ ] **Step 2: 写失败测试（模块元数据 / 重载 / DI 集成 / 配置节登记）**

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleTests.cs`：

```csharp
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneModuleTests
{
    [Fact]
    public void 模块元数据_应声明稳定Id且无工具与依赖()
    {
        var module = new SystemOneModule();

        module.Id.Should().Be("systemone");
        module.ProvidedTools.Should().BeEmpty();
        module.ProvidedSeams.Should().BeEmpty();
        module.DependsOn.Should().BeEmpty();
    }
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneReloadHandlerTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneReloadHandlerTests
{
    private static SystemOneProviderManager CreateManager()
        => new(
            new SystemOneConfigStore(new TestConfigSectionStore()),
            new ISystemOneClientFactory[] { new StubSystemOneClientFactory() },
            NullLogger<SystemOneProviderManager>.Instance);

    [Fact]
    public async Task ReloadAsync_包含SystemOne节_应触发重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "SystemOne" } },
            TestContext.Current.CancellationToken);

        changed.Should().Be(1);
        manager.Providers.Should().ContainSingle(p => p.Id == "typesafe");
    }

    [Fact]
    public async Task ReloadAsync_空节列表_应全量重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        changed.Should().Be(1);
    }

    [Fact]
    public async Task ReloadAsync_其他节_不应重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Other" } },
            TestContext.Current.CancellationToken);

        changed.Should().Be(0);
    }

    [Fact]
    public void ComponentId_应为systemone()
    {
        using var manager = CreateManager();

        new SystemOneReloadHandler(manager).ComponentId.Should().Be("systemone");
    }
}
```

`tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleDiTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Extensions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneModuleDiTests
{
    [Fact]
    public void AddSystemOne_应登记配置节与模块()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSystemOne(registry);

        registry.TryGet(SystemOneConfigStore.SectionName, out var meta).Should().BeTrue();
        meta!.FileName.Should().Be(SystemOneConfigStore.FileName);
        meta.Scope.Should().Be(ConfigScope.UserOnly);
        services
            .Any(d => d.ServiceType == typeof(ISeeingModule) && d.ImplementationInstance is SystemOneModule)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Activate_注册内置Provider_Deactivate_归零()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "test-key");

        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        // 宿主约定：AddSystemOne 须在 AddSeeingCore 之前调用
        services.AddSystemOne(registry);
        services.AddSeeingCore(registry);
        // 用确定性内存存储覆盖 Core 的真实配置存储
        services.AddSingleton<IConfigSectionStore>(new TestConfigSectionStore());

        var module = (SystemOneModule)services
            .Single(d => d.ServiceType == typeof(ISeeingModule)
                         && d.ImplementationInstance is SystemOneModule)
            .ImplementationInstance!;

        await using var provider = services.BuildServiceProvider();
        var providerRegistry = provider.GetRequiredService<ISystemOneProviderRegistry>();

        await module.ActivateAsync(provider);
        providerRegistry.Providers.Should().ContainSingle(p => p.Id == "typesafe");

        await module.DeactivateAsync(provider);
        providerRegistry.Providers.Should().BeEmpty();
    }
}
```

- [ ] **Step 3: 运行构建确认失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: FAIL — `error CS0246: The type or namespace name 'SystemOneModule' could not be found` 及 `'SystemOneReloadHandler'`、`'SystemOneServiceExtensions'`。

- [ ] **Step 4: 实现三个源文件**

`src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneReloadHandler.cs`：

```csharp
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>SystemOne 配置变更重载处理器。</summary>
internal sealed class SystemOneReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly SystemOneProviderManager _manager;

    /// <summary>注入 provider 管理器。</summary>
    public SystemOneReloadHandler(SystemOneProviderManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc />
    public override string ComponentId => "systemone";

    /// <inheritdoc />
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0
            || change.ChangedSections.Contains(SystemOneConfigStore.SectionName))
        {
            _manager.Reload();
        }

        return Task.CompletedTask;
    }
}
```

`src/capabilities/Seeing.Agent.SystemOne/SystemOneModule.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Runtime;

namespace Seeing.Agent.SystemOne;

/// <summary>SystemOne 能力模块 — id=<c>systemone</c>；provider 由 Activate/Deactivate 自管。</summary>
public sealed class SystemOneModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "systemone";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SystemOneConfigStore>();
        services.AddSingleton<ISystemOneClientFactory, TypeSafeSystemOneClientFactory>();
        services.AddSingleton<SystemOneProviderManager>();
        services.AddSingleton<ISystemOneProviderRegistry>(sp => sp.GetRequiredService<SystemOneProviderManager>());
        services.AddSingleton<ISystemOneService, SystemOneService>();
        services.AddSingleton<IReloadHandler, SystemOneReloadHandler>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        services.GetRequiredService<SystemOneProviderManager>().Reload();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        services.GetRequiredService<ISystemOneProviderRegistry>().UnregisterByOwner("systemone");
        return Task.CompletedTask;
    }
}
```

`src/capabilities/Seeing.Agent.SystemOne/Extensions/SystemOneServiceExtensions.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Extensions;

/// <summary>SystemOne 包 DI 注册扩展。</summary>
public static class SystemOneServiceExtensions
{
    /// <summary>
    /// 注册 Seeing.Agent.SystemOne 全部服务并登记 <c>SystemOne</c> 配置节。
    /// 须在 <c>AddSeeingCore</c> 之前调用。
    /// </summary>
    public static IServiceCollection AddSystemOne(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);
        registry.Register(SystemOneConfigStore.SectionMeta);

        var module = new SystemOneModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
```

- [ ] **Step 4b: 删除脚手架占位文件**

Run: `Remove-Item -LiteralPath "src/capabilities/Seeing.Agent.SystemOne/SystemOnePackageMarker.cs"`
说明：Task 1 的占位类已无用途（`SystemOneModule` 等真实源文件已提供可编译内容）；`ScaffoldSmokeTests` 不依赖它。

- [ ] **Step 5: 运行全部 SystemOne 测试确认通过**

Run: `dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests`
Expected: `Build succeeded.` / `0 Error(s)`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneModuleTests"`
Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneReloadHandlerTests"`
Expected: `Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll --TestCaseFilter:"FullyQualifiedName~SystemOneModuleDiTests"`
Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2`

Run: `dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll`
Expected: 全绿（本组累计通过，Failed: 0）。

- [ ] **Step 6: Commit**

```bash
git add src/capabilities/Seeing.Agent.SystemOne/Configuration/SystemOneReloadHandler.cs src/capabilities/Seeing.Agent.SystemOne/SystemOneModule.cs src/capabilities/Seeing.Agent.SystemOne/Extensions/SystemOneServiceExtensions.cs src/capabilities/Seeing.Agent.SystemOne/SystemOnePackageMarker.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneReloadHandlerTests.cs tests/capabilities/Seeing.Agent.SystemOne.Tests/SystemOneModuleDiTests.cs
git commit -m "feat(systemone): 添加重载处理器、模块与 AddSystemOne 入口及 DI 集成测试"
```

---

## 自检与规格覆盖映射

| 规格要求 | 落点 |
|----------|------|
| §4.2 环境变量常量 + Trim/空白→null | Task 7 |
| §4.3 SectionName/FileName/Read/SectionMeta | Task 8 |
| §4.13 内置 typesafe 默认配置工厂 | Task 8 |
| §4.5 延迟创建客户端 + Dispose | Task 9 |
| §4.4 Reload（owner 隔离 / 无 Key 跳过 / 未知 type 跳过 / env 覆盖 / 默认解析 / ProvidersChanged / Dispose） | Task 10 |
| §4.10 门面 + 无 provider 抛 `InvalidOperationException` + `ListModelsAsync(providerId)` | Task 11 |
| §4.11/§4.12 ReloadHandler + Module + `AddSystemOne(registry)` | Task 12 |
| §6 生命周期：Activate `Reload()` / Deactivate `UnregisterByOwner("systemone")` | Task 12（DI 集成测试验证归零） |
| §10 验收 3/4/5/6 | Task 10（无 Key 不阻断）、Task 12（Activate/Deactivate、DI 解析） |

**关键实现不变量（Task 10 已逐条覆盖测试）**：`Reload()` 只重建 `owner == "systemone"` 条目、外部 `Register` 的 provider 原样保留（含同 id 优先）；无 ApiKey 与未知 type 均 `LogWarning` 跳过而非抛出；env 非空时仅覆盖内置/同 id `typesafe` 的 BaseUrl/ApiKey/Model；默认 provider 顺序为 `SYSTEMONE_PROVIDER` > `"typesafe"` > 首个 > null。

> 注：Task 12 Step 1 对测试 csproj 的 `Modify`（追加 `Microsoft.Extensions.DependencyInjection` 包与 `Seeing.Agent.Core` 项目引用）是 DI 集成测试的硬前置——spec §9.1b 的引用集不含 `ServiceCollection` 具体包与 Core，无法编译 `AddSystemOne` + `AddSeeingCore` 组合测试。此做法与既有 `tests/capabilities/Seeing.Agent.Acp.Tests/Seeing.Agent.Acp.Tests.csproj`（同样 `ProjectReference` 了 `Seeing.Agent.Core`）一致；测试工程引用 Core 不违反能力包依赖纪律（纪律仅约束 `src/capabilities/*`）。
