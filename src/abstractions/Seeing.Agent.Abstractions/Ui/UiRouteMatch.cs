namespace Seeing.Agent.Abstractions.Ui;

/// <summary>一次路由匹配结果。</summary>
public sealed record UiRouteMatch(
    PageContribution Page,
    IReadOnlyDictionary<string, object?> Parameters);
