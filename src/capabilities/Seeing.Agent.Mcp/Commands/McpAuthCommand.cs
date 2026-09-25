using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Mcp.OAuth;

namespace Seeing.Agent.Mcp.Commands;

/// <summary>
/// <c>/mcp-auth &lt;server&gt;</c> — 为指定 MCP 服务器发起 OAuth 授权（授权码 + PKCE）。
/// </summary>
public sealed class McpAuthCommand : ICommand
{
    private readonly IMcpOAuthAuthorizer _authorizer;

    public McpAuthCommand(IMcpOAuthAuthorizer authorizer) => _authorizer = authorizer;

    /// <inheritdoc />
    public CommandMetadata Metadata { get; } = new()
    {
        Name = "mcp-auth",
        Description = "为 MCP 服务器发起 OAuth 授权",
        Usage = "mcp-auth <server>",
        Category = CommandCategory.Tools,
        Examples = ["/mcp-auth github"],
        Source = "mcp"
    };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        var server = context.Arguments?.Trim();
        if (string.IsNullOrWhiteSpace(server))
            return CommandResult.Fail("请指定 MCP 服务器名称", $"用法: {Metadata.Usage}");

        var result = await _authorizer.AuthorizeAsync(server, cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return CommandResult.Ok($"MCP 服务器 {server} OAuth 授权成功");

        var error = result.Error ?? $"MCP 服务器 {server} 授权失败";
        return CommandResult.Fail(error, $"授权失败: {error}");
    }
}
