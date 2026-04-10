using FluentAssertions;
using Seeing.Agent.Core.Sessions;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Sessions
{
    /// <summary>
    /// SessionCompressor 单元测试
    /// </summary>
    public class SessionCompressorTests
    {
        [Fact]
        public async Task CompressAsync_ShouldRetainFirstAndLastMessages()
        {
            // Arrange
            var compressor = new SessionCompressor(keepLastN: 3);
            var messages = CreateMessages(25); // 25 条消息
            
            // Act
            var result = await compressor.CompressAsync(messages);
            
            // Assert
            // 应保留：第1条 + 最后3条 = 4条
            result.Count.Should().Be(4);
            
            // 第一条消息应保留（系统提示）
            result[0].Content.Should().Be("msg_0");
            
            // 最后3条消息应保留
            result[1].Content.Should().Be("msg_22");
            result[2].Content.Should().Be("msg_23");
            result[3].Content.Should().Be("msg_24");
        }

        [Fact]
        public async Task CompressAsync_ShouldNotCompress_WhenMessagesLessThanThreshold()
        {
            // Arrange
            var compressor = new SessionCompressor(keepLastN: 20);
            var messages = CreateMessages(15); // 15 条消息（少于 21 = 1 + 20）
            
            // Act
            var result = await compressor.CompressAsync(messages);
            
            // Assert
            // 消息数量不足以压缩，应全部保留
            result.Count.Should().Be(15);
            result.Should().BeEquivalentTo(messages);
        }

        [Fact]
        public async Task CompressAsync_ShouldRespectCustomKeepLastN()
        {
            // Arrange
            var compressor = new SessionCompressor(keepLastN: 5);
            var messages = CreateMessages(30); // 30 条消息
            
            // Act
            var result = await compressor.CompressAsync(messages);
            
            // Assert
            // 应保留：第1条 + 最后5条 = 6条
            result.Count.Should().Be(6);
            
            // 第一条消息应保留
            result[0].Content.Should().Be("msg_0");
            
            // 最后5条消息应保留
            result[1].Content.Should().Be("msg_25");
            result[2].Content.Should().Be("msg_26");
            result[3].Content.Should().Be("msg_27");
            result[4].Content.Should().Be("msg_28");
            result[5].Content.Should().Be("msg_29");
        }

        [Fact]
        public async Task CompressAsync_ShouldReturnEmptyList_WhenInputIsEmpty()
        {
            // Arrange
            var compressor = new SessionCompressor();
            var messages = new List<ChatMessage>();
            
            // Act
            var result = await compressor.CompressAsync(messages);
            
            // Assert
            result.Count.Should().Be(0);
        }

        /// <summary>
        /// 创建测试消息列表
        /// </summary>
        private static IReadOnlyList<ChatMessage> CreateMessages(int count)
        {
            var messages = new List<ChatMessage>();
            for (var i = 0; i < count; i++)
            {
                messages.Add(new ChatMessage
                {
                    Role = i == 0 ? ChatRole.System : ChatRole.User,
                    Content = $"msg_{i}"
                });
            }
            return messages;
        }
    }
}