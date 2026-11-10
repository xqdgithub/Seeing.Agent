namespace Seeing.Agent.Abstractions.Ui;

/// <summary>模块侧 UI 贡献端口。</summary>
public interface IUiContributor
{
    IReadOnlyList<UiContribution> DescribeUi();
}

/// <summary>宿主自带 UI 贡献端口（进程级，永不被模块启停门控）。</summary>
public interface IUiHostContributor
{
    /// <summary>必须以 <c>host.</c> 开头。</summary>
    string OwnerId { get; }
    IReadOnlyList<UiContribution> DescribeUi();
}
