using System;
using System.Collections.Generic;
using Terminal.Gui;
using Seeing.Agent.Llm;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.NewTui.Components;

public class MessageList : View
{
    private readonly AppState _state;
    private ListView _listView;
    
    public MessageList(AppState state)
    {
        _state = state;
        
        _listView = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        
        Add(_listView);
        
        _state.StateChanged += Refresh;
    }
    
    private void Refresh()
    {
        var items = new List<string>();
        
        if (_state.CurrentSession != null)
        {
            foreach (var msg in _state.CurrentSession.Messages)
            {
                items.Add(RenderMessage(msg));
            }
        }
        
        if (_state.IsProcessing && _state.StreamingContent.Length > 0)
        {
            items.Add($"[Agent]: {_state.StreamingContent}");
        }
        
        foreach (var tool in _state.ActiveToolCalls)
        {
            items.Add(RenderToolCall(tool));
        }
        
        if (_state.LastError != null)
        {
            items.Add($"[Error]: {_state.LastError}");
        }
        
        _listView.SetSource(items);
    }
    
    private string RenderMessage(ChatMessage msg)
    {
        var prefix = msg.Role == ChatRole.User ? "You" : "Agent";
        var content = msg.Content ?? "";
        if (content.Length > 200)
            content = content.Substring(0, 200) + "...";
        
        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
        {
            content += $" [{msg.ToolCalls.Count} tool calls]";
        }
        
        return $"[{prefix}]: {content}";
    }
    
    private string RenderToolCall(ToolCallDisplay tool)
    {
        var statusIcon = tool.Status switch
        {
            ToolCallStatus.Pending => "⏳",
            ToolCallStatus.Running => "⚙️",
            ToolCallStatus.Success => "✅",
            ToolCallStatus.Failed => "❌",
            ToolCallStatus.Rejected => "🚫",
            _ => "?"
        };
        
        var line = $"{statusIcon} {tool.Name}";
        
        if (tool.Duration != null)
            line += $" ({tool.Duration.Value.TotalSeconds:F1}s)";
        
        if (tool.Error != null)
            line += $" Error: {Truncate(tool.Error, 50)}";
        
        return line;
    }
    
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length > max ? text.Substring(0, max) + "..." : text;
    }
}
