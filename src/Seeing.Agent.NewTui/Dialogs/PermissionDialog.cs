using Terminal.Gui;
using Seeing.Agent.Core.Interfaces;
using System.Text.Json;

namespace Seeing.Agent.NewTui.Dialogs;

public class PermissionDialog : Dialog
{
    public PermissionDialog(
        string toolName,
        object? arguments,
        Action<PermissionDecision> onComplete) 
        : base($"Permission: {toolName}", 60, 12)
    {
        var label = new Label($"Tool '{toolName}' wants to execute:")
        {
            X = Pos.Center(),
            Y = 1
        };
        Add(label);
        
        if (arguments != null)
        {
            var argsJson = JsonSerializer.Serialize(arguments, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            var argsView = new TextView()
            {
                X = 2,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 4,
                Text = argsJson
            };
            Add(argsView);
        }
        
        var allowBtn = new Button("Allow")
        {
            X = Pos.Center() - 12,
            Y = Pos.Bottom(this) - 3
        };
        allowBtn.Clicked += () =>
        {
            onComplete(PermissionDecision.Allow());
            Application.RequestStop();
        };
        Add(allowBtn);
        
        var denyBtn = new Button("Deny")
        {
            X = Pos.Center() + 2,
            Y = Pos.Bottom(this) - 3
        };
        denyBtn.Clicked += () =>
        {
            onComplete(PermissionDecision.Deny("User denied"));
            Application.RequestStop();
        };
        Add(denyBtn);
        
        var alwaysBtn = new Button("Always")
        {
            X = Pos.Center() - 30,
            Y = Pos.Bottom(this) - 3
        };
        alwaysBtn.Clicked += () =>
        {
            onComplete(PermissionDecision.Allow("Always allow this tool"));
            Application.RequestStop();
        };
        Add(alwaysBtn);
    }
}
