namespace Seeing.Agent.Abstractions.Ui;

/// <summary>视图键目录：只答有没有。</summary>
public interface IUiViewKeyCatalog
{
    bool CanResolve(string viewKey);
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>宿主侧贡献校验器。</summary>
public interface IUiContributionValidator
{
    string? Validate(UiContribution contribution, UiPoint point);
}
