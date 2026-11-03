using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Storage;
using Seeing.Agent.Memory.Extensions;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

/// <summary>
/// 记忆数据落盘位置约定。
/// <para>
/// Markdown 记忆文件与 SQLite 索引库必须在**同一个目录**（<c>~/.seeing/memory/</c>）：
/// 索引是文件的派生数据，分开放会导致「换工作区后索引变空、看起来记忆全丢」。
/// 旧版把 memory.db 放在 <c>&lt;工作区&gt;/.seeing/</c> 根下，与配置文件混在一起。
/// </para>
/// </summary>
public class MemoryStoragePathConventionTests
{
    [Fact]
    public void MemoryDb_Should_Live_In_Same_Directory_As_Markdown_Files()
    {
        // 文件存储的默认目录
        var fileDir = LocalFileStore.GetDefaultMemoryDirectory();

        // 索引库的默认目录（经 DI 解析真实连接串，避免复制实现细节）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeeingDirectories>(new FakeDirectories());

        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddMemoryServices(registry, connectionString: null);

        using var provider = services.BuildServiceProvider();
        var owner = provider.GetRequiredService<Seeing.Agent.Memory.Core.SqliteConnectionOwner>();
        var connection = owner.EnsureInstance();

        var dataSource = connection.DataSource;

        Path.GetDirectoryName(Path.GetFullPath(dataSource))
            .Should().Be(Path.GetFullPath(fileDir),
                "memory.db 必须与 Markdown 记忆文件同目录，否则换工作区会得到一份空索引");

        Path.GetFileName(dataSource).Should().Be("memory.db");
    }

    [Fact]
    public void Default_Memory_Directory_Should_Be_User_Level_Not_Workspace()
    {
        var dir = LocalFileStore.GetDefaultMemoryDirectory();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dir.Should().Be(Path.Combine(home, ".seeing", "memory"),
            "记忆文件固定在用户目录、不随工作区变化；索引必须跟随它");
    }

    private sealed class FakeDirectories : ISeeingDirectories
    {
        public string UserSeeingDirectory =>
            Path.Combine(Path.GetTempPath(), "mem-path-test-user", ".seeing");

        public string ProjectSeeingDirectory =>
            Path.Combine(Path.GetTempPath(), "mem-path-test-project", ".seeing");

        public string GetSeeingDirectory(ConfigLevel level) =>
            level == ConfigLevel.User ? UserSeeingDirectory : ProjectSeeingDirectory;
    }
}
