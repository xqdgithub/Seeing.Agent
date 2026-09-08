using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Memory 配置读写（固定节名 Memory），基于 <see cref="IConfigSectionStore"/>。
/// </summary>
public interface IMemoryOptionsStore
{
    MemoryOptions Get();
    Task SaveAsync(MemoryOptions options, CancellationToken ct = default);
}
