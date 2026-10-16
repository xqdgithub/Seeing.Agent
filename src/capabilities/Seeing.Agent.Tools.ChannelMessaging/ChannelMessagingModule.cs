using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 通道消息工具模块。缺省注册 <see cref="NoopChannelMessageSink"/>，已有 Sink 时不覆盖。
/// </summary>
public sealed class ChannelMessagingModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "send_channel_message",
        "list_channel_users",
        "list_channel_conversations",
    ];

    /// <inheritdoc />
    public string Id => "channel-messaging";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = [];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<IChannelMessageSink, NoopChannelMessageSink>();
        services.AddSingleton<SendMessageTool>();
        services.AddSingleton<ListChannelUsersTool>();
        services.AddSingleton<ListConversationsTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<SendMessageTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<ListChannelUsersTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<ListConversationsTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
