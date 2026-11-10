using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Ui;

/// <summary>
/// 守门：Core 的 UI 实现层不得引用 Web 类型。
/// <para>
/// 判定与匹配（可见性 / 路由 / 注册表）必须能被无界面宿主复用，
/// 一旦此处混入 <c>Microsoft.AspNetCore.*</c> / <c>IComponent</c> / <c>RenderFragment</c>，
/// Core 就会被绑死在 Blazor 上（视图解析属宿主职责）。
/// </para>
/// </summary>
public class CoreUiLayerInvariantTests
{
    [Fact]
    public void CoreUiNamespace_MustNotReferenceAspNetCore()
    {
        var coreAssembly = typeof(Seeing.Agent.Core.Ui.UiVisibility).Assembly;

        var offenders = coreAssembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Seeing.Agent.Core.Ui", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m switch
            {
                PropertyInfo p => [p.PropertyType],
                FieldInfo f => [f.FieldType],
                MethodInfo mi => mi.GetParameters().Select(p => p.ParameterType)
                    .Append(mi.ReturnType).ToArray(),
                _ => Type.EmptyTypes
            })
            .Where(t => t.FullName?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty("Core 的 UI 实现不得引用 ASP.NET Core 类型");
    }
}
