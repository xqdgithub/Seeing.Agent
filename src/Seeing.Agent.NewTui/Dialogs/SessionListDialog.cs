using Terminal.Gui;
using Seeing.Agent.NewTui.State;

namespace Seeing.Agent.NewTui.Dialogs;

public class SessionListDialog : Dialog
{
    public string? SelectedSessionId { get; private set; }
    
    public SessionListDialog(AppState state) : base("Sessions", 60, 20)
    {
        var listView = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2,
            AllowsMarking = false
        };
        
        var sessions = new List<string> { "New Session" };
        if (state.CurrentSession != null)
        {
            sessions.Add($"Current: {state.CurrentSession.SessionId}");
        }
        listView.SetSource(sessions);
        
        listView.OpenSelectedItem += (args) =>
        {
            if (args.Item == 0)
            {
                SelectedSessionId = null; // New session
            }
            else
            {
                SelectedSessionId = state.CurrentSession?.SessionId;
            }
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
