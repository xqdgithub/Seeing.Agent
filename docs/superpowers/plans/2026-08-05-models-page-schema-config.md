# Models Page Schema Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract `Seeing.ConfigSchema`, add `IConfigurableLlmProvider`, and dual-track the Models page connection form (handcrafted built-in vs schema-driven extensions).

**Architecture:** Shared pure schema library feeds Gateway + Agent. Optional `IConfigurableLlmProvider` on providers; `ConfiguredLlmProvider` implements it and persists via existing `seeing.json` path. WebUI discovers configurability only through `IProviderManager.TryGetConfigurable` and reuses `SchemaDrivenConfigForm`.

**Tech Stack:** .NET 10, xUnit v3, FluentAssertions, Moq, Blazor Server + AntDesign, existing `UnifiedConfigManager` / `ConfigLevel`.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-05-models-page-schema-config-design.md`
- Target framework: `net10.0`
- No old-namespace compatibility / type-forwarding
- Built-in UI stays handcrafted (`GetConfigSchema()` returns `null`)
- Extension persistence is self-managed; built-in writes `Providers[id]`
- WebUI must not inject `IProviderRegistry`
- **Do not git commit** unless the user explicitly asks (replace any habit of committing after tasks)

---

## File Structure

| Path | Responsibility |
|------|----------------|
| `src/Seeing.ConfigSchema/Seeing.ConfigSchema.csproj` | New library project |
| `src/Seeing.ConfigSchema/ConfigFieldSchema.cs` | Field schema record (moved) |
| `src/Seeing.ConfigSchema/ConfigFieldType.cs` | Field type enum (moved) |
| `src/Seeing.ConfigSchema/OptionsSchemaBuilder.cs` | Reflection schema builder (moved) |
| `tests/Seeing.ConfigSchema.Tests/*` | Builder unit tests |
| `src/Seeing.Agent/Llm/IConfigurableLlmProvider.cs` | Optional config contract |
| `src/Seeing.Agent/Llm/ConfiguredLlmProvider.cs` | Implement Load/Save; schema null |
| `src/Seeing.Agent/Llm/IProviderManager.cs` | Add `TryGetConfigurable` |
| `src/Seeing.Agent/Llm/ProviderManager.cs` | Wire save callback + TryGetConfigurable |
| `samples/.../Components/ConfigSchema/*.razor` | Moved shared form components |
| `samples/.../Pages/ModelsPage.razor` | Dual-track UI + detail save |

Delete after move: `src/Seeing.Gateway/Configuration/ConfigFieldSchema.cs`, `ConfigFieldType.cs`, `OptionsSchemaBuilder.cs`.

---

### Task 1: Create `Seeing.ConfigSchema` + OptionsSchemaBuilder tests

**Files:**
- Create: `src/Seeing.ConfigSchema/Seeing.ConfigSchema.csproj`
- Create: `src/Seeing.ConfigSchema/ConfigFieldType.cs`
- Create: `src/Seeing.ConfigSchema/ConfigFieldSchema.cs`
- Create: `src/Seeing.ConfigSchema/OptionsSchemaBuilder.cs` (copy from Gateway; change namespace to `Seeing.ConfigSchema`)
- Create: `tests/Seeing.ConfigSchema.Tests/Seeing.ConfigSchema.Tests.csproj`
- Create: `tests/Seeing.ConfigSchema.Tests/OptionsSchemaBuilderTests.cs`
- Modify: `Seeing.Agent.slnx` (add both projects under `/src/` and `/tests/`)

**Interfaces:**
- Consumes: none
- Produces: `Seeing.ConfigSchema.ConfigFieldSchema`, `ConfigFieldType`, `OptionsSchemaBuilder.FromType(Type)`

- [ ] **Step 1: Create library csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.ConfigSchema</RootNamespace>
    <AssemblyName>Seeing.ConfigSchema</AssemblyName>
    <PackageId>Seeing.ConfigSchema</PackageId>
    <Version>1.0.0</Version>
    <Authors>Seeing</Authors>
    <Description>Shared config field schema for schema-driven forms</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Move types into `Seeing.ConfigSchema` namespace**

Copy the three Gateway files verbatim except `namespace Seeing.ConfigSchema;`. Keep `ConfigFieldSchema` record shape and `OptionsSchemaBuilder` behavior identical.

- [ ] **Step 3: Create test project**

Mirror `tests/Seeing.Gateway.Tests/Seeing.Gateway.Tests.csproj` but:
- `RootNamespace`: `Seeing.ConfigSchema.Tests`
- `ProjectReference`: `..\..\src\Seeing.ConfigSchema\Seeing.ConfigSchema.csproj`
- Packages: `Microsoft.NET.Test.Sdk`, `xunit.v3`, `xunit.runner.visualstudio`, `coverlet.collector`, `FluentAssertions`

Add both projects to `Seeing.Agent.slnx`.

- [ ] **Step 4: Write failing/characterization tests for `OptionsSchemaBuilder`**

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Seeing.ConfigSchema;
using Xunit;

namespace Seeing.ConfigSchema.Tests;

public class OptionsSchemaBuilderTests
{
    private sealed class SampleOptions
    {
        public bool Enabled { get; set; } = true;
        public string SectionName { get; set; } = "x";

        [Display(Name = "Bot Token", GroupName = "Auth")]
        [Required]
        public string ApiKey { get; set; } = "";

        [Display(Name = "Endpoint")]
        public string BaseUrl { get; set; } = "https://example.com";

        public int TimeoutMs { get; set; } = 30;

        public bool Verbose { get; set; }

        public SampleMode Mode { get; set; } = SampleMode.Fast;

        public List<string> Tags { get; set; } = new();

        [Browsable(false)]
        public string Hidden { get; set; } = "no";

        public Dictionary<string, string>? Unsupported { get; set; }
    }

    private enum SampleMode { Fast, Slow }

    [Fact]
    public void FromType_SkipsEnabledSectionNameBrowsableAndUnsupported()
    {
        var fields = OptionsSchemaBuilder.FromType(typeof(SampleOptions));
        fields.Select(f => f.Name).Should().NotContain(new[] { "Enabled", "SectionName", "Hidden", "Unsupported" });
    }

    [Fact]
    public void FromType_InfersSecretUrlNumberBooleanEnumStringList()
    {
        var map = OptionsSchemaBuilder.FromType(typeof(SampleOptions)).ToDictionary(f => f.Name);
        map["ApiKey"].Type.Should().Be(ConfigFieldType.Secret);
        map["ApiKey"].Required.Should().BeTrue();
        map["ApiKey"].Section.Should().Be("Auth");
        map["BaseUrl"].Type.Should().Be(ConfigFieldType.Url);
        map["TimeoutMs"].Type.Should().Be(ConfigFieldType.Number);
        map["Verbose"].Type.Should().Be(ConfigFieldType.Boolean);
        map["Mode"].Type.Should().Be(ConfigFieldType.Enum);
        map["Mode"].EnumValues.Should().Equal("Fast", "Slow");
        map["Tags"].Type.Should().Be(ConfigFieldType.StringList);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Seeing.ConfigSchema.Tests --filter "FullyQualifiedName~OptionsSchemaBuilderTests"`

Expected: PASS (types already implemented in Step 2).

- [ ] **Step 6: Skip commit** (user constraint: no git commit)

---

### Task 2: Retarget Gateway family + WebUI to `Seeing.ConfigSchema`

**Files:**
- Modify: `src/Seeing.Gateway/Seeing.Gateway.csproj` — add ProjectReference to ConfigSchema
- Modify: `src/Seeing.Agent/Seeing.Agent.csproj` — add ProjectReference to ConfigSchema
- Modify: `src/Seeing.Gateway/Plugins/IGatewayChannelPlugin.cs` — `using Seeing.ConfigSchema`
- Modify: `src/Seeing.Gateway/Plugins/GatewayChannelTypeInfo.cs` — same
- Modify: `src/Seeing.Gateway.QQ/QQChannelPlugin.cs` — same
- Modify: `src/Seeing.Gateway.WeCom/WeComChannelPlugin.cs` — same
- Modify: `src/Seeing.Agent.Gateway/Channels/GatewayChannelRegistry.cs` — same if it imports schema types
- Modify: `samples/Seeing.Agent.WebUI/Components/GatewayClients/SchemaDrivenConfigForm.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/GatewayClients/SchemaFieldEditor.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/GatewayClientConfigService.cs` — replace fully-qualified `Seeing.Gateway.Configuration.ConfigFieldSchema` with `Seeing.ConfigSchema.ConfigFieldSchema`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` — add ProjectReference to ConfigSchema if not transitive
- Delete: `src/Seeing.Gateway/Configuration/ConfigFieldSchema.cs`
- Delete: `src/Seeing.Gateway/Configuration/ConfigFieldType.cs`
- Delete: `src/Seeing.Gateway/Configuration/OptionsSchemaBuilder.cs`

**Interfaces:**
- Consumes: Task 1 types
- Produces: Gateway/WebUI compile against `Seeing.ConfigSchema` only

- [ ] **Step 1: Add project references**

`Seeing.Gateway.csproj` and `Seeing.Agent.csproj`:

```xml
<ProjectReference Include="..\Seeing.ConfigSchema\Seeing.ConfigSchema.csproj" />
```

WebUI: add the same relative path from `samples/Seeing.Agent.WebUI` (`../../src/Seeing.ConfigSchema/Seeing.ConfigSchema.csproj`).

- [ ] **Step 2: Replace usings and delete old files**

Replace every `using Seeing.Gateway.Configuration;` that only existed for schema types with `using Seeing.ConfigSchema;`. Then delete the three old Gateway Configuration files. Grep to confirm zero remaining `Seeing.Gateway.Configuration.ConfigField` / `OptionsSchemaBuilder` references:

Run: `rg "Seeing\\.Gateway\\.Configuration|OptionsSchemaBuilder|ConfigFieldSchema" -g "*.cs" -g "*.razor"`

Expected: only unrelated Gateway.Configuration hits (none for these types).

- [ ] **Step 3: Build affected projects**

Run: `dotnet build src/Seeing.Gateway/Seeing.Gateway.csproj`
Run: `dotnet build src/Seeing.Gateway.QQ/Seeing.Gateway.QQ.csproj`
Run: `dotnet build src/Seeing.Gateway.WeCom/Seeing.Gateway.WeCom.csproj`
Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`

Expected: 0 errors.

- [ ] **Step 4: Re-run ConfigSchema tests**

Run: `dotnet test tests/Seeing.ConfigSchema.Tests`

Expected: PASS.

- [ ] **Step 5: Skip commit**

---

### Task 3: `IConfigurableLlmProvider` + `ConfiguredLlmProvider` Load/Save

**Files:**
- Create: `src/Seeing.Agent/Llm/IConfigurableLlmProvider.cs`
- Modify: `src/Seeing.Agent/Llm/ConfiguredLlmProvider.cs`
- Modify: `tests/Seeing.Agent.Tests/Llm/ConfiguredLlmProviderTests.cs`

**Interfaces:**
- Consumes: `Seeing.ConfigSchema.ConfigFieldSchema`, `Seeing.Agent.Configuration.ConfigLevel`
- Produces:

```csharp
public interface IConfigurableLlmProvider
{
    IReadOnlyList<ConfigFieldSchema>? GetConfigSchema();
    Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default);
}
```

Dictionary keys for built-in: `Name`, `Type`, `BaseUrl`, `ApiKey`, `Timeout`, `MaxRetries`, `DefaultModel`. Never include `Models` / `Headers` / `Options` in this dictionary round-trip unless already edited via those keys (do not wipe `Models` on save).

- [ ] **Step 1: Write failing tests first**

Add to `ConfiguredLlmProviderTests.cs`:

```csharp
[Fact]
public void GetConfigSchema_ReturnsNull()
{
    var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object);
    ((IConfigurableLlmProvider)sut).GetConfigSchema().Should().BeNull();
}

[Fact]
public async Task LoadConfigAsync_MapsConnectionFields()
{
    var config = new ProviderConfig
    {
        Id = "openai",
        Name = "OpenAI",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "sk-test",
        Timeout = 1000,
        MaxRetries = 2,
        DefaultModel = "gpt-4o",
        Models = new Dictionary<string, ModelConfig> { ["gpt-4o"] = new() { Id = "gpt-4o" } }
    };
    var sut = CreateProvider(CreateFactory(Mock.Of<ILlmClient>()).Object, config);

    var values = await ((IConfigurableLlmProvider)sut).LoadConfigAsync(TestContext.Current.CancellationToken);

    values.Keys.Should().NotContain("Models");
    values["Name"].Should().Be("OpenAI");
    values["Type"].Should().Be(nameof(ProviderType.OpenAI));
    values["BaseUrl"].Should().Be(config.BaseUrl);
    values["ApiKey"].Should().Be("sk-test");
    values["Timeout"].Should().Be(1000);
    values["MaxRetries"].Should().Be(2);
    values["DefaultModel"].Should().Be("gpt-4o");
}

[Fact]
public async Task SaveConfigAsync_PerservesModelsAndInvokesSaveCallback()
{
    ProviderConfig? saved = null;
    ConfigLevel? savedLevel = null;
    var config = new ProviderConfig
    {
        Id = "openai",
        Type = ProviderType.OpenAI,
        Models = new Dictionary<string, ModelConfig> { ["m"] = new() { Id = "m" } }
    };
    var sut = CreateProvider(
        CreateFactory(Mock.Of<ILlmClient>()).Object,
        config,
        saveAsync: (cfg, level, _) =>
        {
            saved = cfg;
            savedLevel = level;
            return Task.CompletedTask;
        });

    await ((IConfigurableLlmProvider)sut).SaveConfigAsync(
        new Dictionary<string, object?>
        {
            ["Name"] = "Renamed",
            ["Type"] = nameof(ProviderType.Anthropic),
            ["BaseUrl"] = "https://example.com",
            ["ApiKey"] = "k",
            ["Timeout"] = 5000,
            ["MaxRetries"] = 9,
            ["DefaultModel"] = "x"
        },
        ConfigLevel.User,
        TestContext.Current.CancellationToken);

    saved.Should().NotBeNull();
    saved!.Name.Should().Be("Renamed");
    saved.Type.Should().Be(ProviderType.Anthropic);
    saved.Models.Should().ContainKey("m");
    savedLevel.Should().Be(ConfigLevel.User);
}
```

Update helper:

```csharp
private static ConfiguredLlmProvider CreateProvider(
    ILlmClientFactory factory,
    ProviderConfig? config = null,
    Func<ProviderConfig, ConfigLevel, CancellationToken, Task>? saveAsync = null)
    => new(
        config ?? new ProviderConfig { Id = "configured" },
        factory,
        NullLogger.Instance,
        saveAsync ?? ((_, _, _) => Task.CompletedTask));
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ConfiguredLlmProviderTests.GetConfigSchema|FullyQualifiedName~ConfiguredLlmProviderTests.LoadConfigAsync|FullyQualifiedName~ConfiguredLlmProviderTests.SaveConfigAsync"`

Expected: FAIL (interface / ctor / methods missing).

- [ ] **Step 3: Implement interface + provider methods**

`IConfigurableLlmProvider.cs` as above (`using Seeing.ConfigSchema;` + `using Seeing.Agent.Configuration;`).

`ConfiguredLlmProvider`:
- Implement `IConfigurableLlmProvider`
- Add ctor param: `Func<ProviderConfig, ConfigLevel, CancellationToken, Task> saveAsync`
- `GetConfigSchema()` → `null`
- `LoadConfigAsync`: map fields listed above; `Type` as enum name string
- `SaveConfigAsync`: clone current config (including `Models`/`Headers`/`Options`), apply dictionary values with tolerant parsing (`Type` parse enum; numbers from `int`/`long`/`double`/`JsonElement`), then `await _saveAsync(updated, level, ct)`. Update in-memory `_config` fields used by `Name`/`MaxRetries` **or** replace `_config` reference with the updated clone so subsequent Load sees new values. Prefer keeping a mutable `_config` field updated after save.

Parsing helpers must not throw on missing keys (leave previous value).

- [ ] **Step 4: Fix `ProviderManager.RegisterConfiguredProvider` call site**

```csharp
var provider = new ConfiguredLlmProvider(
    config,
    _clientFactory,
    _logger,
    saveAsync: (cfg, level, token) => SaveProviderAsync(cfg.Id, cfg, level, token));
```

- [ ] **Step 5: Run tests — expect PASS**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ConfiguredLlmProviderTests"`

Expected: all PASS.

- [ ] **Step 6: Skip commit**

---

### Task 4: `IProviderManager.TryGetConfigurable`

**Files:**
- Modify: `src/Seeing.Agent/Llm/IProviderManager.cs`
- Modify: `src/Seeing.Agent/Llm/ProviderManager.cs`
- Modify: `tests/Seeing.Agent.Tests/Llm/ProviderManagerTests.cs` (or create methods there)

**Interfaces:**
- Consumes: `IConfigurableLlmProvider`, `IProviderRegistry.GetProvider`
- Produces:

```csharp
bool TryGetConfigurable(string providerId, out IConfigurableLlmProvider? configurable);
```

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void TryGetConfigurable_ConfiguredProvider_ReturnsTrue()
{
    // arrange: ProviderManager with one Configured provider registered via options/refresh path used in existing tests
    var ok = manager.TryGetConfigurable("openai", out var configurable);
    ok.Should().BeTrue();
    configurable.Should().NotBeNull();
    configurable!.GetConfigSchema().Should().BeNull();
}

[Fact]
public void TryGetConfigurable_UnknownId_ReturnsFalse()
{
    var ok = manager.TryGetConfigurable("missing", out var configurable);
    ok.Should().BeFalse();
    configurable.Should().BeNull();
}

[Fact]
public void TryGetConfigurable_ExtensionWithoutInterface_ReturnsFalse()
{
    // register a plain ILlmProvider mock (not IConfigurable) via registry used by manager
    var ok = manager.TryGetConfigurable("ext", out var configurable);
    ok.Should().BeFalse();
    configurable.Should().BeNull();
}
```

Reuse existing `ProviderManagerTests` fixtures/helpers; if registry is injectable, register a non-configurable fake provider for the third case.

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~TryGetConfigurable"`

Expected: FAIL (method missing).

- [ ] **Step 3: Implement**

On `IProviderManager` and `ProviderManager`:

```csharp
public bool TryGetConfigurable(string providerId, out IConfigurableLlmProvider? configurable)
{
    configurable = null;
    var provider = _registry.GetProvider(providerId);
    if (provider is IConfigurableLlmProvider c)
    {
        configurable = c;
        return true;
    }
    return false;
}
```

- [ ] **Step 4: Run tests — expect PASS**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ProviderManagerTests"`

Expected: PASS.

- [ ] **Step 5: Skip commit**

---

### Task 5: Models page dual-track UI + move shared form components

**Files:**
- Create/Move: `samples/Seeing.Agent.WebUI/Components/ConfigSchema/SchemaDrivenConfigForm.razor`
- Create/Move: `samples/Seeing.Agent.WebUI/Components/ConfigSchema/SchemaFieldEditor.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/GatewayClients/GatewayClientDrawer.razor` — update component path/namespace usings
- Delete old: `samples/Seeing.Agent.WebUI/Components/GatewayClients/SchemaDrivenConfigForm.razor` + `SchemaFieldEditor.razor` after move
- Modify: `samples/Seeing.Agent.WebUI/Pages/ModelsPage.razor`

**Interfaces:**
- Consumes: `IProviderManager.TryGetConfigurable`, `IConfigurableLlmProvider`, `SchemaDrivenConfigForm`
- Produces: Models page behavior per spec §3

- [ ] **Step 1: Move Blazor components to `Components/ConfigSchema/`**

Keep code identical; ensure `@using Seeing.ConfigSchema`. Update `GatewayClientDrawer.razor` to reference the moved component (same type name is fine in new folder).

- [ ] **Step 2: Rewrite Models connection panel logic**

State additions:

```csharp
private IConfigurableLlmProvider? _configurable;
private IReadOnlyList<ConfigFieldSchema>? _schemaFields;
private Dictionary<string, object?> _schemaValues = new();
private SchemaDrivenConfigForm? _schemaForm;
private bool _savingProviderConfig;
private string? _configLoadError;
```

On `SelectProvider`:

1. Reset `_configLoadError`, `_schemaFields`, `_schemaValues`, `_configurable`
2. Load `_currentProviderInfo` as today
3. If `ProviderManager.TryGetConfigurable(id, out var c)`:
   - `_configurable = c`
   - `_schemaFields = c.GetConfigSchema()`
   - If `_schemaFields is { Count: > 0 }`: `LoadConfigAsync` into `_schemaValues` (try/catch → set `_configLoadError`)
4. Else if Configured: keep loading `_currentProvider` from `ConfigManager.SeeingAgent.Providers` for handcrafted form
5. Else: read-only branch

Render:

```razor
@if (!string.IsNullOrEmpty(_configLoadError))
{
    <Alert Type="@AlertType.Error" Message="@_configLoadError" ShowIcon="true" />
}
else if (_schemaFields is { Count: > 0 })
{
    <SchemaDrivenConfigForm @ref="_schemaForm" Fields="_schemaFields" Values="_schemaValues" />
    <Button Type="@ButtonType.Primary" Loading="_savingProviderConfig" OnClick="SaveProviderConfigAsync">
        保存配置
    </Button>
}
else if (_currentProvider is not null)
{
    <!-- existing handcrafted form fields -->
    <Button Type="@ButtonType.Primary" Loading="_savingProviderConfig" OnClick="SaveProviderConfigAsync">
        保存配置
    </Button>
}
else
{
    <!-- existing read-only Descriptions + alert -->
}
```

`SaveProviderConfigAsync`:

```csharp
private async Task SaveProviderConfigAsync()
{
    if (_configurable is null) return;
    _savingProviderConfig = true;
    try
    {
        if (_schemaFields is { Count: > 0 })
        {
            if (_schemaForm is null || !_schemaForm.TryValidate(out var err))
            {
                MessageService.Error(err ?? "请检查配置");
                return;
            }
            await _configurable.SaveConfigAsync(_schemaValues, _saveLevel);
        }
        else if (_currentProvider is not null)
        {
            var values = new Dictionary<string, object?>
            {
                ["Name"] = _currentProvider.Name,
                ["Type"] = _currentProvider.Type.ToString(),
                ["BaseUrl"] = _currentProvider.BaseUrl,
                ["ApiKey"] = _currentProvider.ApiKey,
                ["Timeout"] = _currentProvider.Timeout,
                ["MaxRetries"] = _currentProvider.MaxRetries,
                ["DefaultModel"] = _currentProvider.DefaultModel
            };
            await _configurable.SaveConfigAsync(values, _saveLevel);
        }
        MessageService.Success("Provider 配置已保存");
        SelectProvider(_selectedProviderId);
    }
    catch (Exception ex)
    {
        MessageService.Error($"保存失败: {ex.Message}");
    }
    finally
    {
        _savingProviderConfig = false;
    }
}
```

Page chrome:

- Keep `ConfigLevel` select in Extra
- **Remove** Extra 「保存」 button that called `SaveOptions` / `SaveProviderAsync`
- Delete `SaveOptions` if unused
- Keep model CRUD / test connection / set default using `_saveLevel`

`CanEditCurrentProvider` for model table: keep Configured-only rule (extensions still non-editable model CRUD unless already allowed — do not expand scope).

- [ ] **Step 3: Build WebUI**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`

Expected: 0 errors.

- [ ] **Step 4: Regression tests**

Run:
- `dotnet test tests/Seeing.ConfigSchema.Tests`
- `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ConfiguredLlmProviderTests|FullyQualifiedName~ProviderManagerTests|FullyQualifiedName~ModelCatalog"`
- `dotnet build Seeing.Agent.slnx` (or at least Gateway + Agent + WebUI)

Expected: PASS / 0 errors.

- [ ] **Step 5: Manual check list (no automation required)**

1. Open `/models`, edit built-in provider, 「保存配置」, confirm `seeing.json` `Providers[id]` updated at selected level
2. Confirm page-top no longer has Provider save button; ConfigLevel still present
3. Open Gateway client drawer; schema form still works
4. Extension without interface still read-only

- [ ] **Step 6: Skip commit**

---

## Spec Coverage Checklist

| Spec requirement | Task |
|------------------|------|
| `Seeing.ConfigSchema` library | 1 |
| Migrate Gateway schema types, no forwarding | 2 |
| `IConfigurableLlmProvider` | 3 |
| `ConfiguredLlmProvider` schema null + seeing.json save | 3 |
| `TryGetConfigurable` / no Registry in WebUI | 4–5 |
| Dual-track Models UI + detail save | 5 |
| Remove page-top Provider save | 5 |
| Move/reuse SchemaDrivenConfigForm | 5 |
| Model list CRUD unchanged | 5 (explicit non-change) |
| Acceptance / build | 5 Step 4–5 |

## Self-Review Notes

- No TBD placeholders
- `ConfiguredLlmProvider` ctor gains required `saveAsync` — all call sites updated in Task 3
- Dictionary key names consistent across Load/Save/ModelsPage mapping
- Commit steps intentionally skipped per user constraint
