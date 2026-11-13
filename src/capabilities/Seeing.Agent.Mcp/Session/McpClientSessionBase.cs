using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Session;

/// <summary>
/// 基于 SDK <see cref="McpClient"/> 的会话公共实现：连接后缓存 capabilities，并按能力门控 Resources/Prompts/Skills。
/// </summary>
internal abstract class McpClientSessionBase : IMcpSession
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private McpServerCapabilitiesInfo _capabilities = new(
        false, false, false, false, false, false, false, false);

    protected McpClientSessionBase(McpServerConfig config, ILogger logger)
    {
        Config = config;
        Logger = logger;
    }

    protected McpServerConfig Config { get; }
    protected ILogger Logger { get; }
    protected McpClient? Client { get; private set; }

    public string ServerName => Config.Name;
    public McpServerCapabilitiesInfo Capabilities => _capabilities;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        _capabilities = McpSessionCapabilitiesMapper.FromSdk(Client?.ServerCapabilities);
        Logger.LogInformation(
            "MCP Server {Name} capabilities: tools={Tools} resources={Resources} prompts={Prompts} skills={Skills}",
            Config.Name, _capabilities.Tools, _capabilities.Resources, _capabilities.Prompts, _capabilities.SkillsExtension);
    }

    protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);

    protected void SetClient(McpClient? client) => Client = client;

    public virtual async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Client is null)
            return;

        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            Logger.LogInformation("MCP Server {Name} 已断开连接", Config.Name);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "断开 MCP Server {Name} 连接时发生错误", Config.Name);
        }
        finally
        {
            Client = null;
            _capabilities = new McpServerCapabilitiesInfo(
                false, false, false, false, false, false, false, false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    public async Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Client is null)
        {
            Logger.LogError("MCP 客户端未连接，无法获取工具列表");
            return Array.Empty<McpSessionToolInfo>();
        }

        try
        {
            var tools = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = new List<McpSessionToolInfo>();
            foreach (var tool in tools)
            {
                result.Add(new McpSessionToolInfo(
                    tool.Name,
                    tool.Description,
                    tool.JsonSchema.ValueKind == JsonValueKind.Undefined
                        ? null
                        : tool.JsonSchema.GetRawText()));
                Logger.LogDebug("发现 MCP 工具: {Name} - {Description}", tool.Name, tool.Description);
            }

            Logger.LogInformation("从 MCP Server {Name} 获取到 {Count} 个工具", Config.Name, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "获取 MCP Server {Name} 工具列表失败", Config.Name);
            throw;
        }
    }

    public virtual async Task<McpSessionToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (Client is null)
        {
            Logger.LogError("MCP 客户端未连接，无法调用工具: {ToolName}", toolName);
            return new McpSessionToolCallResult(true, "MCP 客户端未连接", "MCP 客户端未连接");
        }

        try
        {
            Logger.LogDebug("调用 MCP 工具: {ServerName}.{ToolName}", Config.Name, toolName);
            var dict = arguments as Dictionary<string, object?>
                       ?? new Dictionary<string, object?>(arguments);
            var result = await Client.CallToolAsync(toolName, dict, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return McpSessionToolResultBuilder.FromCallToolResult(result);
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "调用 MCP 工具 {ServerName}.{ToolName} 失败", Config.Name, toolName);
            return new McpSessionToolCallResult(true, $"工具调用异常: {ex.Message}", ex.Message);
        }
    }

    public async Task<McpResourceListResult> ListResourcesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Resources)
            return McpResourceListResult.Unsupported("server 未声明 resources 能力");
        if (Client is null)
            return new McpResourceListResult(Array.Empty<McpResourceInfo>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var page = await Client.ListResourcesAsync(
                new ListResourcesRequestParams { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            var items = page.Resources?
                .Select(r => new McpResourceInfo(r.Uri, r.Name, r.Description, r.MimeType))
                .ToList()
                ?? new List<McpResourceInfo>();
            return new McpResourceListResult(items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} resources/list 失败", Config.Name);
            return new McpResourceListResult(Array.Empty<McpResourceInfo>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpResourceReadResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Resources)
            return McpResourceReadResult.Unsupported("server 未声明 resources 能力");
        if (Client is null)
            return new McpResourceReadResult(Array.Empty<McpResourceContent>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var result = await Client.ReadResourceAsync(uri, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var contents = new List<McpResourceContent>();
            foreach (var c in result.Contents ?? Array.Empty<ResourceContents>())
            {
                contents.Add(c switch
                {
                    TextResourceContents text => new McpResourceContent(text.Uri, text.MimeType, text.Text, null),
                    BlobResourceContents blob => new McpResourceContent(
                        blob.Uri,
                        blob.MimeType,
                        Text: null,
                        BlobBase64: Convert.ToBase64String(blob.Blob.Span)),
                    _ => new McpResourceContent(c.Uri, c.MimeType)
                });
            }

            return new McpResourceReadResult(contents);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} resources/read 失败: {Uri}", Config.Name, uri);
            return new McpResourceReadResult(Array.Empty<McpResourceContent>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Resources)
            return McpResourceTemplateListResult.Unsupported("server 未声明 resources 能力");
        if (Client is null)
            return new McpResourceTemplateListResult(
                Array.Empty<McpResourceTemplateInfo>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var page = await Client.ListResourceTemplatesAsync(
                new ListResourceTemplatesRequestParams { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            var items = page.ResourceTemplates?
                .Select(t => new McpResourceTemplateInfo(t.UriTemplate, t.Name, t.Description, t.MimeType))
                .ToList()
                ?? new List<McpResourceTemplateInfo>();
            return new McpResourceTemplateListResult(items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} resources/templates/list 失败", Config.Name);
            return new McpResourceTemplateListResult(
                Array.Empty<McpResourceTemplateInfo>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpPromptListResult> ListPromptsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Prompts)
            return McpPromptListResult.Unsupported("server 未声明 prompts 能力");
        if (Client is null)
            return new McpPromptListResult(Array.Empty<McpPromptInfo>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var page = await Client.ListPromptsAsync(
                new ListPromptsRequestParams { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            var items = page.Prompts?
                .Select(p => new McpPromptInfo(
                    p.Name,
                    p.Description,
                    p.Arguments?.Select(a => new McpPromptArgumentInfo(
                        a.Name, a.Description, a.Required == true)).ToList()))
                .ToList()
                ?? new List<McpPromptInfo>();
            return new McpPromptListResult(items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} prompts/list 失败", Config.Name);
            return new McpPromptListResult(Array.Empty<McpPromptInfo>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpPromptGetResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Prompts)
            return McpPromptGetResult.Unsupported("server 未声明 prompts 能力");
        if (Client is null)
            return new McpPromptGetResult(Array.Empty<McpPromptMessage>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var result = await Client.GetPromptAsync(name, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var messages = new List<McpPromptMessage>();
            foreach (var msg in result.Messages ?? Array.Empty<PromptMessage>())
            {
                var role = msg.Role.ToString().ToLowerInvariant();
                var text = FormatPromptContent(msg.Content);
                messages.Add(new McpPromptMessage(role, text));
            }

            return new McpPromptGetResult(messages, result.Description);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} prompts/get 失败: {Prompt}", Config.Name, name);
            return new McpPromptGetResult(Array.Empty<McpPromptMessage>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpSkillExtListResult> ListSkillsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SkillsExtension)
            return McpSkillExtListResult.Unsupported("server 未声明 skills 扩展");
        if (Client is null)
            return new McpSkillExtListResult(Array.Empty<McpSkillExtInfo>(), ErrorMessage: "MCP 客户端未连接");

        try
        {
            var parameters = cursor is null
                ? new SkillsListParams()
                : new SkillsListParams { Cursor = cursor };
            var page = await Client.SendRequestAsync<SkillsListParams, SkillsListResult>(
                "skills/list",
                parameters,
                s_jsonOptions,
                default,
                cancellationToken).ConfigureAwait(false);

            var items = page.Skills?
                .Select(s => new McpSkillExtInfo(s.Uri ?? "", s.Name ?? "", s.Description, s.FrontmatterJson))
                .ToList()
                ?? new List<McpSkillExtInfo>();
            return new McpSkillExtListResult(items, page.NextCursor);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} skills/list 失败", Config.Name);
            return new McpSkillExtListResult(Array.Empty<McpSkillExtInfo>(), ErrorMessage: ex.Message);
        }
    }

    public async Task<McpSkillExtGetResult> GetSkillAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SkillsExtension)
            return McpSkillExtGetResult.Unsupported("server 未声明 skills 扩展");
        if (Client is null)
            return new McpSkillExtGetResult(ErrorMessage: "MCP 客户端未连接");

        try
        {
            var page = await Client.SendRequestAsync<SkillsGetParams, SkillsGetResult>(
                "skills/get",
                new SkillsGetParams { Uri = uri },
                s_jsonOptions,
                default,
                cancellationToken).ConfigureAwait(false);

            return new McpSkillExtGetResult(
                Uri: page.Uri ?? uri,
                Name: page.Name,
                Description: page.Description,
                Content: page.Content,
                FrontmatterJson: page.FrontmatterJson,
                AttachmentUris: page.AttachmentUris);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MCP Server {Name} skills/get 失败: {Uri}", Config.Name, uri);
            return new McpSkillExtGetResult(ErrorMessage: ex.Message);
        }
    }

    private static string FormatPromptContent(ContentBlock? content)
    {
        if (content is null)
            return "";
        if (content is TextContentBlock text)
            return text.Text ?? "";
        if (content is EmbeddedResourceBlock embedded)
            return McpSessionToolResultBuilder.FormatEmbeddedResource(embedded.Resource?.Uri);
        if (content is ImageContentBlock image)
            return $"[Image: {image.MimeType}]";
        return $"[Content: {content.Type}]";
    }

    private sealed class SkillsListParams
    {
        [JsonPropertyName("cursor")]
        public string? Cursor { get; set; }
    }

    private sealed class SkillsListResult
    {
        [JsonPropertyName("skills")]
        public List<SkillsListItem>? Skills { get; set; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; set; }
    }

    private sealed class SkillsListItem
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("frontmatter")]
        public string? FrontmatterJson { get; set; }
    }

    private sealed class SkillsGetParams
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }
    }

    private sealed class SkillsGetResult
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("frontmatter")]
        public string? FrontmatterJson { get; set; }

        [JsonPropertyName("attachments")]
        public List<string>? AttachmentUris { get; set; }
    }
}
