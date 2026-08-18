using Seeing.Agent.Abstractions.Llm;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm;

/// <summary>
/// 聊天角色
/// </summary>
public static class ChatRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}