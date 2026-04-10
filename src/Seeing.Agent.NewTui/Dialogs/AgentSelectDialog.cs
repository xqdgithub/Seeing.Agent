using Terminal.Gui;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.Core;

namespace Seeing.Agent.NewTui.Dialogs;

public class AgentSelectDialog : Dialog
{
    public string? SelectedAgent { get; private set; }
    
    public AgentSelectDialog(AppState state, AgentRegistry registry) : base("Select Agent", 60, 20)
    {
        var listView = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2
        };
        
        // 简单实现 - 使用当前 Agent 作为默认
        var agents = new List<string> { "build", "explore", "plan" };
        listView.SetSource(agents);
        
        listView.OpenSelectedItem += (args) =>
        {
            SelectedAgent = agents[args.Item];
            Application.RequestStop();
        };
        
        Add(listView);
        
        var closeBtn = new Button("Close")
        {
            X = Pos.Center(),
            Y = Pos.Bottom(listView)
        };
        closeBtn.Clicked += () => Application.RequestStop();
        Add(closeBtn);
    }
}
