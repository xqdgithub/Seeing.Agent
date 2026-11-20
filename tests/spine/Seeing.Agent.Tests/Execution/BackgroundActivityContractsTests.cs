using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// 后台活动契约的**形状**约束：会话面与方法归属不得被实现方悄悄放宽
/// （工具只注入会话面，一旦系统面方法漏到 <see cref="IBackgroundActivityManager"/>
/// 上，能力包就能跨会话操作他人的活动）。
/// </summary>
public class BackgroundActivityContractsTests
{
    /// <summary>会话面仅允许这 7 个方法，一个不多一个不少。</summary>
    private static readonly string[] SessionSurfaceMethods =
    [
        "StartAsync",
        "Get",
        "List",
        "CancelAsync",
        "WaitAsync",
        "ReadOutputAsync",
        "MarkResultObserved"
    ];

    /// <summary>绝不允许出现在会话面上的系统级操作。</summary>
    private static readonly string[] ForbiddenOnManager =
    [
        "RegisterLauncher",
        "UnregisterLauncherAsync",
        "TrackAsync",
        "StartAsSystemAsync",
        "CancelBySessionAsync",
        "CancelByExecutionAsync",
        "CancelAllAsync",
        "ReconcileOrphanedActivitiesAsync"
    ];

    private static MethodInfo[] ManagerMethods()
        => typeof(IBackgroundActivityManager).GetMethods().ToArray();

    [Fact]
    public void Manager_ShouldNotExposeSystemOperations()
    {
        var names = ManagerMethods().Select(m => m.Name).ToArray();

        names.Should().BeEquivalentTo(SessionSurfaceMethods,
            "会话面就是这 7 个方法；多一个都是把系统权限漏给工具");
        names.Should().NotContain(ForbiddenOnManager,
            "系统操作只走 IBackgroundActivityControl，不得出现在会话面");
    }

    [Fact]
    public void Manager_Methods_ShouldRequireNonOptionalRequesterSessionId()
    {
        foreach (var method in ManagerMethods())
        {
            var requester = method.GetParameters()
                .SingleOrDefault(p => p.Name == "requesterSessionId");

            requester.Should().NotBeNull(
                $"{method.Name} 必须声明 requesterSessionId（归属校验的唯一依据）");
            requester!.ParameterType.Should().Be<string>(
                $"{method.Name}.requesterSessionId 必须是 string，不得可空");
            requester.HasDefaultValue.Should().BeFalse(
                $"{method.Name}.requesterSessionId 不得有默认值");
            requester.IsOptional.Should().BeFalse(
                $"{method.Name}.requesterSessionId 不得省略");

            var nullability = new NullabilityInfoContext().Create(requester);
            nullability.WriteState.Should().Be(NullabilityState.NotNull,
                $"{method.Name}.requesterSessionId 在可空注解上也不得为 null");
        }
    }

    [Fact]
    public void Control_ShouldExposeSystemOperations()
    {
        var names = typeof(IBackgroundActivityControl).GetMethods()
            .Select(m => m.Name)
            .ToArray();

        names.Should().Contain(ForbiddenOnManager,
            "系统面才是这些操作的唯一归宿");
        names.Should().Contain(nameof(IBackgroundActivityControl.CancelByExecutionAsync),
            "级联中断按执行 id 精确取消");
    }

    [Fact]
    public void StartRequest_ShouldRequireOwnerExecutionId()
    {
        AssertRequiredString<BackgroundActivityStartRequest>(nameof(BackgroundActivityStartRequest.OwnerExecutionId));
        AssertRequiredString<BackgroundActivityStartRequest>(nameof(BackgroundActivityStartRequest.OwnerSessionId));
        AssertRequiredString<BackgroundActivityTrackRequest>(nameof(BackgroundActivityTrackRequest.OwnerExecutionId));
    }

    [Fact]
    public void StartRequest_ShouldCarryPayloadAndDefaultAttributes()
    {
        using var doc = JsonDocument.Parse("""{"command":"echo hi"}""");
        var request = new BackgroundActivityStartRequest
        {
            Kind = "process",
            Title = "build",
            OwnerSessionId = "s1",
            OwnerExecutionId = "e1",
            Payload = doc.RootElement.Clone()
        };

        request.Payload.Should().NotBeNull();
        request.Payload!.Value.GetProperty("command").GetString().Should().Be("echo hi");
        request.Attributes.Should().BeEmpty("Attributes 默认空字典，仅用于展示/诊断");
        request.Origin.Should().Be(BackgroundActivityOrigin.ToolBackground);
        request.Visibility.Should().Be(BackgroundActivityVisibility.Session);
        request.NotifyPolicy.Should().Be(BackgroundActivityNotifyPolicy.OnComplete);
        request.DedupeStrategy.Should().Be(BackgroundActivityDedupeStrategy.Reject);
        request.Lifetime.Should().Be(BackgroundActivityLifetime.Host);
    }

    [Fact]
    public void Event_ShouldImplementMessageEventAndUseActivityTypes()
    {
        var snapshot = new BackgroundActivitySnapshot
        {
            Id = "act_1",
            Kind = "process",
            Title = "build",
            WorkspaceRoot = "C:/ws",
            HostInstanceId = "12345",
            OwnerSessionId = "s1",
            OwnerExecutionId = "e1"
        };

        var evt = new BackgroundActivityEvent
        {
            SessionId = "s1",
            Type = ActivityEventTypes.Completed,
            Activity = snapshot
        };

        evt.Should().BeAssignableTo<IMessageEvent>();
        evt.Activity.Should().BeSameAs(snapshot);
        ActivityEventTypes.Started.Should().Be("activity.started");
        ActivityEventTypes.Progress.Should().Be("activity.progress");
        ActivityEventTypes.Completed.Should().Be("activity.completed");
        ActivityEventTypes.Failed.Should().Be("activity.failed");
        ActivityEventTypes.Cancelled.Should().Be("activity.cancelled");
    }

    /// <summary>断言属性以 C# <c>required</c> 修饰、类型为 string 且可空注解为 NotNull。</summary>
    private static void AssertRequiredString<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName);
        property.Should().NotBeNull($"{typeof(T).Name}.{propertyName} 必须存在");
        property!.PropertyType.Should().Be<string>();

        property.GetCustomAttribute<RequiredMemberAttribute>().Should().NotBeNull(
            $"{typeof(T).Name}.{propertyName} 必须声明为 required");

        var nullability = new NullabilityInfoContext().Create(property);
        nullability.WriteState.Should().Be(NullabilityState.NotNull,
            $"{typeof(T).Name}.{propertyName} 不得为 null");
    }
}
