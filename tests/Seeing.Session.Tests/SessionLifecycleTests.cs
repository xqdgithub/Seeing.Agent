using System.Threading.Tasks;
using Seeing.Session.Core;
using Xunit;
using Moq;

namespace Seeing.Session.Tests
{
    public class SessionLifecycleTests
    {
        [Fact]
        public async Task BeginSessionAsync_should_return_session_data()
        {
            var mock = new Mock<ISessionLifecycle>();
            mock.Setup(m => m.BeginSessionAsync(It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(new SessionData { Id = "ses_begin" });

            var result = await mock.Object.BeginSessionAsync("Test Session", "agent-1");
            Assert.NotNull(result);
            Assert.Equal("ses_begin", result!.Id);
        }

        [Fact]
        public async Task CloneSessionAsync_should_return_new_session_data()
        {
            var mock = new Mock<ISessionLifecycle>();
            mock.Setup(m => m.CloneSessionAsync("ses_1", It.IsAny<string?>()))
                .ReturnsAsync(new SessionData { Id = "ses_2", Title = "Cloned" });

            var result = await mock.Object.CloneSessionAsync("ses_1", "Cloned Session");
            Assert.NotNull(result);
            Assert.Equal("ses_2", result!.Id);
        }

        [Fact]
        public async Task EndSessionAsync_should_complete_task()
        {
            var mock = new Mock<ISessionLifecycle>();
            mock.Setup(m => m.EndSessionAsync("ses_1"))
                .Returns(Task.CompletedTask);

            await mock.Object.EndSessionAsync("ses_1");
            mock.Verify(m => m.EndSessionAsync("ses_1"), Times.Once);
        }
    }
}
