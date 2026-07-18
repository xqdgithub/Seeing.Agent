using Seeing.Agent.Configuration;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Memory 配置读写（固定节名 Memory），基于核心 <see cref="IConfigSectionStore"/>。
/// </summary>
public interface IMemoryOptionsStore
{
    MemoryOptions Get();
    Task SaveAsync(MemoryOptions options, CancellationToken ct = default);
}
