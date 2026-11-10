using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>载荷种类 ↔ 类型映射（类型参数不能做 switch case，只能查表）。</summary>
public static class UiPayloadKindMap
{
    private static readonly Dictionary<Type, UiPayloadKind> s_map = new()
    {
        [typeof(PageContribution)] = UiPayloadKind.Page,
        [typeof(SettingsCardContribution)] = UiPayloadKind.SettingsCard,
        [typeof(SlotContribution)] = UiPayloadKind.Slot
    };

    public static bool TryGet(Type contributionType, out UiPayloadKind kind)
    {
        for (var t = contributionType; t is not null && t != typeof(UiContribution); t = t.BaseType)
        {
            if (s_map.TryGetValue(t, out kind))
                return true;
        }

        kind = default;
        return false;
    }
}
