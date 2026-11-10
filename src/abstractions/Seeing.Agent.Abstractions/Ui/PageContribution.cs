namespace Seeing.Agent.Abstractions.Ui;

/// <summary>侧栏导航位。</summary>
public sealed record NavPlacement
{
    public required string Icon { get; init; }
    public string? Group { get; init; }
    public string? GroupIcon { get; init; }
    public int GroupOrder { get; init; }
}

/// <summary>页面贡献（合并旧 NavContribution + RouteContribution）。</summary>
public sealed record PageContribution : UiContribution
{
    /// <summary>路由模板；占位名 <c>{name}</c> 会逐字成为参数键。</summary>
    public required string Route { get; init; }
    public required string Title { get; init; }
    public required string ViewKey { get; init; }

    /// <summary>侧栏位；null = 纯路由不进侧栏。</summary>
    public NavPlacement? Nav { get; init; }
}
