using System.Reflection;
using AntDesign;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

/// <summary>
/// AntDesign 组件参数守卫。
/// <para>
/// 为什么需要它：Razor 编译期不校验组件参数，写错只会在渲染那一刻抛
/// 「Object of type 'AntDesign.X' does not have a property matching the name 'Y'」，
/// 而 Blazor Server 会因此把整个 circuit 打掉（用户看到白屏）。SSR 页面测试也覆盖不到
/// 只在弹窗里渲染的组件，所以用反射把「我们实际用到的参数」钉死。
/// </para>
/// </summary>
public class AntDesignParameterGuardTests
{
    private static readonly Dictionary<Type, string[]> s_expectations = new()
    {
        // 工作区首页 / 侧栏切换 / 目录选择用到的参数
        [typeof(Button)] = ["Size", "Icon", "OnClick", "Type", "Block", "Loading", "Disabled", "Shape"],
        [typeof(Tooltip)] = ["Title"],
        [typeof(Spin)] = ["Spinning", "Tip"],
        [typeof(Empty)] = ["Description"],
        [typeof(Alert)] = ["Type", "Message", "ShowIcon"],
        [typeof(Modal)] = ["Visible", "Title", "Centered", "Width", "OnOk", "OnCancel", "OkText"],
        [typeof(Dropdown)] = ["Placement"],
        [typeof(Menu)] = ["Mode"],
        [typeof(MenuItem)] = ["Key", "OnClick"],
        [typeof(Icon)] = ["Type"],
    };

    [Fact]
    public void UsedAntDesignParameters_Should_Exist()
    {
        var missing = new List<string>();

        foreach (var (componentType, parameterNames) in s_expectations)
        {
            var available = componentType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<ParameterAttribute>() is not null
                            || p.GetCustomAttribute<CascadingParameterAttribute>() is not null)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in parameterNames)
            {
                if (!available.Contains(name))
                    missing.Add($"{componentType.Name}.{name}");
            }
        }

        missing.Should().BeEmpty("这些参数在组件里不存在，渲染时会直接抛异常并打掉 circuit：{0}",
            string.Join(", ", missing));
    }
}

