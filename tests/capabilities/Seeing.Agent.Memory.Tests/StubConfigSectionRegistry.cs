using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// 内存版 <see cref="IConfigSectionRegistry"/>，供 Memory 测试共用。
/// <para>先前在 <c>CostLedgerRemovalTests</c> / <c>MemoryToolRegistrationTests</c> /
/// <c>MemoryOptionsDeserializationTests</c> 各有一份拷贝，抽到此处统一维护。</para>
/// </summary>
internal sealed class StubConfigSectionRegistry : IConfigSectionRegistry
{
    private readonly Dictionary<string, ConfigSectionMeta> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(ConfigSectionMeta meta) => _sections[meta.Key] = meta;

    public IReadOnlyCollection<ConfigSectionMeta> Sections => _sections.Values;

    public bool TryGet(string key, out ConfigSectionMeta meta) =>
        _sections.TryGetValue(key, out meta!);
}
