using Seeing.Agent.Abstractions.Mcp;
namespace Seeing.Agent.Mcp.Management;

using System.Text.Json;

public class McpToolInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonElement ParametersSchema { get; set; }
}