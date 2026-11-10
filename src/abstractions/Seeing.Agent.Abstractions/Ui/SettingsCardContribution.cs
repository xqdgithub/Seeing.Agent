namespace Seeing.Agent.Abstractions.Ui;

/// <summary>设置页卡片贡献。</summary>
public sealed record SettingsCardContribution : UiContribution
{
    public required string Title { get; init; }
    public required string ViewKey { get; init; }
}

/// <summary>命名插槽条目。</summary>
public sealed record SlotContribution : UiContribution
{
    public required string ViewKey { get; init; }
    public string? DisplayName { get; init; }
}
