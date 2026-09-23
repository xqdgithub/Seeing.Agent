namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>业务门面：按默认 provider 转发 SystemOne 调用</summary>
public interface ISystemOneService
{
    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(string? providerId = null, CancellationToken cancellationToken = default);
}
