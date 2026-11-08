using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 界面文案口径守门：术语唯一、状态值不直出枚举、单位与标点统一。
/// <para>
/// 这些断言锁的是「说法」而不是「排版」。任何一页自行发明一套译名都会在这里变红，
/// 从而避免同一种状态在 MCP 页、首页、状态面板出现三种写法。
/// </para>
/// </summary>
public class UiTextTests
{
    // ===== 术语唯一 =====

    [Fact]
    public void Terms_ShouldUseSingleChineseWording()
    {
        UiText.Agent.Should().Be("智能体");
        UiText.Session.Should().Be("会话");
        UiText.Scenario.Should().Be("工作模式");
        UiText.Conversation.Should().Be("对话");
        UiText.Tool.Should().Be("工具");
        UiText.Skill.Should().Be("技能");
    }

    [Fact]
    public void EnabledLabels_ShouldBeConsistent()
    {
        UiText.Enabled.Should().Be("已启用");
        UiText.Disabled.Should().Be("已禁用");
    }

    [Fact]
    public void Ellipsis_ShouldBeFullWidthDoubleEllipsis()
        => UiText.Ellipsis.Should().Be("……");

    /// <summary>
    /// 会话级思考强度的「未指定」项不得写作「默认」——
    /// 那与模型配置里的「默认档」撞词，用户分不清是在改会话还是改模型。
    /// </summary>
    [Fact]
    public void ThinkingEffortUnspecified_ShouldNotCollideWithModelDefault()
    {
        UiText.ThinkingEffortUnspecified.Should().Be("不指定");
        UiText.ThinkingEffortUnspecified.Should().NotBe("默认");
    }

    // ===== 状态不直出枚举 =====

    [Theory]
    [InlineData(JobState.Scheduled, "已调度")]
    [InlineData(JobState.Running, "运行中")]
    [InlineData(JobState.Paused, "已暂停")]
    [InlineData(JobState.Disabled, "已禁用")]
    public void JobStateText_ShouldMapToChinese(JobState state, string expected)
        => UiText.JobStateText(state).Should().Be(expected);

    [Fact]
    public void JobStateText_ExpiredScheduled_ShouldPreferExpired()
        => UiText.JobStateText(JobState.Scheduled, expired: true).Should().Be("已过期");

    [Fact]
    public void JobStateText_ExpiredNonScheduled_ShouldKeepState()
        => UiText.JobStateText(JobState.Running, expired: true).Should().Be("运行中");

    [Theory]
    [InlineData("cron", "定时任务")]
    [InlineData("heartbeat", "心跳")]
    [InlineData("manual", "手动触发")]
    public void ScheduleSource_ShouldMapInternalValues(string source, string expected)
        => UiText.ScheduleSource(source).Should().Be(expected);

    [Fact]
    public void ScheduleSource_Unknown_ShouldFallBackToRaw()
        => UiText.ScheduleSource("custom").Should().Be("custom");

    [Fact]
    public void ScheduleSource_Null_ShouldBePlaceholder()
        => UiText.ScheduleSource(null).Should().Be(UiText.None);

    [Theory]
    [InlineData("agent", "智能体任务")]
    [InlineData("text", "文本消息")]
    public void ScheduleTaskType_ShouldMapInternalValues(string taskType, string expected)
        => UiText.ScheduleTaskType(taskType).Should().Be(expected);

    [Theory]
    [InlineData("cron", "Cron 表达式")]
    [InlineData("interval", "固定间隔")]
    [InlineData("once", "一次性")]
    public void ScheduleKind_ShouldMapInternalValues(string type, string expected)
        => UiText.ScheduleKind(type).Should().Be(expected);

    [Theory]
    [InlineData("success", "成功")]
    [InlineData("failed", "失败")]
    [InlineData("running", "运行中")]
    public void ExecutionStatus_ShouldMapInternalValues(string status, string expected)
        => UiText.ExecutionStatus(status).Should().Be(expected);

    [Theory]
    [InlineData("failed:timeout", "失败（执行超时）")]
    [InlineData("failed", "失败")]
    [InlineData("skipped:quota", "已跳过（超出当日额度）")]
    [InlineData("suggested", "已产出建议")]
    [InlineData("created", "已创建技能")]
    public void ReflectionOutcomeText_ShouldExpandInternalShape(string outcome, string expected)
        => UiText.ReflectionOutcomeText(outcome).Should().Be(expected);

    [Fact]
    public void ReflectionOutcomeText_Null_ShouldBePlaceholder()
        => UiText.ReflectionOutcomeText(null).Should().Be(UiText.None);

    [Theory]
    [InlineData(ReflectionTriggerKind.TurnNudge, "轮次")]
    [InlineData(ReflectionTriggerKind.Idle, "空闲")]
    [InlineData(ReflectionTriggerKind.Manual, "手动")]
    public void ReflectionTriggerText_ShouldMapToChinese(ReflectionTriggerKind kind, string expected)
        => UiText.ReflectionTriggerText(kind).Should().Be(expected);

    // ===== 枚举名不出现 =====

    [Theory]
    [InlineData(ToolCategory.General)]
    [InlineData(ToolCategory.FileSystem)]
    [InlineData(ToolCategory.Network)]
    [InlineData(ToolCategory.Database)]
    [InlineData(ToolCategory.Computation)]
    [InlineData(ToolCategory.LlmInteraction)]
    [InlineData(ToolCategory.ExternalService)]
    public void ToolCategoryText_ShouldNeverReturnEnumName(ToolCategory category)
    {
        var text = UiText.ToolCategoryText(category);

        text.Should().NotBe(category.ToString());
        text.Should().MatchRegex("^[\\u4e00-\\u9fa5]+$");
    }

    [Fact]
    public void ToolCategoryText_StringOverload_ShouldMapKnownName()
        => UiText.ToolCategoryText("FileSystem").Should().Be("文件系统");

    [Fact]
    public void ToolCategoryText_StringOverload_UnknownName_ShouldFallBackRaw()
        => UiText.ToolCategoryText("Custom").Should().Be("Custom");

    [Theory]
    [InlineData(ConversationKind.Direct, "私聊")]
    [InlineData(ConversationKind.Group, "群聊")]
    [InlineData(ConversationKind.Guild, "频道")]
    public void ConversationKindText_ShouldMapToChinese(ConversationKind kind, string expected)
        => UiText.ConversationKindText(kind).Should().Be(expected);

    [Fact]
    public void ConversationKindText_ShouldNeverReturnEnumName()
    {
        foreach (var kind in Enum.GetValues<ConversationKind>())
            UiText.ConversationKindText(kind).Should().NotBe(kind.ToString());
    }

    [Theory]
    [InlineData(ModelType.Text, "文本")]
    [InlineData(ModelType.Embedding, "向量")]
    [InlineData(ModelType.Rerank, "重排")]
    [InlineData(ModelType.Decision, "决策")]
    [InlineData(ModelType.ImageGeneration, "图像生成")]
    [InlineData(ModelType.Video, "视频生成")]
    [InlineData(ModelType.Speech, "语音")]
    public void ModelTypeText_ShouldMapToChinese(ModelType type, string expected)
        => UiText.ModelTypeText(type).Should().Be(expected);

    [Fact]
    public void MemoryTypeText_ShouldUseReadableWording()
    {
        UiText.MemoryTypeText(MemoryType.Session).Should().Be("会话记忆");
        UiText.MemoryTypeText(MemoryType.Daily).Should().Be("每日整理");
        UiText.MemoryTypeText(MemoryType.Digest).Should().Be("长期摘要");
    }

    [Theory]
    [InlineData(PermissionEffect.Allow, "允许")]
    [InlineData(PermissionEffect.Deny, "拒绝")]
    [InlineData(PermissionEffect.Ask, "询问")]
    public void PermissionEffectText_ShouldMapToChinese(PermissionEffect effect, string expected)
        => UiText.PermissionEffectText(effect).Should().Be(expected);

    [Theory]
    [InlineData(PermissionKind.Tool, "工具")]
    [InlineData(PermissionKind.File, "文件")]
    [InlineData(PermissionKind.Shell, "命令")]
    [InlineData(PermissionKind.McpTool, "MCP 工具")]
    public void PermissionKindText_ShouldMapToChinese(PermissionKind kind, string expected)
        => UiText.PermissionKindText(kind).Should().Be(expected);

    [Theory]
    [InlineData("filesystem.read", "文件")]
    [InlineData("tool.execute", "工具")]
    [InlineData("shell.exec", "命令")]
    [InlineData("mcp.call", "MCP 工具")]
    public void PermissionKindCodeText_ShouldStripPayloadDetail(string payload, string expected)
        => UiText.PermissionKindCodeText(payload).Should().Be(expected);

    [Theory]
    [InlineData("low", "低风险")]
    [InlineData("medium", "中风险")]
    [InlineData("high", "高风险")]
    [InlineData("critical", "极高风险")]
    public void RiskLevelText_ShouldMapToChinese(string level, string expected)
        => UiText.RiskLevelText(level).Should().Be(expected);

    // ===== 单位与格式 =====

    [Fact]
    public void Duration_ShouldUseChineseUnits()
    {
        UiText.Duration(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5)).Should().Be("2 小时 5 分");
        UiText.Duration(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(3)).Should().Be("12 分 3 秒");
        UiText.Duration(TimeSpan.FromSeconds(30)).Should().Be("30 秒");
        UiText.Duration(TimeSpan.FromMilliseconds(250)).Should().Be("250 毫秒");
    }

    [Fact]
    public void Duration_ShouldNeverEmitLatinUnitSuffix()
    {
        var text = UiText.Duration(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7));

        text.Should().NotMatchRegex("\\d\\s*(ms|s|m|h)$");
    }

    [Fact]
    public void FileSize_ShouldUseBinaryUnits()
    {
        UiText.FileSize(512).Should().Be("512 字节");
        UiText.FileSize(2048).Should().Be("2 KB");
        UiText.FileSize(5 * 1024 * 1024).Should().Be("5 MB");
    }
}
