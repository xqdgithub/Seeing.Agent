namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 资源标识符 - 统一的资源命名格式
/// </summary>
/// <remarks>
/// 格式: kind:[namespace:]name
/// 示例: tool:bash, mcp:filesystem:read_file, agent:oracle
/// </remarks>
public readonly struct ResourceIdentifier : IEquatable<ResourceIdentifier>
{
    /// <summary>资源类型</summary>
    public PermissionKind Kind { get; }

    /// <summary>资源命名空间（可选）</summary>
    public string Namespace { get; }

    /// <summary>资源名称</summary>
    public string Name { get; }

    /// <summary>
    /// 创建资源标识符
    /// </summary>
    /// <param name="kind">资源类型</param>
    /// <param name="name">资源名称</param>
    /// <param name="ns">命名空间（可选）</param>
    public ResourceIdentifier(PermissionKind kind, string name, string? ns = null)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Namespace = ns ?? string.Empty;
    }

    /// <summary>
    /// 解析资源标识符字符串
    /// </summary>
    /// <param name="identifier">格式: kind:name 或 kind:namespace:name</param>
    /// <returns>解析后的资源标识符</returns>
    /// <exception cref="ArgumentNullException">identifier 为空</exception>
    /// <exception cref="FormatException">格式不正确</exception>
    public static ResourceIdentifier Parse(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentNullException(nameof(identifier));

        var parts = identifier.Split(':');

        return parts.Length switch
        {
            2 => new ResourceIdentifier(
                Enum.Parse<PermissionKind>(parts[0], ignoreCase: true),
                parts[1]),
            3 => new ResourceIdentifier(
                Enum.Parse<PermissionKind>(parts[0], ignoreCase: true),
                parts[2],
                parts[1]),
            _ => throw new FormatException(
                $"Invalid resource identifier format: {identifier}. " +
                $"Expected 'kind:name' or 'kind:namespace:name'")
        };
    }

    /// <summary>
    /// 尝试解析资源标识符字符串
    /// </summary>
    /// <param name="identifier">格式: kind:name 或 kind:namespace:name</param>
    /// <param name="result">解析结果</param>
    /// <returns>是否解析成功</returns>
    public static bool TryParse(string identifier, out ResourceIdentifier result)
    {
        try
        {
            result = Parse(identifier);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    /// <summary>
    /// 转换为规范字符串表示
    /// </summary>
    /// <returns>kind:name 或 kind:namespace:name 格式</returns>
    public string ToCanonicalString() =>
        string.IsNullOrEmpty(Namespace)
            ? $"{Kind.ToString().ToLowerInvariant()}:{Name}"
            : $"{Kind.ToString().ToLowerInvariant()}:{Namespace}:{Name}";

    #region IEquatable

    /// <inheritdoc/>
    public bool Equals(ResourceIdentifier other) =>
        Kind == other.Kind && Namespace == other.Namespace && Name == other.Name;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ResourceIdentifier other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Namespace, Name);

    /// <summary>相等比较</summary>
    public static bool operator ==(ResourceIdentifier left, ResourceIdentifier right) => left.Equals(right);

    /// <summary>不等比较</summary>
    public static bool operator !=(ResourceIdentifier left, ResourceIdentifier right) => !left.Equals(right);

    #endregion

    /// <inheritdoc/>
    public override string ToString() => ToCanonicalString();

    /// <summary>隐式转换为字符串</summary>
    public static implicit operator string(ResourceIdentifier identifier) => identifier.ToCanonicalString();
}
