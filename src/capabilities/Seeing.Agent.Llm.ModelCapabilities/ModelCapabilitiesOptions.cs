namespace Seeing.Agent.Llm.ModelCapabilities;

/// <summary>
/// 系统级模型能力编排配置（节名 <c>ModelCapabilities</c>）。
/// </summary>
public sealed class ModelCapabilitiesOptions
{
    public const string SectionName = "ModelCapabilities";

    /// <summary>总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>["*"]</c> 或 ProviderId 白名单</summary>
    public List<string> Providers { get; set; } = ["*"];

    /// <summary>变更时是否默认刷新聚合模型目录（仅作 Notify 默认值）</summary>
    public bool InvalidateModelCatalogOnChange { get; set; }

    public List<ModelCapabilitySourceOptions> Sources { get; set; } = [];
}

public sealed class ModelCapabilitySourceOptions
{
    public string Id { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}
