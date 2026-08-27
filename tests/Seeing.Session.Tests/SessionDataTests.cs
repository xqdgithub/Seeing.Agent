using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests
{
    /// <summary>
    /// SessionData 单元测试：验证 SessionData 是否可替代 SessionEntry 的消息操作
    /// </summary>
    public class SessionDataTests
    {
        [Fact]
        public void AddMessage_ShouldAddMessageToMessages()
        {
            // Arrange
            var data = SessionData.Create();
            var msg = SessionMessage.UserMessage("Hello");

            // Act
            data.AddMessage(msg);

            // Assert
            data.Messages.Should().HaveCount(1);
            data.Messages[0].Content.Should().Be("Hello");
            data.Messages[0].Role.Should().Be(MessageRole.User);
        }

        [Fact]
        public void GetMessages_ShouldReturnAllMessagesInOrder()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.SystemMessage("Sys"));
            data.AddMessage(SessionMessage.UserMessage("User"));
            data.AddMessage(SessionMessage.AssistantMessage("Assistant"));

            // Act
            var messages = data.Messages;

            // Assert
            messages.Should().HaveCount(3);
            messages[0].Role.Should().Be(MessageRole.System);
            messages[1].Role.Should().Be(MessageRole.User);
            messages[2].Role.Should().Be(MessageRole.Assistant);
        }

        [Fact]
        public void ClearMessages_ShouldClearAllMessages()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("One"));
            data.AddMessage(SessionMessage.UserMessage("Two"));
            data.Metadata[SessionMetadataKeys.InstructionFingerprints] =
                """{"cwd":"/repo","files":{"/repo/AGENTS.md":"sha256:abc"}}""";

            // Act
            data.ClearMessages();

            // Assert
            data.Messages.Should().BeEmpty();
            data.Metadata.Should().NotContainKey(SessionMetadataKeys.InstructionFingerprints);
        }

        [Fact]
        public void Clone_ShouldBeIndependentCopy()
        {
            // Arrange
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.SystemMessage("Sys"));
            data.SetContext("flag", 1);

            var clone = data.Clone();

            // Act: mutate original after clone
            data.AddMessage(SessionMessage.UserMessage("New"));
            data.SetContext("flag", 2);
            data.Context["extra"] = true;

            // Assert: clone should remain unaffected by subsequent mutations to original
            clone.Messages.Should().HaveCount(1);
            clone.Messages[0].Content.Should().Be("Sys");
            clone.GetContext<int>("flag").Should().Be(1);
            clone.Context.Should().NotContainKey("extra");
            data.Messages.Should().HaveCount(2);
        }

        [Fact]
        public void Clone_CopiesChannelIdAndUserId()
        {
            var s = SessionData.Create();
            s.ChannelId = "qq";
            s.UserId = "u1";
            var c = s.Clone();
            c.ChannelId.Should().Be("qq");
            c.UserId.Should().Be("u1");
        }

        [Fact]
        public void AutoApprove_ShouldDefaultToFollowGlobal()
        {
            SessionData.Create().AutoApprove.Should().Be(SessionAutoApprove.FollowGlobal);
        }

        [Fact]
        public void Clone_CopiesAutoApprove()
        {
            var s = SessionData.Create();
            s.AutoApprove = SessionAutoApprove.Enabled;
            var c = s.Clone();
            c.AutoApprove.Should().Be(SessionAutoApprove.Enabled);
        }

        [Fact]
        public void GetActiveMessages_WhenNoSummary_ShouldReturnAll()
        {
            // 未压缩过：全部消息均为活跃
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("问题一"));
            data.AddMessage(SessionMessage.AssistantMessage("回答一"));
            data.AddMessage(SessionMessage.UserMessage("问题二"));

            var active = data.GetActiveMessages();

            active.Should().HaveCount(3);
            active[0].Content.Should().Be("问题一");
            active[2].Content.Should().Be("问题二");
        }

        [Fact]
        public void GetActiveMessages_WithSummary_ShouldReturnSummaryAndAfter()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("问题一"));
            data.AddMessage(SessionMessage.AssistantMessage("回答一"));
            var summary = SessionMessage.AssistantMessage("摘要");
            summary.IsSummary = true;
            data.AddMessage(summary);
            data.AddMessage(SessionMessage.UserMessage("问题二"));

            var active = data.GetActiveMessages();

            active.Should().HaveCount(2, "活跃 = 摘要消息 + 其后消息");
            active[0].Content.Should().Be("摘要");
            active[1].Content.Should().Be("问题二");
        }

        [Fact]
        public void GetActiveMessages_WhenSummaryCutoff_ShouldNotLeakHistory()
        {
            // 摘要之前的历史消息（无论何种原因）一律不活跃：位置约束即压缩真相，无需标记
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("问题一"));
            data.AddMessage(SessionMessage.AssistantMessage("回答一"));
            var summary = SessionMessage.AssistantMessage("摘要");
            summary.IsSummary = true;
            data.AddMessage(summary);
            data.AddMessage(SessionMessage.UserMessage("问题二"));

            var active = data.GetActiveMessages();

            active.Should().HaveCount(2, "摘要之前的消息不应泄漏给 LLM");
            active[0].Content.Should().Be("摘要");
            active[1].Content.Should().Be("问题二");
        }

        [Fact]
        public void GetActiveMessages_MultipleSummaries_ShouldOnlyKeepLastSummaryOnward()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("问题一"));
            var s1 = SessionMessage.AssistantMessage("摘要一");
            s1.IsSummary = true;
            data.AddMessage(s1);
            data.AddMessage(SessionMessage.UserMessage("问题二"));
            var s2 = SessionMessage.AssistantMessage("摘要二");
            s2.IsSummary = true;
            data.AddMessage(s2);
            data.AddMessage(SessionMessage.UserMessage("问题三"));

            var active = data.GetActiveMessages();

            active.Should().HaveCount(2, "多次压缩只保留最后一个摘要及之后的消息");
            active[0].Content.Should().Be("摘要二");
            active[1].Content.Should().Be("问题三");
        }

        [Fact]
        public void GetActiveMessages_WhenSummaryRemoved_ShouldReturnAllAgain()
        {
            // 先建立压缩真相（摘要消息）→ 活跃列表应被截断；摘要被移除（如回退操作）后：
            // 压缩真相消失，全部消息重新活跃
            var data = SessionData.Create();
            var summary = SessionMessage.AssistantMessage("摘要");
            summary.IsSummary = true;
            data.AddMessage(summary);
            data.AddMessage(SessionMessage.UserMessage("问题一"));
            data.AddMessage(SessionMessage.AssistantMessage("回答一"));

            data.GetActiveMessages().Should().HaveCount(3, "摘要存在时从摘要位置起算");

            data.RemoveMessage(data.Messages[0]);

            var active = data.GetActiveMessages();

            active.Should().HaveCount(2, "摘要移除后压缩真相消失，全部消息重新活跃");
            active[0].Content.Should().Be("问题一");
            active[1].Content.Should().Be("回答一");
        }

        [Fact]
        public void Clone_CopiesSummaryMarker()
        {
            var s = SessionData.Create();
            var summary = SessionMessage.AssistantMessage("摘要");
            summary.IsSummary = true;
            s.AddMessage(summary);

            var c = s.Clone();

            c.Messages[0].IsSummary.Should().BeTrue();
        }

        [Fact]
        public void GetActiveMessages_WhenConcurrentlyAppending_ShouldNotThrow()
        {
            // 快照拷贝（List.CopyTo 快速路径）保证并发追加时读侧不抛异常：
            // TokenBudget 估算与事件管道在后台执行与消息追加并发进行
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("问题一"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var appender = Task.Run(() =>
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    data.AddMessage(SessionMessage.AssistantMessage($"回答 {i++}"));
                    if (i > 200) break;
                }
            });
            var reader = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var active = data.GetActiveMessages();
                    active.Count.Should().BeGreaterThanOrEqualTo(1);
                }
            });

            Task.WaitAll(appender, reader);

            data.GetActiveMessages().Should().NotBeEmpty();
        }

        // === SessionId 归属 ===

        [Fact]
        public void AddMessage_ShouldAssignSessionId()
        {
            var data = SessionData.Create();
            var msg = SessionMessage.UserMessage("hi");

            data.AddMessage(msg);

            msg.SessionId.Should().Be(data.Id);
        }

        [Fact]
        public void AddMessage_ShouldNotOverrideExistingSessionId()
        {
            var data = SessionData.Create();
            var msg = SessionMessage.UserMessage("hi");
            msg.SessionId = "other-session";

            data.AddMessage(msg);

            msg.SessionId.Should().Be("other-session");
        }

        [Fact]
        public void AddMessages_ShouldAssignSessionId_ForAll()
        {
            var data = SessionData.Create();
            data.AddMessages(new[]
            {
                SessionMessage.UserMessage("a"),
                SessionMessage.UserMessage("b")
            });

            data.Messages.Should().HaveCount(2);
            data.Messages.All(m => m.SessionId == data.Id).Should().BeTrue();
        }

        [Fact]
        public void InsertMessage_ShouldAssignSessionId()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("a"));
            var msg = SessionMessage.UserMessage("b");

            data.InsertMessage(0, msg);

            msg.SessionId.Should().Be(data.Id);
            data.Messages[0].Content.Should().Be("b");
        }

        [Fact]
        public void ReplaceMessages_ShouldRewriteAllSessionIds()
        {
            var source = SessionData.Create();
            source.AddMessage(SessionMessage.UserMessage("a"));
            var clone = source.Messages[0].Clone();
            clone.SessionId.Should().Be(source.Id);

            var target = SessionData.Create();
            target.ReplaceMessages(new[] { clone });

            target.Messages.Should().ContainSingle();
            target.Messages[0].SessionId.Should().Be(target.Id, "ReplaceMessages 全部改写为当前会话 Id");
        }

        [Fact]
        public void ReplaceMessages_WithLazySequenceFromSelf_ShouldNotClearAll()
        {
            // 回归：回滚场景传入基于自身 Messages 的惰性序列（session.Messages.Take(n)），
            // ReplaceMessages 必须先物化再清空，否则源被清空后序列枚举为空导致全部消息丢失
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("a"));
            data.AddMessage(SessionMessage.UserMessage("b"));
            data.AddMessage(SessionMessage.UserMessage("c"));

            data.ReplaceMessages(data.Messages.Take(2));

            data.Messages.Select(m => m.Content).Should().Equal("a", "b");
            data.Messages.Should().HaveCount(2, "回滚应保留前 2 条而非清空全部");
        }

        [Fact]
        public void RemoveLastMessage_ShouldRemoveLastMatchingFromTail()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("a"));
            data.AddMessage(SessionMessage.UserMessage("b"));
            data.AddMessage(SessionMessage.UserMessage("a"));

            var removed = data.RemoveLastMessage(m => m.Content == "a");

            removed.Should().BeTrue();
            data.Messages.Select(m => m.Content).Should().Equal("a", "b");
        }

        [Fact]
        public void RemoveLastMessage_NoMatch_ShouldReturnFalse()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("a"));

            data.RemoveLastMessage(m => m.Content == "zzz").Should().BeFalse();
            data.Messages.Should().HaveCount(1);
        }

        [Fact]
        public void Clone_ShouldPreserveMessageSessionId()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("hi"));

            var clone = data.Clone();

            clone.Messages[0].SessionId.Should().Be(data.Id);
        }

        [Fact]
        public void SerializeRoundTrip_ShouldPreserveSessionId()
        {
            var data = SessionData.Create();
            data.AddMessage(SessionMessage.UserMessage("hi"));

            var json = JsonSerializer.Serialize(data);
            var round = JsonSerializer.Deserialize<SessionData>(json);

            round!.Messages[0].SessionId.Should().Be(data.Id);
        }

        [Fact]
        public void DeserializeLegacyJson_WithoutSessionId_ShouldBeNull()
        {
            // 模拟旧会话文件（CamelCase + CaseInsensitive，与 FileSessionStore 一致）
            var json = """{"id":"ses_x","title":"T","messages":[{"id":"m1","role":"user","content":"hi"}]}""";
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            var data = JsonSerializer.Deserialize<SessionData>(json, options);

            data!.Messages.Should().ContainSingle();
            data.Messages[0].SessionId.Should().BeNull();
        }
    }
}
