namespace Seeing.ConfigSchema;

/// <summary>
/// 配置字段 Schema（驱动 UI 动态表单）
/// </summary>
/// <param name="Name">属性名（表单回写键）。</param>
/// <param name="Label">显示标签。</param>
/// <param name="Description">说明文案（悬浮提示）。</param>
/// <param name="Type">控件类型。</param>
/// <param name="Required">是否必填。</param>
/// <param name="DefaultValue">默认值（取自 Options 类型的无参实例）。</param>
/// <param name="Choices">
/// 可选候选值。<see cref="ConfigFieldType.Enum"/> 与 <see cref="ConfigFieldType.StringList"/> 用。
/// <para><b>为什么与 <see cref="EnumValues"/> 分开</b>：后者由枚举类型自动推导、不可定制；
/// 而 <see cref="ConfigFieldType.StringList"/> 的候选（如 Provider 能力键）来自领域知识，
/// 只有调用方知道。为 <c>null</c> 时 UI 退回自由输入控件——绝不能渲染成空下拉
/// （那会让用户面对一个没有选项、也无法输入的死框）。</para>
/// </param>
/// <param name="EnumValues">枚举类型的全部取值（仅 <see cref="ConfigFieldType.Enum"/>）。</param>
/// <param name="Section">分组名（表单用 Divider 分段）。</param>
/// <param name="Placeholder">
/// 输入控件的占位文案。<b>空值时的自我说明</b>：对「留空有特殊含义」的字段（如 Provider 的
/// <c>Capabilities</c>——留空表示沿用默认能力），占位文案是用户唯一能看到的解释；
/// 否则一个空下拉看起来就像「这里什么都没有」。
/// </param>
public sealed record ConfigFieldSchema(
    string Name,
    string Label,
    string? Description,
    ConfigFieldType Type,
    bool Required,
    object? DefaultValue,
    IReadOnlyList<string>? EnumValues = null,
    string? Section = null,
    IReadOnlyList<string>? Choices = null,
    string? Placeholder = null);
